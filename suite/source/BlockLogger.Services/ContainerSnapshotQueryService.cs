using System.Collections.Generic;
using System.Threading.Tasks;
using BlockLogger.Models;

namespace BlockLogger.Services;

public sealed class ContainerSnapshotQueryService
{
	private readonly LogStorageService storage;

	public ContainerSnapshotQueryService(LogStorageService storage)
	{
		this.storage = storage;
	}

	public Task<List<ContainerSnapshotRecord>> QueryAsync(int x, int y, int z, int dimension, int radius, int maxCount, string? textFilter)
	{
		return storage.QuerySnapshotsAsync(x, y, z, dimension, radius, maxCount, textFilter);
	}

	public Task<int> CountAsync()
	{
		return storage.CountSnapshotsAsync();
	}

	public string GetLogDirectoryPath()
	{
		return storage.LegacySnapshotLogPath;
	}
}
