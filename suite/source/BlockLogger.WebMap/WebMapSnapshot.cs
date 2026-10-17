using System.Collections.Generic;

namespace BlockLogger.WebMap;

public sealed class WebMapSnapshot
{
	public bool Enabled { get; set; }

	public string Provider { get; set; } = "";

	public bool ProviderDetected { get; set; }

	public string ProviderVersion { get; set; } = "";

	public bool Running { get; set; }

	public string BindAddress { get; set; } = "";

	public int Port { get; set; }

	public string MapUrl { get; set; } = "";

	public string DashboardUrl { get; set; } = "";

	public int DashboardPort { get; set; }

	public string Title { get; set; } = "";

	public int CoordinateOriginX { get; set; }

	public int CoordinateOriginZ { get; set; }

	public int SpawnX { get; set; }

	public int SpawnZ { get; set; }

	public bool AllowLivePlayerData { get; set; }

	public bool ShowPlayers { get; set; }

	public bool ShowBadBlockEntities { get; set; }

	public bool ShowActivity { get; set; }

	public bool ShowWaypoints { get; set; }

	public bool ShowLeaderboards { get; set; }

	public bool LeaderboardShowPlaytime { get; set; }

	public bool LeaderboardShowXSkills { get; set; }

	public int LeaderboardTopPlayers { get; set; }

	public int LeaderboardRefreshSeconds { get; set; }

	public bool ExportNormalTerrain { get; set; }

	public bool ExportSepiaTerrain { get; set; }

	public bool ExportTopographicTerrain { get; set; }

	public int PlayerMajorWaypointLimit { get; set; }

	public int PlayerMinorWaypointLimit { get; set; }

	public long LastTerrainExportDurationMilliseconds { get; set; }

	public int MaximumZoomOutSpanBlocks { get; set; }

	public int DefaultViewSpanBlocks { get; set; }

	public int ActivityWindowMinutes { get; set; }

	public int MaxActivityMarkers { get; set; }

	public int PublicRefreshSeconds { get; set; }

	public int PlayerRefreshSeconds { get; set; }

	public int ActivityRefreshSeconds { get; set; }

	public long GeneratedUnixMilliseconds { get; set; }

	public int ActivePlayerCount { get; set; }

	public string IngameDate { get; set; } = "unknown";

	public int IngameYear { get; set; }

	public int IngameMonth { get; set; }

	public int IngameDay { get; set; }

	public float IngameHour { get; set; }

	public long PlayerSnapshotUnixMilliseconds { get; set; }

	public long ActivitySnapshotUnixMilliseconds { get; set; }

	public bool ExportTerrainOnStart { get; set; }

	public bool AutomaticStartupExportEnabled { get; set; }

	public int AutomaticStartupExportIntervalDays { get; set; }

	public bool AutomaticStartupExportDue { get; set; }

	public long NextAutomaticStartupExportUnixMilliseconds { get; set; }

	public bool LockServerDuringTerrainExport { get; set; }

	public bool UseIncrementalTerrainExport { get; set; }

	public int TerrainTileSize { get; set; }

	public WebMapTerrainExportStatus Terrain { get; set; } = new WebMapTerrainExportStatus();

	public int PendingDirtyMapChunks { get; set; }

	public WebMapTerrainManifest? TerrainManifest { get; set; }

	public string LastError { get; set; } = "";

	public string Note { get; set; } = "";

	public List<WebMapPlayerMarker> Players { get; set; } = new List<WebMapPlayerMarker>();

	public List<WebMapActivityMarker> Activity { get; set; } = new List<WebMapActivityMarker>();

	public List<WebMapWaypoint> Waypoints { get; set; } = new List<WebMapWaypoint>();

	public List<WebMapBadBlockMarker> BadBlockEntities { get; set; } = new List<WebMapBadBlockMarker>();
}
