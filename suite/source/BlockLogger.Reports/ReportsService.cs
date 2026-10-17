using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BlockLogger.Services;
using Newfies.Storage;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace BlockLogger.Reports;

public sealed class ReportsService : IDisposable
{
	private const int SchemaVersion = 1;

	private const int MaxReports = 5000;

	private const int MaxReasonLength = 700;

	private const int MaxNoteLength = 1200;

	private const int MaxReporterCooldownEntries = 4096;

	private const int MaxNotesPerReport = 1000;

	private const int MaxEventsPerReport = 2000;

	private readonly ICoreServerAPI api;

	private readonly object gate = new object();

	private readonly Dictionary<string, long> lastReportByReporter = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

	private readonly string statePath;

	private readonly DeferredJsonFileWriter writer;

	private readonly JsonSerializerOptions jsonOptions = new JsonSerializerOptions
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		WriteIndented = true
	};

	private PlayerReportState state = new PlayerReportState();

	private bool disposed;

	private bool dirty;

	private bool storageReady;

	private long saveListenerId = -1L;

	public string StorageError { get; private set; } = "";

	public string RecoveryFile { get; private set; } = "";

	public string RootCommandOwner { get; private set; } = "Not registered";

	public bool RootCommandRegisteredBySuite { get; private set; }

	public ReportsService(ICoreServerAPI api)
	{
		this.api = api;
		string text = Path.Combine(SuiteDataPaths.GetWorldRoot(api), "reports");
		Directory.CreateDirectory(text);
		statePath = Path.Combine(text, "player-reports.json");
		writer = new DeferredJsonFileWriter(statePath, (string error) =>
		{
			api.Logger.Warning("[ServerEssentials/Reports] Persistence failed; snapshot retained for retry: {0}", error);
		});
	}

	public void Start()
	{
		Load();
		api.Event.GameWorldSave += OnWorldSave;
		saveListenerId = api.Event.RegisterGameTickListener((float _) =>
		{
			SaveIfDirty();
		}, 60000);
		api.Logger.Notification("[ServerEssentials/Reports] Reports service started. Reports={0}", state.Reports.Count);
	}

	public void SetRootCommandOwnership(string owner, bool suiteOwns)
	{
		RootCommandOwner = (string.IsNullOrWhiteSpace(owner) ? "Unknown" : owner);
		RootCommandRegisteredBySuite = suiteOwns;
	}

	public (bool Success, string Message, PlayerReportRecord? Report) CreateReport(IServerPlayer reporter, string issue)
	{
		string text = NormalizeText(issue, 700);
		if (string.IsNullOrWhiteSpace(text))
		{
			return (Success: false, Message: "Usage: /report <issue>  (example: /report Spawn bridge is broken)", Report: null);
		}
		PlayerReportRecord playerReportRecord2;
		lock (gate)
		{
			if (!storageReady)
			{
				return (Success: false, Message: "Cannot create a report: stored reports are unavailable. Ask an administrator to check the server log.", Report: null);
			}
			long num = NowMs();
			string text2 = (string.IsNullOrWhiteSpace(reporter.PlayerUID) ? reporter.PlayerName : reporter.PlayerUID);
			if (!string.IsNullOrWhiteSpace(text2) && lastReportByReporter.TryGetValue(text2, out var value) && num - value < 30000)
			{
				long value2 = Math.Max(1L, (30000 - (num - value) + 999) / 1000);
				return (Success: false, Message: $"Please wait {value2}s before submitting another report.", Report: null);
			}
			if (state.Reports.Count >= 5000)
			{
				PlayerReportRecord playerReportRecord = (from x in state.Reports
					where string.Equals(x.Status, "Resolved", StringComparison.OrdinalIgnoreCase)
					orderby x.UpdatedUnixMilliseconds
					select x).FirstOrDefault();
				if (playerReportRecord == null)
				{
					return (Success: false, Message: "The report queue is at its safety limit. Please contact staff directly.", Report: null);
				}
				state.Reports.Remove(playerReportRecord);
			}
			if (!string.IsNullOrWhiteSpace(text2))
			{
				if (lastReportByReporter.Count >= 4096 && !lastReportByReporter.ContainsKey(text2))
				{
					long cutoff = num - 300000;
					foreach (string item in (from x in lastReportByReporter
						where x.Value < cutoff
						select x.Key).Take(1024).ToList())
					{
						lastReportByReporter.Remove(item);
					}
					if (lastReportByReporter.Count >= 4096)
					{
						lastReportByReporter.Clear();
					}
				}
				lastReportByReporter[text2] = num;
			}
			playerReportRecord2 = new PlayerReportRecord
			{
				Id = Math.Max(1L, state.NextId++),
				CreatedUnixMilliseconds = num,
				UpdatedUnixMilliseconds = num,
				Status = "Open",
				ReporterUid = (reporter.PlayerUID ?? ""),
				ReporterName = (reporter.PlayerName ?? "Unknown"),
				TargetUid = "",
				TargetName = "General issue",
				Reason = text
			};
			playerReportRecord2.Events.Add(new PlayerReportEvent
			{
				UnixMilliseconds = num,
				Actor = playerReportRecord2.ReporterName,
				Action = "Created",
				Detail = "Issue reported: " + playerReportRecord2.Reason
			});
			state.Reports.Add(playerReportRecord2);
			dirty = true;
		}
		NotifyStaff(playerReportRecord2);
		return (Success: true, Message: $"Report #{playerReportRecord2.Id} submitted. Staff have been notified.", Report: Clone(playerReportRecord2, summary: true));
	}

	public PlayerReportSnapshot GetSnapshot(string status = "all", int limit = 500)
	{
		lock (gate)
		{
			string normalized = NormalizeStatusFilter(status);
			List<PlayerReportRecord> reports = (from x in (from x in state.Reports
					where normalized == "All" || string.Equals(x.Status, normalized, StringComparison.OrdinalIgnoreCase)
					orderby x.UpdatedUnixMilliseconds descending
					select x).Take(Math.Clamp(limit, 1, 1000))
				select Clone(x, summary: true)).ToList();
			return new PlayerReportSnapshot
			{
				GeneratedUnixMilliseconds = NowMs(),
				OpenCount = state.Reports.Count((PlayerReportRecord x) => x.Status == "Open"),
				InvestigatingCount = state.Reports.Count((PlayerReportRecord x) => x.Status == "Investigating"),
				ResolvedCount = state.Reports.Count((PlayerReportRecord x) => x.Status == "Resolved"),
				TotalCount = state.Reports.Count,
				RootCommandOwner = RootCommandOwner,
				RootCommandRegisteredBySuite = RootCommandRegisteredBySuite,
				Reports = reports,
				StorageReady = storageReady,
				StorageError = ((writer.LastError.Length > 0) ? writer.LastError : StorageError)
			};
		}
	}

	public PlayerReportRecord? GetReport(long id, int offset = 0, int limit = 100)
	{
		lock (gate)
		{
			return Clone(state.Reports.FirstOrDefault((PlayerReportRecord x) => x.Id == id), summary: false, Math.Max(0, offset), Math.Clamp(limit, 1, 100));
		}
	}

	public string SetStatus(long id, string status, string actor, string note = "")
	{
		string text = NormalizeStatus(status);
		if (text.Length == 0)
		{
			return "Status must be open, investigating, or resolved.";
		}
		lock (gate)
		{
			if (!storageReady)
			{
				return "Cannot update reports while storage is unavailable.";
			}
			PlayerReportRecord playerReportRecord = state.Reports.FirstOrDefault((PlayerReportRecord x) => x.Id == id);
			if (playerReportRecord == null)
			{
				return $"Report #{id} was not found.";
			}
			if (playerReportRecord.Status == text && string.IsNullOrWhiteSpace(note))
			{
				return $"Report #{id} is already {text}.";
			}
			if (playerReportRecord.Events.Count + (string.IsNullOrWhiteSpace(note) ? 1 : 2) > 2000 || (!string.IsNullOrWhiteSpace(note) && playerReportRecord.Notes.Count >= 1000))
			{
				return "Cannot extend this report's history: its safety limit is reached. Existing notes are retained; create a follow-up report.";
			}
			string status2 = playerReportRecord.Status;
			playerReportRecord.Status = text;
			playerReportRecord.UpdatedUnixMilliseconds = NowMs();
			playerReportRecord.Events.Add(new PlayerReportEvent
			{
				UnixMilliseconds = playerReportRecord.UpdatedUnixMilliseconds,
				Actor = CleanActor(actor),
				Action = "Status",
				Detail = status2 + " -> " + text
			});
			if (!string.IsNullOrWhiteSpace(note))
			{
				AddNoteUnsafe(playerReportRecord, actor, note);
			}
			dirty = true;
			return $"Report #{id} is now {text}.";
		}
	}

	public string Assign(long id, string staff, string actor)
	{
		lock (gate)
		{
			if (!storageReady)
			{
				return "Cannot update reports while storage is unavailable.";
			}
			PlayerReportRecord playerReportRecord = state.Reports.FirstOrDefault((PlayerReportRecord x) => x.Id == id);
			if (playerReportRecord == null)
			{
				return $"Report #{id} was not found.";
			}
			string text = NormalizeText(staff, 80);
			string assignedStaff = playerReportRecord.AssignedStaff;
			if (string.Equals(assignedStaff, text, StringComparison.Ordinal))
			{
				return $"Report #{id} assignment is unchanged.";
			}
			if (playerReportRecord.Events.Count >= 2000)
			{
				return "Cannot extend this report's history: its safety limit is reached. Create a follow-up report.";
			}
			playerReportRecord.AssignedStaff = text;
			playerReportRecord.UpdatedUnixMilliseconds = NowMs();
			playerReportRecord.Events.Add(new PlayerReportEvent
			{
				UnixMilliseconds = playerReportRecord.UpdatedUnixMilliseconds,
				Actor = CleanActor(actor),
				Action = "Assignment",
				Detail = (string.IsNullOrWhiteSpace(text) ? ("Unassigned (was " + assignedStaff + ")") : ("Assigned to " + text))
			});
			dirty = true;
			return string.IsNullOrWhiteSpace(text) ? $"Report #{id} is unassigned." : $"Report #{id} assigned to {text}.";
		}
	}

	public string AddNote(long id, string actor, string text)
	{
		lock (gate)
		{
			if (!storageReady)
			{
				return "Cannot update reports while storage is unavailable.";
			}
			PlayerReportRecord playerReportRecord = state.Reports.FirstOrDefault((PlayerReportRecord x) => x.Id == id);
			if (playerReportRecord == null)
			{
				return $"Report #{id} was not found.";
			}
			if (string.IsNullOrWhiteSpace(NormalizeText(text, 1200)))
			{
				return "Note text is required.";
			}
			if (playerReportRecord.Notes.Count >= 1000 || playerReportRecord.Events.Count >= 2000)
			{
				return "Cannot extend this report's history: its safety limit is reached. Existing notes are retained; create a follow-up report.";
			}
			AddNoteUnsafe(playerReportRecord, actor, text);
			dirty = true;
			return $"Private note added to report #{id}.";
		}
	}

	private void AddNoteUnsafe(PlayerReportRecord row, string actor, string text)
	{
		long num = NowMs();
		string text2 = NormalizeText(text, 1200);
		row.Notes.Add(new PlayerReportNote
		{
			UnixMilliseconds = num,
			Author = CleanActor(actor),
			Text = text2
		});
		row.Events.Add(new PlayerReportEvent
		{
			UnixMilliseconds = num,
			Actor = CleanActor(actor),
			Action = "Note",
			Detail = "Private staff note added."
		});
		row.UpdatedUnixMilliseconds = num;
	}

	private void NotifyStaff(PlayerReportRecord row)
	{
		string text = $"[NSE Reports] New issue report #{row.Id} from {row.ReporterName}: {row.Reason}";
		foreach (IServerPlayer item in api.World.AllOnlinePlayers.OfType<IServerPlayer>())
		{
			if (IsReportStaff(item))
			{
				try
				{
					item.SendMessage(GlobalConstants.GeneralChatGroup, text, EnumChatType.Notification);
				}
				catch
				{
				}
			}
		}
		api.Logger.Notification("{0}", text);
	}

	private static bool IsReportStaff(IServerPlayer player)
	{
		return player.HasPrivilege(Privilege.controlserver) || player.HasPrivilege(Privilege.root) || player.HasPrivilege("nse.reports.manage") || player.HasPrivilege("nse.player.manage") || player.HasPrivilege("nse.player.view");
	}

	private void Load()
	{
		try
		{
			if (!File.Exists(statePath))
			{
				state = new PlayerReportState
				{
					SchemaVersion = 1
				};
				storageReady = true;
				return;
			}
			PlayerReportState playerReportState = JsonStateRecovery.Read<PlayerReportState>(statePath, jsonOptions);
			if (playerReportState.SchemaVersion > 1 || playerReportState.SchemaVersion < 1)
			{
				throw new NotSupportedException("Unsupported report state schema; original file was retained.");
			}
			if (playerReportState.Reports == null)
			{
				throw new InvalidDataException("Report collection is null.");
			}
			bool flag = playerReportState.Reports.Any((PlayerReportRecord x) =>
			{
				int result;
				if (x != null && x.Id > 0 && x.Id != long.MaxValue)
				{
					List<PlayerReportNote> notes = x.Notes;
					if (notes == null || !notes.Any((PlayerReportNote n) => n == null))
					{
						result = ((x.Events?.Any((PlayerReportEvent e) => e == null) ?? false) ? 1 : 0);
						goto IL_0088;
					}
				}
				result = 1;
				goto IL_0088;
				IL_0088:
				return (byte)result != 0;
			}) || (from x in playerReportState.Reports
				where x != null
				group x by x.Id).Any((IGrouping<long, PlayerReportRecord> g) => g.Count() > 1);
			if (flag)
			{
				RecoveryFile = JsonStateRecovery.Preserve(statePath);
			}
			playerReportState.Reports = (from x in playerReportState.Reports
				where x != null && x.Id > 0 && x.Id < long.MaxValue
				group x by x.Id into g
				select g.OrderByDescending((PlayerReportRecord x) => x.UpdatedUnixMilliseconds).First()).ToList();
			foreach (PlayerReportRecord report in playerReportState.Reports)
			{
				PlayerReportRecord playerReportRecord = report;
				if (playerReportRecord.Notes == null)
				{
					List<PlayerReportNote> list = (playerReportRecord.Notes = new List<PlayerReportNote>());
				}
				playerReportRecord = report;
				if (playerReportRecord.Events == null)
				{
					List<PlayerReportEvent> list3 = (playerReportRecord.Events = new List<PlayerReportEvent>());
				}
				string text = NormalizeStatus(report.Status);
				report.Status = ((text.Length > 0) ? text : "Open");
				report.Notes = report.Notes.Where((PlayerReportNote x) => x != null).ToList();
				report.Events = report.Events.Where((PlayerReportEvent x) => x != null).ToList();
			}
			playerReportState.NextId = Math.Max(playerReportState.NextId, (playerReportState.Reports.Count == 0) ? 1 : (playerReportState.Reports.Max((PlayerReportRecord x) => x.Id) + 1));
			if (playerReportState.NextId <= 0 || playerReportState.NextId == long.MaxValue)
			{
				throw new NotSupportedException("Report IDs cannot safely advance; original file was retained.");
			}
			state = playerReportState;
			storageReady = true;
			StorageError = (flag ? "Recovered valid reports; the original file was preserved in recovery/." : "");
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
					state = new PlayerReportState
					{
						SchemaVersion = 1
					};
					storageReady = true;
					StorageError = "Damaged report file preserved in recovery/; a new queue is available.";
				}
				catch (Exception ex2)
				{
					StorageError = StorageError + " Recovery failed: " + ex2.Message;
				}
			}
			api.Logger.Warning("[ServerEssentials/Reports] Report recovery: writable={0}; {1}", storageReady, StorageError);
		}
	}

	private void OnWorldSave()
	{
		SaveIfDirty();
		if (!writer.Flush())
		{
			api.Logger.Warning("[ServerEssentials/Reports] World-save flush did not complete; pending snapshot remains queued.");
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
		PlayerReportState snapshot;
		lock (gate)
		{
			snapshot = new PlayerReportState
			{
				SchemaVersion = 1,
				NextId = state.NextId,
				Reports = (from x in state.Reports
					select Clone(x) into x
					where x != null
					select x).Cast<PlayerReportRecord>().ToList()
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

	private static PlayerReportRecord? Clone(PlayerReportRecord? row, bool summary = false, int offset = 0, int limit = int.MaxValue)
	{
		if (row == null)
		{
			return null;
		}
		PlayerReportRecord playerReportRecord = new PlayerReportRecord
		{
			Id = row.Id,
			CreatedUnixMilliseconds = row.CreatedUnixMilliseconds,
			UpdatedUnixMilliseconds = row.UpdatedUnixMilliseconds,
			Status = row.Status,
			ReporterUid = row.ReporterUid,
			ReporterName = row.ReporterName,
			TargetUid = row.TargetUid,
			TargetName = row.TargetName,
			Reason = row.Reason,
			AssignedStaff = row.AssignedStaff,
			NoteCount = (row.Notes?.Count ?? 0),
			EventCount = (row.Events?.Count ?? 0),
			HistoryOffset = offset,
			HistoryLimit = ((limit != int.MaxValue) ? limit : 0)
		};
		IEnumerable<PlayerReportNote> source;
		if (!summary)
		{
			if (limit != int.MaxValue)
			{
				source = (row.Notes ?? new List<PlayerReportNote>()).AsEnumerable().Reverse().Skip(offset)
					.Take(limit);
			}
			else
			{
				IEnumerable<PlayerReportNote> enumerable = row.Notes ?? new List<PlayerReportNote>();
				source = enumerable;
			}
		}
		else
		{
			source = (row.Notes ?? new List<PlayerReportNote>()).TakeLast(2);
		}
		playerReportRecord.Notes = source.Select((PlayerReportNote x) => new PlayerReportNote
		{
			UnixMilliseconds = x.UnixMilliseconds,
			Author = x.Author,
			Text = x.Text
		}).ToList();
		IEnumerable<PlayerReportEvent> source2;
		if (!summary)
		{
			if (limit != int.MaxValue)
			{
				source2 = (row.Events ?? new List<PlayerReportEvent>()).AsEnumerable().Reverse().Skip(offset)
					.Take(limit);
			}
			else
			{
				IEnumerable<PlayerReportEvent> enumerable2 = row.Events ?? new List<PlayerReportEvent>();
				source2 = enumerable2;
			}
		}
		else
		{
			source2 = (row.Events ?? new List<PlayerReportEvent>()).TakeLast(5);
		}
		playerReportRecord.Events = source2.Select((PlayerReportEvent x) => new PlayerReportEvent
		{
			UnixMilliseconds = x.UnixMilliseconds,
			Actor = x.Actor,
			Action = x.Action,
			Detail = x.Detail
		}).ToList();
		return playerReportRecord;
	}

	private static string NormalizeStatus(string? value)
	{
		string text = (value ?? "").Trim().ToLowerInvariant();
		if (1 == 0)
		{
		}
		string result;
		switch (text)
		{
		case "open":
			result = "Open";
			break;
		case "investigating":
		case "investigate":
		case "working":
			result = "Investigating";
			break;
		case "resolved":
		case "resolve":
		case "closed":
			result = "Resolved";
			break;
		default:
			result = "";
			break;
		}
		if (1 == 0)
		{
		}
		return result;
	}

	private static string NormalizeStatusFilter(string? value)
	{
		if (string.Equals((value ?? "").Trim(), "all", StringComparison.OrdinalIgnoreCase))
		{
			return "All";
		}
		string text = NormalizeStatus(value);
		return (text.Length == 0) ? "All" : text;
	}

	private static string NormalizeText(string? value, int maxLength)
	{
		string text = (value ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
		while (text.Contains("  ", StringComparison.Ordinal))
		{
			text = text.Replace("  ", " ", StringComparison.Ordinal);
		}
		return (text.Length <= maxLength) ? text : text.Substring(0, maxLength);
	}

	private static string CleanActor(string? actor)
	{
		return string.IsNullOrWhiteSpace(actor) ? "Staff" : NormalizeText(actor, 80);
	}

	private static long NowMs()
	{
		return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
	}
}
