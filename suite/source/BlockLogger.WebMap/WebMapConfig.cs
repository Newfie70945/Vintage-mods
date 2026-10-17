using System;

namespace BlockLogger.WebMap;

public sealed class WebMapConfig
{
	public const int CurrentSchemaVersion = 19;

	public int SchemaVersion { get; set; } = 19;

	public bool Enabled { get; set; } = false;

	public string BindAddress { get; set; } = "0.0.0.0";

	public int Port { get; set; } = 9466;

	public string PublicUrl { get; set; } = "";

	public string DashboardUrl { get; set; } = "";

	public string Title { get; set; } = "Newfie's Server Map";

	public bool AllowLivePlayerData { get; set; } = false;

	public bool ShowPlayers { get; set; } = false;

	public int PlayerIconRefreshSeconds { get; set; } = 60;

	public bool ShowBadBlockEntities { get; set; } = false;

	public bool ShowActivity { get; set; } = false;

	public bool ShowWaypoints { get; set; } = true;

	public bool ShowLeaderboards { get; set; } = true;

	public bool LeaderboardShowPlaytime { get; set; } = true;

	public bool LeaderboardShowXSkills { get; set; } = true;

	public int LeaderboardTopPlayers { get; set; } = 5;

	public int LeaderboardRefreshSeconds { get; set; } = 60;

	public bool ExportNormalTerrain { get; set; } = true;

	public bool ExportSepiaTerrain { get; set; } = true;

	public bool ExportTopographicTerrain { get; set; } = true;

	public int PlayerMajorWaypointLimit { get; set; } = 5;

	public int PlayerMinorWaypointLimit { get; set; } = 10;

	public int MaximumZoomOutSpanBlocks { get; set; } = 50000;

	public int DefaultViewSpanBlocks { get; set; } = 8000;

	public int ActivityWindowMinutes { get; set; } = 60;

	public int MaxActivityMarkers { get; set; } = 150;

	public bool ExportTerrainOnStart { get; set; } = false;

	public bool AutomaticStartupExportEnabled { get; set; } = false;

	public int AutomaticStartupExportIntervalDays { get; set; } = 7;

	public long LastSuccessfulTerrainExportUnixMilliseconds { get; set; } = 0L;

	public long LastTerrainExportDurationMilliseconds { get; set; } = 0L;

	public bool LockServerDuringTerrainExport { get; set; } = true;

	public bool UseIncrementalTerrainExport { get; set; } = true;

	public bool UseDirectSaveDatabaseTerrain { get; set; } = true;

	public int TerrainPyramidWorkers { get; set; } = 2;

	public int TerrainBaseRenderWorkers { get; set; } = 2;

	public bool ScheduledTerrainExportEnabled { get; set; } = false;

	public string ScheduledTerrainExportTime { get; set; } = "04:00";

	public bool ScheduledTerrainExportOnlyWhenEmpty { get; set; } = true;

	public int TerrainTileSize { get; set; } = 256;

	public void Clamp()
	{
		SchemaVersion = Math.Max(19, SchemaVersion);
		BindAddress = (BindAddress ?? "0.0.0.0").Trim();
		if (BindAddress.Length == 0 || BindAddress.Length > 100)
		{
			BindAddress = "0.0.0.0";
		}
		Port = Math.Clamp(Port, 1024, 65535);
		PublicUrl = NormalizeHttpUrl(PublicUrl);
		DashboardUrl = NormalizeHttpUrl(DashboardUrl);
		Title = (Title ?? "Newfie's Server Map").Trim();
		if (Title.Length == 0)
		{
			Title = "Newfie's Server Map";
		}
		if (Title.Length > 80)
		{
			Title = Title.Substring(0, 80);
		}
		AutomaticStartupExportIntervalDays = Math.Clamp(AutomaticStartupExportIntervalDays, 1, 365);
		LastSuccessfulTerrainExportUnixMilliseconds = Math.Max(0L, LastSuccessfulTerrainExportUnixMilliseconds);
		LastTerrainExportDurationMilliseconds = Math.Max(0L, LastTerrainExportDurationMilliseconds);
		ScheduledTerrainExportEnabled = false;
		if (!TryParseScheduleTime(ScheduledTerrainExportTime, out string normalized))
		{
			normalized = "04:00";
		}
		ScheduledTerrainExportTime = normalized;
		ActivityWindowMinutes = Math.Clamp(ActivityWindowMinutes, 5, 1440);
		MaxActivityMarkers = Math.Clamp(MaxActivityMarkers, 25, 200);
		PlayerMajorWaypointLimit = Math.Clamp(PlayerMajorWaypointLimit, 0, 100);
		PlayerMinorWaypointLimit = Math.Clamp(PlayerMinorWaypointLimit, 0, 100);
		LeaderboardTopPlayers = Math.Clamp(LeaderboardTopPlayers, 3, 20);
		LeaderboardRefreshSeconds = Math.Clamp(LeaderboardRefreshSeconds, 30, 300);
		PlayerIconRefreshSeconds = Math.Clamp(PlayerIconRefreshSeconds, 1, 300);
		MaximumZoomOutSpanBlocks = Math.Clamp(MaximumZoomOutSpanBlocks, 2048, 100000);
		DefaultViewSpanBlocks = Math.Clamp(DefaultViewSpanBlocks, 512, MaximumZoomOutSpanBlocks);
		TerrainTileSize = Math.Clamp(TerrainTileSize, 128, 512);
		TerrainTileSize = Math.Max(128, TerrainTileSize / 32 * 32);
		TerrainPyramidWorkers = Math.Clamp(TerrainPyramidWorkers, 1, 4);
		TerrainBaseRenderWorkers = Math.Clamp(TerrainBaseRenderWorkers, 1, 4);
		if (!AllowLivePlayerData)
		{
			ShowPlayers = false;
		}
		if (!ExportNormalTerrain && !ExportSepiaTerrain && !ExportTopographicTerrain)
		{
			ExportNormalTerrain = true;
		}
	}

