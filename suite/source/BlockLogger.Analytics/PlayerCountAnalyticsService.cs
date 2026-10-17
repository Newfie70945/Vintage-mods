using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using BlockLogger.Services;
using Newfies.Storage;
using Vintagestory.API.Server;

namespace BlockLogger.Analytics;

public sealed class PlayerCountAnalyticsService : IDisposable
{
	private const int ReconcileIntervalMs = 60000;

	private const int PersistIntervalMs = 300000;

	private const int DailyRetentionDays = 400;

	private const int HourlyRetentionDays = 45;

	private const int SchemaVersion = 1;

	private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		WriteIndented = true
	};

	private readonly ICoreServerAPI api;

	private readonly object gate = new object();

	private readonly HashSet<string> activePlayers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

	private PlayerCountAnalyticsState state = new PlayerCountAnalyticsState();

	private readonly string statePath;

	private readonly DeferredJsonFileWriter writer;

	private bool storageReady;

	private long listenerId = -1L;

	private long lastObservedUnixMilliseconds;

	private long lastPersistUnixMilliseconds;

	private bool started;

	private bool disposed;

	public PlayerCountAnalyticsService(ICoreServerAPI api)
	{
		this.api = api;
		string text = Path.Combine(SuiteDataPaths.GetWorldRoot(api), "diagnostics");
		Directory.CreateDirectory(text);
		statePath = Path.Combine(text, "player-count-analytics.json");
		writer = new DeferredJsonFileWriter(statePath, (string error) =>
		{
			api.Logger.Warning("[PlayerAnalytics] Persistence failed; snapshot retained for retry: {0}", error);
		});
	}

	public void Start()
	{
		if (started)
		{
			return;
		}
		started = true;
		Load();
		lock (gate)
		{
			activePlayers.Clear();
			foreach (IServerPlayer item in api.World.AllOnlinePlayers.OfType<IServerPlayer>())
			{
				if (!string.IsNullOrWhiteSpace(item.PlayerUID))
				{
					activePlayers.Add(item.PlayerUID);
				}
			}
			TouchExtremaUnsafe(lastPersistUnixMilliseconds = (lastObservedUnixMilliseconds = NowMs()), activePlayers.Count);
		}
		api.Event.PlayerNowPlaying += OnPlayerNowPlaying;
		api.Event.PlayerDisconnect += OnPlayerDisconnect;
		api.Event.GameWorldSave += OnWorldSave;
		listenerId = api.Event.RegisterGameTickListener((float _) =>
		{
			ReconcileAndMaintain();
		}, (Exception ex) =>
		{
			api.Logger.Warning("[PlayerAnalytics] reconciliation failed: {0}: {1}", ex.GetType().Name, ex.Message);
		}, 60000);
		api.Logger.Notification("[PlayerAnalytics] Player-count history started. CurrentPlayers={0}, retention={1} daily/{2} hourly days.", activePlayers.Count, 400, 45);
	}

	public PlayerCountAnalyticsSnapshot GetSnapshot(int dailyDays = 30)
	{
		lock (gate)
		{
			ObserveElapsedUnsafe(NowMs());
			PruneUnsafe();
			int count = Math.Clamp(dailyDays, 7, 365);
			string todayKey = LocalDateKey(NowMs());
			PlayerCountDailyAccumulator playerCountDailyAccumulator = state.Daily.FirstOrDefault((PlayerCountDailyAccumulator x) => x.Date == todayKey);
			List<PlayerCountDailyPoint> daily = (from x in state.Daily.OrderBy((PlayerCountDailyAccumulator x) => x.Date, StringComparer.Ordinal).TakeLast(count)
				select new PlayerCountDailyPoint
				{
					Date = x.Date,
					Low = (x.HasObservation ? x.Low : 0),
					High = (x.HasObservation ? x.High : 0),
					Average = ((x.ObservedMilliseconds <= 0) ? ((double)x.High) : WeightedAverage(x.WeightedPlayerMilliseconds, x.ObservedMilliseconds)),
					ObservedMinutes = (double)x.ObservedMilliseconds / 60000.0
				}).ToList();
			Dictionary<int, PlayerCountHourlyAccumulator> dictionary = state.Hourly.Where((PlayerCountHourlyAccumulator x) => x.Date == todayKey).ToDictionary((PlayerCountHourlyAccumulator x) => x.Hour);
			List<PlayerCountHourlyPoint> list = new List<PlayerCountHourlyPoint>(24);
			for (int num = 0; num < 24; num++)
			{
				if (dictionary.TryGetValue(num, out var value) && value.HasObservation)
				{
					list.Add(new PlayerCountHourlyPoint
					{
						Hour = num,
						Label = $"{num:00}:00",
						Average = ((value.ObservedMilliseconds <= 0) ? ((double)value.High) : WeightedAverage(value.WeightedPlayerMilliseconds, value.ObservedMilliseconds)),
						Low = value.Low,
						High = value.High,
						ObservedMinutes = (double)value.ObservedMilliseconds / 60000.0
					});
				}
				else
				{
					list.Add(new PlayerCountHourlyPoint
					{
						Hour = num,
						Label = $"{num:00}:00"
					});
				}
			}
			DateTime now = DateTime.Now;
			now = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Local);
			List<PlayerCountHourlyPoint> list2 = new List<PlayerCountHourlyPoint>(24);
			for (int num2 = 23; num2 >= 0; num2--)
			{
				DateTime dateTime = now.AddHours(-num2);
				string dateKey = dateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
				int hour = dateTime.Hour;
				PlayerCountHourlyAccumulator playerCountHourlyAccumulator = state.Hourly.FirstOrDefault((PlayerCountHourlyAccumulator x) => x.Date == dateKey && x.Hour == hour);
				if (playerCountHourlyAccumulator != null && playerCountHourlyAccumulator.HasObservation)
				{
					list2.Add(new PlayerCountHourlyPoint
					{
						Date = dateKey,
						Hour = hour,
						Label = dateTime.ToString("HH:mm", CultureInfo.InvariantCulture),
						Average = ((playerCountHourlyAccumulator.ObservedMilliseconds <= 0) ? ((double)playerCountHourlyAccumulator.High) : WeightedAverage(playerCountHourlyAccumulator.WeightedPlayerMilliseconds, playerCountHourlyAccumulator.ObservedMilliseconds)),
						Low = playerCountHourlyAccumulator.Low,
						High = playerCountHourlyAccumulator.High,
						ObservedMinutes = (double)playerCountHourlyAccumulator.ObservedMilliseconds / 60000.0
					});
				}
				else
				{
					list2.Add(new PlayerCountHourlyPoint
					{
						Date = dateKey,
						Hour = hour,
						Label = dateTime.ToString("HH:mm", CultureInfo.InvariantCulture)
					});
				}
			}
			return new PlayerCountAnalyticsSnapshot
			{
				GeneratedUnixMilliseconds = NowMs(),
				CurrentPlayers = activePlayers.Count,
				ServerTimeZone = TimeZoneInfo.Local.Id,
				DailyRetentionDays = 400,
				HourlyRetentionDays = 45,
				TodayLow = ((playerCountDailyAccumulator != null && playerCountDailyAccumulator.HasObservation) ? playerCountDailyAccumulator.Low : activePlayers.Count),
				TodayHigh = ((playerCountDailyAccumulator != null && playerCountDailyAccumulator.HasObservation) ? playerCountDailyAccumulator.High : activePlayers.Count),
				TodayAverage = ((playerCountDailyAccumulator == null || playerCountDailyAccumulator.ObservedMilliseconds <= 0) ? ((double)activePlayers.Count) : WeightedAverage(playerCountDailyAccumulator.WeightedPlayerMilliseconds, playerCountDailyAccumulator.ObservedMilliseconds)),
				Daily = daily,
				TodayHourly = list,
				Last24Hours = list2
			};
		}
	}

	private void OnPlayerNowPlaying(IServerPlayer player)
	{
		if (disposed || string.IsNullOrWhiteSpace(player.PlayerUID))
		{
			return;
		}
		lock (gate)
		{
			long nowUnixMilliseconds = NowMs();
			ObserveElapsedUnsafe(nowUnixMilliseconds);
			if (activePlayers.Add(player.PlayerUID))
			{
				TouchExtremaUnsafe(nowUnixMilliseconds, activePlayers.Count);
			}
		}
	}

	private void OnPlayerDisconnect(IServerPlayer player)
	{
		if (disposed || string.IsNullOrWhiteSpace(player.PlayerUID))
		{
			return;
		}
		lock (gate)
		{
			long nowUnixMilliseconds = NowMs();
			ObserveElapsedUnsafe(nowUnixMilliseconds);
			if (activePlayers.Remove(player.PlayerUID))
			{
				TouchExtremaUnsafe(nowUnixMilliseconds, activePlayers.Count);
			}
		}
	}

	private void ReconcileAndMaintain()
	{
		if (disposed)
		{
			return;
		}
		bool flag;
		lock (gate)
		{
			long num = NowMs();
			ObserveElapsedUnsafe(num);
			HashSet<string> hashSet = new HashSet<string>(from x in api.World.AllOnlinePlayers.OfType<IServerPlayer>()
				select x.PlayerUID into x
				where !string.IsNullOrWhiteSpace(x)
				select x, StringComparer.OrdinalIgnoreCase);
			if (!activePlayers.SetEquals(hashSet))
			{
				activePlayers.Clear();
				foreach (string item in hashSet)
				{
					activePlayers.Add(item);
				}
				TouchExtremaUnsafe(num, activePlayers.Count);
			}
			PruneUnsafe();
			flag = num - lastPersistUnixMilliseconds >= 300000;
			if (flag)
			{
				lastPersistUnixMilliseconds = num;
			}
		}
		if (flag)
		{
			Save();
		}
	}

	private void ObserveElapsedUnsafe(long nowUnixMilliseconds)
	{
		if (lastObservedUnixMilliseconds <= 0)
		{
			lastObservedUnixMilliseconds = nowUnixMilliseconds;
			TouchExtremaUnsafe(nowUnixMilliseconds, activePlayers.Count);
		}
		else
		{
			if (nowUnixMilliseconds <= lastObservedUnixMilliseconds)
			{
				return;
			}
			long num = lastObservedUnixMilliseconds;
			int count = activePlayers.Count;
			while (num < nowUnixMilliseconds)
			{
				DateTimeOffset dateTimeOffset = DateTimeOffset.FromUnixTimeMilliseconds(num).ToLocalTime();
				long num2 = Math.Min(nowUnixMilliseconds, new DateTimeOffset(dateTimeOffset.Year, dateTimeOffset.Month, dateTimeOffset.Day, dateTimeOffset.Hour, 0, 0, dateTimeOffset.Offset).AddHours(1.0).ToUnixTimeMilliseconds());
				if (num2 <= num)
				{
					num2 = nowUnixMilliseconds;
				}
				long num3 = num2 - num;
				string date = dateTimeOffset.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
				PlayerCountDailyAccumulator dailyUnsafe = GetDailyUnsafe(date);
				PlayerCountHourlyAccumulator hourlyUnsafe = GetHourlyUnsafe(date, dateTimeOffset.Hour);
				dailyUnsafe.ObservedMilliseconds += num3;
				dailyUnsafe.WeightedPlayerMilliseconds += (double)num3 * (double)count;
				hourlyUnsafe.ObservedMilliseconds += num3;
				hourlyUnsafe.WeightedPlayerMilliseconds += (double)num3 * (double)count;
				ObserveExtrema(dailyUnsafe, count);
				ObserveExtrema(hourlyUnsafe, count);
				num = num2;
			}
			lastObservedUnixMilliseconds = nowUnixMilliseconds;
		}
	}

	private void TouchExtremaUnsafe(long nowUnixMilliseconds, int count)
	{
		DateTimeOffset dateTimeOffset = DateTimeOffset.FromUnixTimeMilliseconds(nowUnixMilliseconds).ToLocalTime();
		string date = dateTimeOffset.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
		ObserveExtrema(GetDailyUnsafe(date), count);
		ObserveExtrema(GetHourlyUnsafe(date, dateTimeOffset.Hour), count);
	}

	private PlayerCountDailyAccumulator GetDailyUnsafe(string date)
	{
		PlayerCountDailyAccumulator playerCountDailyAccumulator = state.Daily.FirstOrDefault((PlayerCountDailyAccumulator x) => x.Date == date);
		if (playerCountDailyAccumulator != null)
		{
			return playerCountDailyAccumulator;
		}
		playerCountDailyAccumulator = new PlayerCountDailyAccumulator
		{
			Date = date
		};
		state.Daily.Add(playerCountDailyAccumulator);
		return playerCountDailyAccumulator;
	}

	private PlayerCountHourlyAccumulator GetHourlyUnsafe(string date, int hour)
	{
		PlayerCountHourlyAccumulator playerCountHourlyAccumulator = state.Hourly.FirstOrDefault((PlayerCountHourlyAccumulator x) => x.Date == date && x.Hour == hour);
		if (playerCountHourlyAccumulator != null)
		{
			return playerCountHourlyAccumulator;
		}
		playerCountHourlyAccumulator = new PlayerCountHourlyAccumulator
		{
			Date = date,
			Hour = hour
		};
		state.Hourly.Add(playerCountHourlyAccumulator);
		return playerCountHourlyAccumulator;
	}

	private static void ObserveExtrema(PlayerCountAccumulator row, int count)
	{
		if (!row.HasObservation)
		{
			row.HasObservation = true;
			row.Low = count;
			row.High = count;
		}
		else
		{
			row.Low = Math.Min(row.Low, count);
			row.High = Math.Max(row.High, count);
		}
	}

	private void PruneUnsafe()
	{
		DateTime date = DateTime.Now.Date;
		string dailyCutoff = date.AddDays(-399.0).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
		string hourlyCutoff = date.AddDays(-44.0).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
		state.Daily.RemoveAll((PlayerCountDailyAccumulator x) => string.CompareOrdinal(x.Date, dailyCutoff) < 0);
		state.Hourly.RemoveAll((PlayerCountHourlyAccumulator x) => string.CompareOrdinal(x.Date, hourlyCutoff) < 0);
	}

	private void OnWorldSave()
	{
		Save();
		if (!writer.Flush())
		{
			api.Logger.Warning("[PlayerAnalytics] World-save flush did not complete; snapshot remains queued.");
		}
	}

	private void Load()
	{
		try
		{
			if (!File.Exists(statePath))
			{
				state = new PlayerCountAnalyticsState
				{
					SchemaVersion = 1
				};
				storageReady = true;
				return;
			}
			PlayerCountAnalyticsState playerCountAnalyticsState = JsonStateRecovery.Read<PlayerCountAnalyticsState>(statePath, JsonOptions);
			if (playerCountAnalyticsState.SchemaVersion != 1)
			{
				throw new NotSupportedException("Unsupported analytics schema; original file retained.");
			}
			List<PlayerCountDailyAccumulator> source = playerCountAnalyticsState.Daily ?? new List<PlayerCountDailyAccumulator>();
			List<PlayerCountHourlyAccumulator> source2 = playerCountAnalyticsState.Hourly ?? new List<PlayerCountHourlyAccumulator>();
			if (playerCountAnalyticsState.Daily == null || playerCountAnalyticsState.Hourly == null || source.Any((PlayerCountDailyAccumulator x) => !ValidDaily(x)) || source2.Any((PlayerCountHourlyAccumulator x) => !ValidHourly(x)) || (from x in source.Where(ValidDaily)
				group x by x.Date).Any((IGrouping<string, PlayerCountDailyAccumulator> g) => g.Count() > 1) || (from x in source2.Where(ValidHourly)
				group x by (Date: x.Date, Hour: x.Hour)).Any((IGrouping<(string Date, int Hour), PlayerCountHourlyAccumulator> g) => g.Count() > 1))
			{
				JsonStateRecovery.Preserve(statePath);
			}
			playerCountAnalyticsState.Daily = (from x in source.Where(ValidDaily)
				group x by x.Date into g
				select g.OrderByDescending((PlayerCountDailyAccumulator x) => x.ObservedMilliseconds).First()).ToList();
			playerCountAnalyticsState.Hourly = (from x in source2.Where(ValidHourly)
				group x by (Date: x.Date, Hour: x.Hour) into g
				select g.OrderByDescending((PlayerCountHourlyAccumulator x) => x.ObservedMilliseconds).First()).ToList();
			state = playerCountAnalyticsState;
			storageReady = true;
			PlayerCountAnalyticsState playerCountAnalyticsState2 = state;
			if (playerCountAnalyticsState2.Daily == null)
			{
				List<PlayerCountDailyAccumulator> list = (playerCountAnalyticsState2.Daily = new List<PlayerCountDailyAccumulator>());
			}
			playerCountAnalyticsState2 = state;
			if (playerCountAnalyticsState2.Hourly == null)
			{
				List<PlayerCountHourlyAccumulator> list3 = (playerCountAnalyticsState2.Hourly = new List<PlayerCountHourlyAccumulator>());
			}
		}
		catch (Exception ex)
		{
			storageReady = false;
			if (ex is JsonException || ex is InvalidDataException)
			{
				try
				{
					JsonStateRecovery.Preserve(statePath);
					state = new PlayerCountAnalyticsState
					{
						SchemaVersion = 1
					};
					storageReady = true;
				}
				catch
				{
				}
			}
			api.Logger.Warning("[PlayerAnalytics] Could not load history; original data retained or preserved in recovery/: {0}: {1}", ex.GetType().Name, ex.Message);
		}
		static bool ValidAccumulator(PlayerCountAccumulator row)
		{
			return row.ObservedMilliseconds >= 0 && double.IsFinite(row.WeightedPlayerMilliseconds) && row.WeightedPlayerMilliseconds >= 0.0 && (!row.HasObservation || (row.Low >= 0 && row.High >= row.Low));
		}
		static bool ValidDaily(PlayerCountDailyAccumulator? row)
		{
			return row != null && ValidDate(row.Date) && ValidAccumulator(row);
		}
		static bool ValidDate(string date)
		{
			DateTime result;
			return DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out result);
		}
		static bool ValidHourly(PlayerCountHourlyAccumulator? row)
		{
			return row != null && ValidDate(row.Date) && row.Hour >= 0 && row.Hour <= 23 && ValidAccumulator(row);
		}
	}

	private void Save()
	{
		if (storageReady)
		{
			PlayerCountAnalyticsState snapshot;
			lock (gate)
			{
				ObserveElapsedUnsafe(NowMs());
				PruneUnsafe();
				snapshot = state.Clone();
			}
			writer.Queue(() => JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions));
		}
	}

	public void Dispose()
	{
		if (!disposed)
		{
			disposed = true;
			api.Event.PlayerNowPlaying -= OnPlayerNowPlaying;
			api.Event.PlayerDisconnect -= OnPlayerDisconnect;
			api.Event.GameWorldSave -= OnWorldSave;
			if (listenerId >= 0)
			{
				api.Event.UnregisterGameTickListener(listenerId);
				listenerId = -1L;
			}
			try
			{
				Save();
			}
			catch
			{
			}
			writer.Dispose();
		}
	}

	private static long NowMs()
	{
		return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
	}

	private static string LocalDateKey(long unixMilliseconds)
	{
		return DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds).ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
	}

	private static double WeightedAverage(double weighted, long observed)
	{
		return (observed <= 0) ? 0.0 : (weighted / (double)observed);
	}
}
