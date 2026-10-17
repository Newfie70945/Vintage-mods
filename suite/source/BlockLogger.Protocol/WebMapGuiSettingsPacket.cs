using ProtoBuf;

namespace BlockLogger.Protocol;

[ProtoContract]
public sealed class WebMapGuiSettingsPacket
{
	[ProtoMember(1)]
	public bool Enabled { get; set; }

	[ProtoMember(2)]
	public bool Running { get; set; }

	[ProtoMember(3)]
	public string BindAddress { get; set; } = "0.0.0.0";

	[ProtoMember(4)]
	public int Port { get; set; } = 9466;

	[ProtoMember(5)]
	public string Title { get; set; } = "Newfie's Server Map";

	[ProtoMember(6)]
	public string PublicUrl { get; set; } = "";

	[ProtoMember(7)]
	public string DashboardUrl { get; set; } = "";

	[ProtoMember(8)]
	public bool ShowPlayers { get; set; }

	[ProtoMember(9)]
	public bool ShowWaypoints { get; set; } = true;

	[ProtoMember(10)]
	public bool ShowActivity { get; set; }

	[ProtoMember(11)]
	public bool ShowBadBlockEntities { get; set; }

	[ProtoMember(12)]
	public int PlayerMajorWaypointLimit { get; set; } = 5;

	[ProtoMember(13)]
	public int PlayerMinorWaypointLimit { get; set; } = 10;

	[ProtoMember(14)]
	public int MaximumZoomOutSpanBlocks { get; set; } = 50000;

	[ProtoMember(15)]
	public int DefaultViewSpanBlocks { get; set; } = 8000;

	[ProtoMember(16)]
	public int TerrainTileSize { get; set; } = 256;

	[ProtoMember(17)]
	public int ActivityWindowMinutes { get; set; } = 60;

	[ProtoMember(18)]
	public int MaxActivityMarkers { get; set; } = 150;

	[ProtoMember(19)]
	public bool ExportTerrainOnStart { get; set; }

	[ProtoMember(20)]
	public bool AutomaticStartupExportEnabled { get; set; }

	[ProtoMember(21)]
	public int AutomaticStartupExportIntervalDays { get; set; } = 7;

	[ProtoMember(22)]
	public bool UseIncrementalTerrainExport { get; set; } = true;

	[ProtoMember(23)]
	public bool LockServerDuringTerrainExport { get; set; } = true;

	[ProtoMember(24)]
	public bool TerrainExportInProgress { get; set; }

	[ProtoMember(25)]
	public string LastError { get; set; } = "";
}
