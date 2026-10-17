using System.Collections.Generic;
using System.Threading.Tasks;
using BlockLogger.Models;
using Vintagestory.API.Server;

namespace BlockLogger.Services;

public sealed class LogQueryService
{
    private readonly LogStorageService storage;
    public LogQueryService(LogStorageService storage) => this.storage = storage;

    public Task<List<LogEventRecord>> QueryBlocksAsync(int x, int y, int z, int dimension, int radius, int maxCount, string? textFilter, string? tagFilter, int offset = 0) =>
        storage.QueryEventsAsync(x, y, z, dimension, radius, maxCount, textFilter, tagFilter, false, offset);

    public Task<List<LogEventRecord>> QueryEntitiesAsync(int x, int y, int z, int dimension, int radius, int maxCount, string? textFilter, string? tagFilter, int offset = 0) =>
        storage.QueryEventsAsync(x, y, z, dimension, radius, maxCount, textFilter, tagFilter, true, offset);

    public Task<List<LogEventRecord>> QueryPlayerAsync(string player, int maxCount, string? category, int offset = 0) =>
        storage.QueryPlayerEventsAsync(player, maxCount, category, offset);

    public Task<List<LogEventRecord>> QueryPlayerUidAsync(string uid, int maxCount = 100) => storage.QueryPlayerUidEventsAsync(uid, maxCount);

    public Task<List<BlockLogger.WebDashboard.PerformanceBlockActivityHotspot>>
        QueryRecentBlockActivityHotspotsAsync(int minutes, int maxCount) =>
        storage.QueryRecentBlockActivityHotspotsAsync(minutes, maxCount);

    public Task<int> CountAsync() => storage.CountEventsAsync();
    public string GetLogDirectoryPath() => storage.LegacyEventLogPath;
    public string GetDatabasePath() => storage.DatabasePath;
}
