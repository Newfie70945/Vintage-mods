using System.Collections.Generic;
using System.Threading.Tasks;
using BlockLogger.Models;
using BlockLogger.WebDashboard;

namespace BlockLogger.Services;

public sealed class LogQueryService
{
	private readonly LogStorageService storage;

	public LogQueryService(LogStorageService storage)
	{
		this.storage = storage;
	}

	public Task<List<LogEventRecord>> QueryBlocksAsync(int x, int y, int z, int dimension, int radius, int maxCount, string? textFilter, string? tagFilter, int offset = 0)
	{
		return storage.QueryEventsAsync(x, y, z, dimension, radius, maxCount, textFilter, tagFilter, entityOnly: false, offset);
	}

	public Task<List<LogEventRecord>> QueryEntitiesAsync(int x, int y, int z, int dimension, int radius, int maxCount, string? textFilter, string? tagFilter, int offset = 0)
	{
		return storage.QueryEventsAsync(x, y, z, dimension, radius, maxCount, textFilter, tagFilter, entityOnly: true, offset);
	}

	public Task<List<LogEventRecord>> QueryPlayerAsync(string player, int maxCount, string? category, int offset = 0)
	{
		return storage.QueryPlayerEventsAsync(player, maxCount, category, offset);
	}

	public Task<List<LogEventRecord>> QueryPlayerUidAsync(string uid, int maxCount = 100)
	{
		return storage.QueryPlayerUidEventsAsync(uid, maxCount);
	}

	public Task<List<PerformanceBlockActivityHotspot>> QueryRecentBlockActivityHotspotsAsync(int minutes, int maxCount)
	{
		return storage.QueryRecentBlockActivityHotspotsAsync(minutes, maxCount);
	}

	public Task<int> CountAsync()
	{
		return storage.CountEventsAsync();
	}

	public string GetLogDirectoryPath()
	{
		return storage.LegacyEventLogPath;
	}

	public string GetDatabasePath()
	{
		return storage.DatabasePath;
	}
}