	private static bool TryParseScheduleTime(string? value, out string normalized)
	{
		normalized = "04:00";
		string[] array = (value ?? "").Trim().Split(':');
		if (array.Length != 2 || !int.TryParse(array[0], out var result) || !int.TryParse(array[1], out var result2) || result < 0 || result > 23 || result2 < 0 || result2 > 59)
		{
			return false;
		}
		normalized = $"{result:00}:{result2:00}";
		return true;
	}

	private static string NormalizeHttpUrl(string? value)
	{
		string text = (value ?? "").Trim();
		if (text.Length > 500)
		{
			text = text.Substring(0, 500);
		}
		if (text.Length == 0)
		{
			return "";
		}
		if (!Uri.TryCreate(text, UriKind.Absolute, out Uri result))
		{
			return "";
		}
		if (result.Scheme != Uri.UriSchemeHttp && result.Scheme != Uri.UriSchemeHttps)
		{
			return "";
		}
		return text;
	}

	public WebMapConfig Clone()
	{
		return new WebMapConfig
		{
			SchemaVersion = SchemaVersion,
			Enabled = Enabled,
			BindAddress = BindAddress,
			Port = Port,
			PublicUrl = PublicUrl,
			DashboardUrl = DashboardUrl,
			Title = Title,
			AllowLivePlayerData = AllowLivePlayerData,
			ShowPlayers = (AllowLivePlayerData && ShowPlayers),
			PlayerIconRefreshSeconds = PlayerIconRefreshSeconds,
			ShowBadBlockEntities = ShowBadBlockEntities,
			ShowActivity = ShowActivity,
			ShowWaypoints = ShowWaypoints,
			ShowLeaderboards = ShowLeaderboards,
			LeaderboardShowPlaytime = LeaderboardShowPlaytime,
			LeaderboardShowXSkills = LeaderboardShowXSkills,
			LeaderboardTopPlayers = LeaderboardTopPlayers,
			LeaderboardRefreshSeconds = LeaderboardRefreshSeconds,
			ExportNormalTerrain = ExportNormalTerrain,
			ExportSepiaTerrain = ExportSepiaTerrain,
			ExportTopographicTerrain = ExportTopographicTerrain,
			PlayerMajorWaypointLimit = PlayerMajorWaypointLimit,
			PlayerMinorWaypointLimit = PlayerMinorWaypointLimit,
			MaximumZoomOutSpanBlocks = MaximumZoomOutSpanBlocks,
			DefaultViewSpanBlocks = DefaultViewSpanBlocks,
			ActivityWindowMinutes = ActivityWindowMinutes,
			MaxActivityMarkers = MaxActivityMarkers,
			ExportTerrainOnStart = ExportTerrainOnStart,
			AutomaticStartupExportEnabled = AutomaticStartupExportEnabled,
			AutomaticStartupExportIntervalDays = AutomaticStartupExportIntervalDays,
			LastSuccessfulTerrainExportUnixMilliseconds = LastSuccessfulTerrainExportUnixMilliseconds,
			LastTerrainExportDurationMilliseconds = LastTerrainExportDurationMilliseconds,
			LockServerDuringTerrainExport = LockServerDuringTerrainExport,
			UseIncrementalTerrainExport = UseIncrementalTerrainExport,
			UseDirectSaveDatabaseTerrain = UseDirectSaveDatabaseTerrain,
			TerrainPyramidWorkers = TerrainPyramidWorkers,
			TerrainBaseRenderWorkers = TerrainBaseRenderWorkers,
			ScheduledTerrainExportEnabled = ScheduledTerrainExportEnabled,
			ScheduledTerrainExportTime = ScheduledTerrainExportTime,
			ScheduledTerrainExportOnlyWhenEmpty = ScheduledTerrainExportOnlyWhenEmpty,
			TerrainTileSize = TerrainTileSize
		};
	}
}
