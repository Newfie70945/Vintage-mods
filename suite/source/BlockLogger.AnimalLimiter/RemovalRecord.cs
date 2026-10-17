namespace BlockLogger.AnimalLimiter;

public sealed class RemovalRecord
{
	public string RemovalId { get; set; } = "";

	public long OriginalEntityId { get; set; }

	public string Code { get; set; } = "";

	public string DisplayName { get; set; } = "";

	public double X { get; set; }

	public double Y { get; set; }

	public double Z { get; set; }

	public float Yaw { get; set; }

	public float Pitch { get; set; }

	public int ChunkX { get; set; }

	public int ChunkZ { get; set; }

	public string Rule { get; set; } = "";

	public int Limit { get; set; }

	public int CountBeforeCull { get; set; }

	public int RadiusChunks { get; set; }

	public string NearestPlayerName { get; set; } = "";

	public double NearestPlayerDistance { get; set; }

	public bool LookedTamedOrOwned { get; set; }

	public bool LookedFarmBred { get; set; }

	public bool LookedNamed { get; set; }

	public string WatchedAttributesBase64 { get; set; } = "";

	public long RemovedUnixSeconds { get; set; }

	public bool Returned { get; set; }
}
