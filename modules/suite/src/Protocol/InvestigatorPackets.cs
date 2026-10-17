using System.Collections.Generic;
using ProtoBuf;

namespace BlockLogger.Protocol;

[ProtoContract]
public sealed class InvestigatorQueryRequest
{
    [ProtoMember(1)] public long RequestId { get; set; }
    [ProtoMember(2)] public string View { get; set; } = "blocks";
    [ProtoMember(3)] public bool UseSelection { get; set; } = true;
    [ProtoMember(4)] public int Radius { get; set; } = 5;
    [ProtoMember(5)] public int Limit { get; set; } = 20;
    [ProtoMember(6)] public string Search { get; set; } = "";
    [ProtoMember(7)] public string Action { get; set; } = "";
}

[ProtoContract]
public sealed class InvestigatorQueryResponse
{
    [ProtoMember(1)] public long RequestId { get; set; }
    [ProtoMember(2)] public bool Success { get; set; }
    [ProtoMember(3)] public string View { get; set; } = "blocks";
    [ProtoMember(4)] public string Header { get; set; } = "";
    [ProtoMember(5)] public string Error { get; set; } = "";
    [ProtoMember(6)] public List<string> Lines { get; set; } = new();
}

[ProtoContract]
public sealed class InvestigatorStatusRequest
{
    [ProtoMember(1)] public long RequestId { get; set; }
}

[ProtoContract]
public sealed class InvestigatorStatusResponse
{
    [ProtoMember(1)] public long RequestId { get; set; }
    [ProtoMember(2)] public bool Success { get; set; }
    [ProtoMember(3)] public string Error { get; set; } = "";
    [ProtoMember(4)] public List<string> Lines { get; set; } = new();
}

[ProtoContract]
public sealed class BlockLoggerSettingsRequest
{
    [ProtoMember(1)] public long RequestId { get; set; }
    [ProtoMember(2)] public string Operation { get; set; } = "get";
    [ProtoMember(3)] public string SettingKey { get; set; } = "";
    [ProtoMember(4)] public int IntValue { get; set; }
    [ProtoMember(5)] public bool BoolValue { get; set; }
}

[ProtoContract]
public sealed class BlockLoggerSettingsResponse
{
    [ProtoMember(1)] public long RequestId { get; set; }
    [ProtoMember(2)] public bool Success { get; set; }
    [ProtoMember(3)] public string Message { get; set; } = "";

    [ProtoMember(4)] public bool Enabled { get; set; }
    [ProtoMember(5)] public bool LogBlockBreak { get; set; }
    [ProtoMember(6)] public bool LogBlockPlace { get; set; }
    [ProtoMember(7)] public bool LogBlockUse { get; set; }
    [ProtoMember(8)] public bool LogContainerAccess { get; set; }
    [ProtoMember(9)] public bool LogContainerItemChanges { get; set; }
    [ProtoMember(10)] public bool LogEntityInteract { get; set; }
    [ProtoMember(11)] public bool LogEntitySpawn { get; set; }
    [ProtoMember(12)] public bool LogEntityDeath { get; set; }
    [ProtoMember(13)] public bool LogEntityDespawn { get; set; }
    [ProtoMember(14)] public bool AggregateEntitySpawnDespawn { get; set; }

    [ProtoMember(15)] public int RetentionDays { get; set; }
    [ProtoMember(16)] public int FlushIntervalMs { get; set; }
    [ProtoMember(17)] public int MaxQueuedEvents { get; set; }
    [ProtoMember(18)] public int DefaultRadius { get; set; }
    [ProtoMember(19)] public int EntityRadius { get; set; }
    [ProtoMember(20)] public int MaxRadius { get; set; }
    [ProtoMember(21)] public int DefaultDisplayCount { get; set; }
    [ProtoMember(22)] public int DefaultEntityDisplayCount { get; set; }
    [ProtoMember(23)] public int MaxDisplayCount { get; set; }
    [ProtoMember(24)] public int EmergencyOverflowCapacity { get; set; }
}

[ProtoContract]
public sealed class AnimalLimiterRequest
{
    [ProtoMember(1)] public long RequestId { get; set; }
    [ProtoMember(2)] public string Operation { get; set; } = "status";
    [ProtoMember(3)] public string Pattern { get; set; } = "";
    [ProtoMember(4)] public int Limit { get; set; } = 20;
    [ProtoMember(5)] public int RadiusChunks { get; set; }
    [ProtoMember(6)] public int IntValue { get; set; }
    [ProtoMember(7)] public bool BoolValue { get; set; }
    [ProtoMember(8)] public string SettingKey { get; set; } = "";
}

[ProtoContract]
public sealed class AnimalLimiterResponse
{
    [ProtoMember(1)] public long RequestId { get; set; }
    [ProtoMember(2)] public bool Success { get; set; }
    [ProtoMember(3)] public string Message { get; set; } = "";
    [ProtoMember(4)] public List<string> Lines { get; set; } = new();

    // Current server-side settings, returned with every Animal Limiter response so the
    // GUI never has to assume its local values are authoritative.
    [ProtoMember(5)] public bool Enabled { get; set; }
    [ProtoMember(6)] public int RadiusChunks { get; set; }
    [ProtoMember(7)] public int DefaultPerEntityLimit { get; set; }
    [ProtoMember(8)] public int PlayerProtectionRadiusBlocks { get; set; }
    [ProtoMember(9)] public int ReconciliationSeconds { get; set; }
    [ProtoMember(10)] public bool LimitAllEntities { get; set; }
    [ProtoMember(11)] public bool ProtectPets { get; set; }
    [ProtoMember(12)] public bool ProtectFarmAnimals { get; set; }
    [ProtoMember(13)] public bool ProtectNamedEntities { get; set; }
    [ProtoMember(14)] public bool KeepNearestToPlayers { get; set; }
    [ProtoMember(15)] public bool LogActions { get; set; }
    [ProtoMember(16)] public bool LogToCsv { get; set; }
    [ProtoMember(17)] public string GenerationFilter { get; set; } = "";
}

[ProtoContract]
public sealed class ProfilerRequest
{
    [ProtoMember(1)] public long RequestId { get; set; }
    [ProtoMember(2)] public string Operation { get; set; } = "get";
    [ProtoMember(3)] public int DurationSeconds { get; set; } = 10;
    [ProtoMember(4)] public bool BoolValue { get; set; }
    // Lightweight live refresh: current counters/status only, no rolling history.
    [ProtoMember(5)] public bool Compact { get; set; }
}

[ProtoContract]
public sealed class ProfilerResponse
{
    [ProtoMember(1)] public long RequestId { get; set; }
    [ProtoMember(2)] public bool Success { get; set; }
    [ProtoMember(3)] public string Message { get; set; } = "";
    [ProtoMember(4)] public List<ProfilerSamplePacket> Samples { get; set; } = new();
    [ProtoMember(5)] public List<ProfilerSlowSpikePacket> SlowSpikes { get; set; } = new();
    [ProtoMember(6)] public List<ProfilerHotspotPacket> Hotspots { get; set; } = new();
    [ProtoMember(7)] public bool ProfileRunning { get; set; }
    [ProtoMember(8)] public int ProfileDurationSeconds { get; set; }
    [ProtoMember(9)] public int ProfileRemainingSeconds { get; set; }
    [ProtoMember(10)] public ProfilerSamplePacket Current { get; set; } = new();
    [ProtoMember(11)] public ProfilerProfileSummaryPacket LastProfile { get; set; } = new();
    [ProtoMember(12)] public bool Enabled { get; set; }

    // Appended fields preserve the existing profiler packet tags.
    [ProtoMember(13)] public List<ProfilerModHotspotPacket> ModHotspots { get; set; } = new();
    [ProtoMember(14)] public List<ProfilerPhasePacket> Phases { get; set; } = new();
    [ProtoMember(15)] public List<ProfilerProfileSlowTickPacket> ProfileSlowTicks { get; set; } = new();
    [ProtoMember(16)] public int LoadedModCount { get; set; }
    [ProtoMember(17)] public List<ProfilerOffThreadReportPacket> OffThreadReports { get; set; } = new();
    [ProtoMember(18)] public List<ProfilerDiagnosticIncidentPacket> DiagnosticIncidents { get; set; } = new();
    [ProtoMember(19)] public ProfilerDiagnosticSummaryPacket DiagnosticSummary { get; set; } = new();
    [ProtoMember(20)] public string LocalWebUrl { get; set; } = "";
    [ProtoMember(21)] public string PublicWebUrl { get; set; } = "";
    // True when this packet intentionally omits history/profile tables for a low-cost live update.
    [ProtoMember(22)] public bool Compact { get; set; }
}

[ProtoContract]
public sealed class ProfilerSamplePacket
{
    [ProtoMember(1)] public long UnixMilliseconds { get; set; }
    [ProtoMember(2)] public double MainThreadDelayMs { get; set; }
    [ProtoMember(3)] public double CpuPercent { get; set; }
    [ProtoMember(4)] public double WorkingSetMb { get; set; }
    [ProtoMember(5)] public double ManagedHeapMb { get; set; }
    [ProtoMember(6)] public int Gen0Collections { get; set; }
    [ProtoMember(7)] public int Gen1Collections { get; set; }
    [ProtoMember(8)] public int Gen2Collections { get; set; }
    [ProtoMember(9)] public int Players { get; set; }
    [ProtoMember(10)] public int LoadedEntities { get; set; }
    [ProtoMember(11)] public int LoadedChunks { get; set; }
    [ProtoMember(12)] public int BlockLoggerQueue { get; set; }
    [ProtoMember(13)] public long BlockLoggerWriterMs { get; set; }
    [ProtoMember(14)] public long BlockLoggerDropped { get; set; }
    [ProtoMember(15)] public int AnimalTrackedEntities { get; set; }
    [ProtoMember(16)] public int AnimalTrackedRegions { get; set; }
}

[ProtoContract]
public sealed class ProfilerSlowSpikePacket
{
    [ProtoMember(1)] public long UnixMilliseconds { get; set; }
    [ProtoMember(2)] public double DelayMs { get; set; }
    [ProtoMember(3)] public double CpuPercent { get; set; }
    [ProtoMember(4)] public double WorkingSetMb { get; set; }
    [ProtoMember(5)] public int Players { get; set; }
    [ProtoMember(6)] public int LoadedEntities { get; set; }
    [ProtoMember(7)] public int LoadedChunks { get; set; }
    [ProtoMember(8)] public int BlockLoggerQueue { get; set; }
    [ProtoMember(9)] public long BlockLoggerWriterMs { get; set; }
    [ProtoMember(10)] public int Gen0Delta { get; set; }
    [ProtoMember(11)] public int Gen1Delta { get; set; }
    [ProtoMember(12)] public int Gen2Delta { get; set; }
}

[ProtoContract]
public sealed class ProfilerHotspotPacket
{
    [ProtoMember(1)] public string Code { get; set; } = "";
    [ProtoMember(2)] public double TotalMs { get; set; }
    [ProtoMember(3)] public double MaxSingleMs { get; set; }
    [ProtoMember(4)] public long Calls { get; set; }
    [ProtoMember(5)] public double PercentOfProfileTickTime { get; set; }
    [ProtoMember(6)] public string ModId { get; set; } = "";
    [ProtoMember(7)] public string ModName { get; set; } = "";
    [ProtoMember(8)] public string AttributionConfidence { get; set; } = "Low";
    [ProtoMember(9)] public bool IsCore { get; set; }
}

[ProtoContract]
public sealed class ProfilerModHotspotPacket
{
    [ProtoMember(1)] public string ModId { get; set; } = "";
    [ProtoMember(2)] public string ModName { get; set; } = "";
    [ProtoMember(3)] public double TotalMs { get; set; }
    [ProtoMember(4)] public double MaxSingleMs { get; set; }
    [ProtoMember(5)] public long Calls { get; set; }
    [ProtoMember(6)] public double PercentOfProfileTickTime { get; set; }
    [ProtoMember(7)] public string Confidence { get; set; } = "Low";
    [ProtoMember(8)] public string TopMarker { get; set; } = "";
    [ProtoMember(9)] public bool IsCore { get; set; }
    [ProtoMember(10)] public int SlowSegments { get; set; }
}

[ProtoContract]
public sealed class ProfilerPhasePacket
{
    [ProtoMember(1)] public string Code { get; set; } = "";
    [ProtoMember(2)] public double TotalMs { get; set; }
    [ProtoMember(3)] public double AveragePerTickMs { get; set; }
    [ProtoMember(4)] public double MaxSingleMs { get; set; }
    [ProtoMember(5)] public long Calls { get; set; }
    [ProtoMember(6)] public double PercentOfProfileTickTime { get; set; }
    [ProtoMember(7)] public string ModId { get; set; } = "";
    [ProtoMember(8)] public string AttributionConfidence { get; set; } = "Low";
}

[ProtoContract]
public sealed class ProfilerProfileSlowTickPacket
{
    [ProtoMember(1)] public long UnixMilliseconds { get; set; }
    [ProtoMember(2)] public double TickMs { get; set; }
    [ProtoMember(3)] public string TopCauseSummary { get; set; } = "";
}

[ProtoContract]
public sealed class ProfilerOffThreadReportPacket
{
    [ProtoMember(1)] public double DurationMs { get; set; }
    [ProtoMember(2)] public string Summary { get; set; } = "";
    [ProtoMember(3)] public string ModId { get; set; } = "";
    [ProtoMember(4)] public string Confidence { get; set; } = "Low";
}

[ProtoContract]
public sealed class ProfilerDiagnosticIncidentPacket
{
    [ProtoMember(1)] public long UnixMilliseconds { get; set; }
    [ProtoMember(2)] public string Severity { get; set; } = "Watch";
    [ProtoMember(3)] public double DelayMs { get; set; }
    [ProtoMember(4)] public double CpuPercent { get; set; }
    [ProtoMember(5)] public double ManagedHeapMb { get; set; }
    [ProtoMember(6)] public int LoadedEntities { get; set; }
    [ProtoMember(7)] public int EntityDelta { get; set; }
    [ProtoMember(8)] public int LoadedChunks { get; set; }
    [ProtoMember(9)] public int ChunkDelta { get; set; }
    [ProtoMember(10)] public int BlockLoggerQueue { get; set; }
    [ProtoMember(11)] public long BlockLoggerWriterMs { get; set; }
    [ProtoMember(12)] public long BlockLoggerDroppedDelta { get; set; }
    [ProtoMember(13)] public int Gen0Delta { get; set; }
    [ProtoMember(14)] public int Gen1Delta { get; set; }
    [ProtoMember(15)] public int Gen2Delta { get; set; }
    [ProtoMember(16)] public string Evidence { get; set; } = "";
}

[ProtoContract]
public sealed class ProfilerDiagnosticSummaryPacket
{
    [ProtoMember(1)] public string State { get; set; } = "Disabled";
    [ProtoMember(2)] public int WindowMinutes { get; set; } = 5;
    [ProtoMember(3)] public string Summary { get; set; } = "";
    [ProtoMember(4)] public int StallCount { get; set; }
    [ProtoMember(5)] public int SevereStallCount { get; set; }
    [ProtoMember(6)] public double WorstDelayMs { get; set; }
    [ProtoMember(7)] public double AverageCpuPercent { get; set; }
    [ProtoMember(8)] public double MaxCpuPercent { get; set; }
    [ProtoMember(9)] public int Gen2Collections { get; set; }
    [ProtoMember(10)] public int MaxBlockLoggerQueue { get; set; }
    [ProtoMember(11)] public long MaxBlockLoggerWriterMs { get; set; }
    [ProtoMember(12)] public long BlockLoggerDroppedIncrease { get; set; }
    [ProtoMember(13)] public int MaxLoadedEntities { get; set; }
    [ProtoMember(14)] public int MaxLoadedChunks { get; set; }
}

[ProtoContract]
public sealed class ProfilerProfileSummaryPacket
{
    [ProtoMember(1)] public long CompletedUnixMilliseconds { get; set; }
    [ProtoMember(2)] public int DurationSeconds { get; set; }
    [ProtoMember(3)] public int TickCount { get; set; }
    [ProtoMember(4)] public double AverageTickMs { get; set; }
    [ProtoMember(5)] public double P95TickMs { get; set; }
    [ProtoMember(6)] public double P99TickMs { get; set; }
    [ProtoMember(7)] public double MaxTickMs { get; set; }
    [ProtoMember(8)] public int AttributedSegments { get; set; }
    [ProtoMember(9)] public int UnattributedSegments { get; set; }
    [ProtoMember(10)] public double AttributedMs { get; set; }
    [ProtoMember(11)] public double UnattributedMs { get; set; }
}

[ProtoContract]
public sealed class LoginProtectionRequest
{
    [ProtoMember(1)] public long RequestId { get; set; }
    [ProtoMember(2)] public string Operation { get; set; } = "status";
    [ProtoMember(3)] public string SettingKey { get; set; } = "";
    [ProtoMember(4)] public int IntValue { get; set; }
    [ProtoMember(5)] public bool BoolValue { get; set; }
}

[ProtoContract]
public sealed class LoginProtectionResponse
{
    [ProtoMember(1)] public long RequestId { get; set; }
    [ProtoMember(2)] public bool Success { get; set; }
    [ProtoMember(3)] public string Message { get; set; } = "";
    [ProtoMember(4)] public List<string> Lines { get; set; } = new();

    [ProtoMember(5)] public bool Enabled { get; set; }
    [ProtoMember(6)] public int ProtectionSeconds { get; set; }
    [ProtoMember(7)] public bool ProtectOnJoin { get; set; }
    [ProtoMember(8)] public bool ProtectOnRespawn { get; set; }
    [ProtoMember(9)] public bool AnnounceOnJoin { get; set; }
    [ProtoMember(10)] public bool AnnounceOnEnd { get; set; }
    [ProtoMember(11)] public bool BlockPvP { get; set; }
    [ProtoMember(12)] public bool CancelOnAttack { get; set; }
    [ProtoMember(13)] public bool ProtectFallDamage { get; set; }
    [ProtoMember(14)] public bool ExtinguishFire { get; set; }
    [ProtoMember(15)] public int ActiveProtectedCount { get; set; }
    [ProtoMember(16)] public bool RecoverSwimmingOnLogin { get; set; }
}

[ProtoContract]
public sealed class WebDashboardRequest
{
    [ProtoMember(1)] public long RequestId { get; set; }
    [ProtoMember(2)] public string Operation { get; set; } = "status";
    [ProtoMember(3)] public string SettingKey { get; set; } = "";
    [ProtoMember(4)] public string StringValue { get; set; } = "";
    [ProtoMember(5)] public int IntValue { get; set; }
    [ProtoMember(6)] public bool BoolValue { get; set; }
    [ProtoMember(7)] public WebMapGuiSettingsPacket? WebMapSettings { get; set; }
}

[ProtoContract]
public sealed class WebDashboardResponse
{
    [ProtoMember(1)] public long RequestId { get; set; }
    [ProtoMember(2)] public bool Success { get; set; }
    [ProtoMember(3)] public string Message { get; set; } = "";
    [ProtoMember(4)] public List<string> Lines { get; set; } = new();
    [ProtoMember(5)] public bool Enabled { get; set; }
    [ProtoMember(6)] public bool Running { get; set; }
    [ProtoMember(7)] public string BindAddress { get; set; } = "";
    [ProtoMember(8)] public int Port { get; set; }
    [ProtoMember(9)] public string AdminUsername { get; set; } = "";
    [ProtoMember(10)] public bool PasswordConfigured { get; set; }
    [ProtoMember(11)] public int ActiveSessions { get; set; }
    [ProtoMember(12)] public int SessionTimeoutMinutes { get; set; }
    [ProtoMember(13)] public bool SecureCookies { get; set; }
    [ProtoMember(14)] public string LastError { get; set; } = "";
    [ProtoMember(15)] public bool LocalAnonymousMode { get; set; }
    [ProtoMember(16)] public string LocalUrl { get; set; } = "";
    [ProtoMember(17)] public string PublicUrl { get; set; } = "";
    [ProtoMember(18)] public WebMapGuiSettingsPacket? WebMapSettings { get; set; }
}

[ProtoContract]
public sealed class WebMapGuiSettingsPacket
{
    [ProtoMember(1)] public bool Enabled { get; set; }
    [ProtoMember(2)] public bool Running { get; set; }
    [ProtoMember(3)] public string BindAddress { get; set; } = "0.0.0.0";
    [ProtoMember(4)] public int Port { get; set; } = 9466;
    [ProtoMember(5)] public string Title { get; set; } = "Newfie's Server Map";
    [ProtoMember(6)] public string PublicUrl { get; set; } = "";
    [ProtoMember(7)] public string DashboardUrl { get; set; } = "";
    [ProtoMember(8)] public bool ShowPlayers { get; set; }
    [ProtoMember(9)] public bool ShowWaypoints { get; set; } = true;
    [ProtoMember(10)] public bool ShowActivity { get; set; }
    [ProtoMember(11)] public bool ShowBadBlockEntities { get; set; }
    [ProtoMember(12)] public int PlayerMajorWaypointLimit { get; set; } = 5;
    [ProtoMember(13)] public int PlayerMinorWaypointLimit { get; set; } = 10;
    [ProtoMember(14)] public int MaximumZoomOutSpanBlocks { get; set; } = 50000;
    [ProtoMember(15)] public int DefaultViewSpanBlocks { get; set; } = 8000;
    [ProtoMember(16)] public int TerrainTileSize { get; set; } = 256;
    [ProtoMember(17)] public int ActivityWindowMinutes { get; set; } = 60;
    [ProtoMember(18)] public int MaxActivityMarkers { get; set; } = 150;
    [ProtoMember(19)] public bool ExportTerrainOnStart { get; set; }
    [ProtoMember(20)] public bool AutomaticStartupExportEnabled { get; set; }
    [ProtoMember(21)] public int AutomaticStartupExportIntervalDays { get; set; } = 7;
    [ProtoMember(22)] public bool UseIncrementalTerrainExport { get; set; } = true;
    [ProtoMember(23)] public bool LockServerDuringTerrainExport { get; set; } = true;
    [ProtoMember(24)] public bool TerrainExportInProgress { get; set; }
    [ProtoMember(25)] public string LastError { get; set; } = "";
}


[ProtoContract]
public sealed class AntiCheatGuiSettingsPacket
{
    [ProtoMember(1)] public bool RequireClient { get; set; } = true;
    [ProtoMember(2)] public int RescanMinutes { get; set; } = 15;
    [ProtoMember(3)] public bool RuntimeRescanOnChange { get; set; } = true;
    [ProtoMember(4)] public int RuntimeChangeRescanCooldownSeconds { get; set; } = 30;
    [ProtoMember(5)] public bool SimilarityDetectionEnabled { get; set; } = true;
    [ProtoMember(6)] public int SimilarityReviewThresholdPercent { get; set; } = 72;
    [ProtoMember(7)] public int SimilarityHighThresholdPercent { get; set; } = 88;

    [ProtoMember(8)] public bool PacketGuardEnabled { get; set; } = true;
    [ProtoMember(9)] public bool MovementEnabled { get; set; } = true;
    [ProtoMember(10)] public double HardMoveDistance { get; set; } = 32;
    [ProtoMember(11)] public bool RejectUnauthorizedMovement { get; set; } = true;
    [ProtoMember(12)] public bool ReachEnabled { get; set; } = true;
    [ProtoMember(13)] public double ReachTolerance { get; set; } = 1.25;
    [ProtoMember(14)] public double ReachHardExcess { get; set; } = 1.5;
    [ProtoMember(15)] public bool RejectOutOfReachActions { get; set; } = true;

    [ProtoMember(16)] public bool MiningEnabled { get; set; } = true;
    [ProtoMember(17)] public bool MiningCadenceValidationEnabled { get; set; } = false;
    [ProtoMember(18)] public double MiningCadenceSuspiciousRatio { get; set; } = 0.38;
    [ProtoMember(19)] public int MiningCadenceConfirmations { get; set; } = 3;
    [ProtoMember(20)] public bool RejectConnectedBreakBursts { get; set; } = true;
    [ProtoMember(21)] public bool RejectImpossibleMiningCadence { get; set; }

    [ProtoMember(22)] public bool TpsAwarenessEnabled { get; set; } = true;
    [ProtoMember(23)] public double TpsDegradedPercent { get; set; } = 85;
    [ProtoMember(24)] public double TpsCriticalPercent { get; set; } = 60;
    [ProtoMember(25)] public double TpsMaxTimingMultiplier { get; set; } = 2.5;
    [ProtoMember(26)] public bool TpsSuppressTimingRejectsWhenCritical { get; set; } = true;

    [ProtoMember(27)] public bool IncidentCorrelationEnabled { get; set; } = true;
    [ProtoMember(28)] public int IncidentCorrelationWindowSeconds { get; set; } = 90;
    [ProtoMember(29)] public int IncidentCorrelationRequiredCategories { get; set; } = 2;
    [ProtoMember(30)] public int IncidentCorrelationMaxRiskBonus { get; set; } = 20;

    [ProtoMember(31)] public bool HistoryEnabled { get; set; } = true;
    [ProtoMember(32)] public int HistoryRetentionDays { get; set; } = 90;
    [ProtoMember(33)] public int HistoryMaxRecords { get; set; } = 500;
    [ProtoMember(34)] public bool Th3EssentialsSpawnCompatibility { get; set; } = true;
    [ProtoMember(35)] public int Th3EssentialsSpawnGraceMs { get; set; } = 8000;
    [ProtoMember(36)] public bool Th3EssentialsDetected { get; set; }
}

[ProtoContract]
public sealed class AntiCheatPacketProtectionGuiPacket
{
    [ProtoMember(1)] public bool PacketGuardEnabled { get; set; } = true;
    [ProtoMember(2)] public bool PlayerWarningEnabled { get; set; } = false;
    [ProtoMember(3)] public int PlayerWarningCooldownSeconds { get; set; } = 15;
    [ProtoMember(4)] public int AccelerationSampleMinMs { get; set; } = 220;
    [ProtoMember(5)] public double AccelerationSmoothingFactor { get; set; } = 0.25;
    [ProtoMember(6)] public bool NoclipEnabled { get; set; } = false;
    [ProtoMember(7)] public int NoclipRequiredCollisionSamples { get; set; } = 3;
    [ProtoMember(8)] public double NoclipMinPenetrationDepth { get; set; } = 0.18;
    [ProtoMember(9)] public double NoclipEndpointMinNetMovement { get; set; } = 0.45;
    [ProtoMember(10)] public double MountAccelerationMinSpeedRatio { get; set; } = 0.85;
    [ProtoMember(11)] public bool DiagnosticContextEnabled { get; set; } = true;
    [ProtoMember(12)] public bool DiagnosticSuppressionEnabled { get; set; } = true;
    [ProtoMember(13)] public int DiagnosticSpeedHistorySamples { get; set; } = 8;

    [ProtoMember(14)] public bool MovementEnabled { get; set; } = true;
    [ProtoMember(15)] public int MovementSampleMs { get; set; } = 75;
    [ProtoMember(16)] public double HardMoveDistance { get; set; } = 32.0;
    [ProtoMember(17)] public int MovementConfirmSamples { get; set; } = 2;
    [ProtoMember(18)] public int MovementConfirmWindowMs { get; set; } = 600;
    [ProtoMember(19)] public double MovementExtremeMultiplier { get; set; } = 2.75;
    [ProtoMember(20)] public int CorrectionGraceMs { get; set; } = 1200;
    [ProtoMember(21)] public bool RejectUnauthorizedMovement { get; set; } = true;
    [ProtoMember(22)] public bool ExemptAdmins { get; set; } = true;
    [ProtoMember(23)] public bool ExemptMountedPlayers { get; set; } = true;
    [ProtoMember(24)] public int MountGraceMs { get; set; } = 2000;
    [ProtoMember(25)] public int EngineTeleportGraceMs { get; set; } = 5000;
    [ProtoMember(26)] public bool TPRunesCompatibility { get; set; } = true;
    [ProtoMember(27)] public int TPRunesGraceMs { get; set; } = 8000;
    [ProtoMember(28)] public bool TPRunesDetected { get; set; }
    [ProtoMember(29)] public bool Th3EssentialsSpawnCompatibility { get; set; } = true;
    [ProtoMember(30)] public int Th3EssentialsSpawnGraceMs { get; set; } = 8000;
    [ProtoMember(31)] public bool Th3EssentialsDetected { get; set; }

    [ProtoMember(32)] public bool ReachEnabled { get; set; } = true;
    [ProtoMember(33)] public double ReachTolerance { get; set; } = 1.25;
    [ProtoMember(34)] public bool ReachLatencyCompensation { get; set; } = true;
    [ProtoMember(35)] public double ReachMaxLatencyCompensation { get; set; } = 1.25;
    [ProtoMember(36)] public bool ReachPositionHistoryEnabled { get; set; } = true;
    [ProtoMember(37)] public int ReachPositionHistoryMs { get; set; } = 650;
    [ProtoMember(38)] public int ReachMaxRewindMs { get; set; } = 350;
    [ProtoMember(39)] public double ReachUseExtraTolerance { get; set; } = 0.20;
    [ProtoMember(40)] public double ReachBreakExtraTolerance { get; set; } = 0.00;
    [ProtoMember(41)] public int ReachNearThresholdStrikes { get; set; } = 3;
    [ProtoMember(42)] public int ReachStrikeWindowMs { get; set; } = 3500;
    [ProtoMember(43)] public int ReachStrikeDecayMs { get; set; } = 1800;
    [ProtoMember(44)] public double ReachHardExcess { get; set; } = 1.5;
    [ProtoMember(45)] public bool RejectOutOfReachActions { get; set; } = true;

    [ProtoMember(46)] public bool MiningEnabled { get; set; } = true;
    [ProtoMember(47)] public int VeinWindowMs { get; set; } = 2200;
    [ProtoMember(48)] public int ConnectedBreakLimit { get; set; } = 18;
    [ProtoMember(49)] public int MiningDuplicateTargetMs { get; set; } = 450;
    [ProtoMember(50)] public bool RejectConnectedBreakBursts { get; set; } = true;
    [ProtoMember(51)] public bool MiningCadenceValidationEnabled { get; set; } = false;
    [ProtoMember(52)] public double MiningCadenceMinExpectedMs { get; set; } = 250;
    [ProtoMember(53)] public double MiningCadenceSuspiciousRatio { get; set; } = 0.38;
    [ProtoMember(54)] public int MiningCadenceConfirmations { get; set; } = 3;
    [ProtoMember(55)] public int MiningCadenceConfirmationWindowMs { get; set; } = 8000;
    [ProtoMember(56)] public bool RejectImpossibleMiningCadence { get; set; } = false;

    [ProtoMember(57)] public bool TpsAwarenessEnabled { get; set; } = true;
    [ProtoMember(58)] public double TpsDegradedPercent { get; set; } = 85.0;
    [ProtoMember(59)] public double TpsCriticalPercent { get; set; } = 60.0;
    [ProtoMember(60)] public double TpsMaxTimingMultiplier { get; set; } = 2.5;
    [ProtoMember(61)] public bool TpsSuppressTimingRejectsWhenCritical { get; set; } = true;
    [ProtoMember(62)] public bool TpsReady { get; set; }
    [ProtoMember(63)] public double TpsTarget { get; set; }
    [ProtoMember(64)] public double TpsEstimated { get; set; }
    [ProtoMember(65)] public double TpsHealthPercent { get; set; }
    [ProtoMember(66)] public string TpsBand { get; set; } = "Disabled";

    [ProtoMember(67)] public bool PerFamilyRiskEnabled { get; set; } = true;
    [ProtoMember(68)] public int MovementRiskDecaySeconds { get; set; } = 180;
    [ProtoMember(69)] public int MovementRiskReviewThreshold { get; set; } = 25;
    [ProtoMember(70)] public int MovementRiskSuspiciousThreshold { get; set; } = 50;
    [ProtoMember(71)] public int ReachRiskDecaySeconds { get; set; } = 300;
    [ProtoMember(72)] public int ReachRiskReviewThreshold { get; set; } = 25;
    [ProtoMember(73)] public int ReachRiskSuspiciousThreshold { get; set; } = 50;
    [ProtoMember(74)] public int MiningRiskDecaySeconds { get; set; } = 300;
    [ProtoMember(75)] public int MiningRiskReviewThreshold { get; set; } = 25;
    [ProtoMember(76)] public int MiningRiskSuspiciousThreshold { get; set; } = 50;
    [ProtoMember(77)] public int PacketAbuseRiskDecaySeconds { get; set; } = 600;
    [ProtoMember(78)] public int PacketAbuseRiskReviewThreshold { get; set; } = 25;
    [ProtoMember(79)] public int PacketAbuseRiskSuspiciousThreshold { get; set; } = 50;
    [ProtoMember(80)] public int ClientIntegrityRiskDecaySeconds { get; set; } = 1800;
    [ProtoMember(81)] public int ClientIntegrityRiskReviewThreshold { get; set; } = 10;
    [ProtoMember(82)] public int ClientIntegrityRiskSuspiciousThreshold { get; set; } = 50;

    [ProtoMember(83)] public bool IncidentCorrelationEnabled { get; set; } = true;
    [ProtoMember(84)] public int IncidentCorrelationWindowSeconds { get; set; } = 90;
    [ProtoMember(85)] public int IncidentCorrelationRequiredCategories { get; set; } = 2;
    [ProtoMember(86)] public int IncidentCorrelationMaxRiskBonus { get; set; } = 20;
    [ProtoMember(87)] public bool HistoryEnabled { get; set; } = true;
    [ProtoMember(88)] public int HistoryRetentionDays { get; set; } = 90;
    [ProtoMember(89)] public int HistoryMaxRecords { get; set; } = 500;
    [ProtoMember(90)] public bool IngressEnabled { get; set; }
    [ProtoMember(91)] public bool IngressActive { get; set; }
    [ProtoMember(92)] public string IngressStatus { get; set; } = "Unknown";
}

[ProtoContract]
public sealed class AntiCheatConsequenceRuleGuiPacket
{
    [ProtoMember(1)] public string Id { get; set; } = "";
    [ProtoMember(2)] public string Name { get; set; } = "";
    [ProtoMember(3)] public bool Enabled { get; set; }
    [ProtoMember(4)] public string TriggerType { get; set; } = "FindingCode";
    [ProtoMember(5)] public string TriggerValue { get; set; } = "";
    [ProtoMember(6)] public string Subject { get; set; } = "";
    [ProtoMember(7)] public string Action { get; set; } = "Log";
    [ProtoMember(8)] public string Duration { get; set; } = "30m";
    [ProtoMember(9)] public string Message { get; set; } = "";
}

[ProtoContract]
public sealed class AntiCheatGuiRequest
{
    [ProtoMember(1)] public long RequestId { get; set; }
    [ProtoMember(2)] public string Operation { get; set; } = "overview";
    [ProtoMember(3)] public string Search { get; set; } = "";
    [ProtoMember(4)] public bool BoolValue { get; set; }
    [ProtoMember(5)] public string Secondary { get; set; } = "";
    [ProtoMember(6)] public AntiCheatGuiSettingsPacket? Settings { get; set; }
    [ProtoMember(7)] public List<AntiCheatConsequenceRuleGuiPacket> ConsequenceRules { get; set; } = new();
    [ProtoMember(8)] public bool ApplyCurrent { get; set; }
    [ProtoMember(9)] public AntiCheatPacketProtectionGuiPacket? PacketSettings { get; set; }
}

[ProtoContract]
public sealed class AntiCheatGuiResponse
{
    [ProtoMember(1)] public long RequestId { get; set; }
    [ProtoMember(2)] public bool Success { get; set; }
    [ProtoMember(3)] public string Operation { get; set; } = "overview";
    [ProtoMember(4)] public string Message { get; set; } = "";
    [ProtoMember(5)] public List<string> Lines { get; set; } = new();
    [ProtoMember(6)] public string SelectedPlayer { get; set; } = "";
    [ProtoMember(7)] public bool Enabled { get; set; }
    [ProtoMember(8)] public AntiCheatGuiSettingsPacket? Settings { get; set; }
    [ProtoMember(9)] public List<AntiCheatConsequenceRuleGuiPacket> ConsequenceRules { get; set; } = new();
    [ProtoMember(10)] public AntiCheatPacketProtectionGuiPacket? PacketSettings { get; set; }
}

[ProtoContract]
public sealed class AdminCommandGuiRequest
{
    [ProtoMember(1)] public long RequestId { get; set; }
    [ProtoMember(2)] public string Operation { get; set; } = "player-info";
    [ProtoMember(3)] public string Target { get; set; } = "";
    [ProtoMember(4)] public string Value { get; set; } = "";
    [ProtoMember(5)] public string Secondary { get; set; } = "";
    [ProtoMember(6)] public string Reason { get; set; } = "";
    [ProtoMember(7)] public int IntValue { get; set; }
    [ProtoMember(8)] public int IntValue2 { get; set; }
    [ProtoMember(9)] public long LongValue { get; set; }
    [ProtoMember(10)] public bool Confirmed { get; set; }
    [ProtoMember(11)] public string ExpectedFingerprint { get; set; } = "";
}

[ProtoContract]
public sealed class AdminCommandGuiResponse
{
    [ProtoMember(1)] public long RequestId { get; set; }
    [ProtoMember(2)] public bool Success { get; set; }
    [ProtoMember(3)] public string Operation { get; set; } = "";
    [ProtoMember(4)] public string Message { get; set; } = "";
    [ProtoMember(5)] public List<string> Lines { get; set; } = new();
}

[ProtoContract]
public sealed class DiscordIntegrationGuiRequest
{
    [ProtoMember(1)] public long RequestId { get; set; }
    [ProtoMember(2)] public string Operation { get; set; } = "get";
    [ProtoMember(3)] public string SettingKey { get; set; } = "";
    [ProtoMember(4)] public bool BoolValue { get; set; }
    [ProtoMember(5)] public int IntValue { get; set; }
    [ProtoMember(6)] public string StringValue { get; set; } = "";
    [ProtoMember(7)] public string Target { get; set; } = "";
}

[ProtoContract]
public sealed class DiscordIntegrationGuiResponse
{
    [ProtoMember(1)] public long RequestId { get; set; }
    [ProtoMember(2)] public bool Success { get; set; }
    [ProtoMember(3)] public string Message { get; set; } = "";
    [ProtoMember(4)] public DiscordIntegrationGuiSettingsPacket Settings { get; set; } = new();
}

[ProtoContract]
public sealed class DiscordAnnouncementGuiPacket
{
    [ProtoMember(1)] public bool Enabled { get; set; }
    [ProtoMember(2)] public string ChannelId { get; set; } = "0";
    [ProtoMember(3)] public string Format { get; set; } = "";
}

[ProtoContract]
public sealed class DiscordIntegrationGuiSettingsPacket
{
    [ProtoMember(1)] public bool TransportAvailable { get; set; }
    [ProtoMember(2)] public bool TransportEnabled { get; set; }
    [ProtoMember(3)] public bool TransportConnected { get; set; }
    [ProtoMember(4)] public bool BotTokenConfigured { get; set; }
    [ProtoMember(5)] public string BotName { get; set; } = "";
    [ProtoMember(6)] public string GuildId { get; set; } = "";
    [ProtoMember(7)] public string InviteUrl { get; set; } = "";
    [ProtoMember(8)] public bool ChatEnabled { get; set; }
    [ProtoMember(9)] public string ChatChannelId { get; set; } = "0";
    [ProtoMember(10)] public bool GameToDiscord { get; set; }
    [ProtoMember(11)] public bool DiscordToGame { get; set; }
    [ProtoMember(12)] public bool AdminAuditEnabled { get; set; }
    [ProtoMember(13)] public string AdminAuditChannelId { get; set; } = "0";
    [ProtoMember(14)] public bool AntiCheatDiscordEnabled { get; set; }
    [ProtoMember(15)] public string AntiCheatChannelId { get; set; } = "0";
    [ProtoMember(16)] public bool LinkCommandsEnabled { get; set; }
    [ProtoMember(17)] public string Channels { get; set; } = "";
    [ProtoMember(18)] public DiscordAnnouncementGuiPacket PlayerJoin { get; set; } = new();
    [ProtoMember(19)] public DiscordAnnouncementGuiPacket PlayerLeave { get; set; } = new();
    [ProtoMember(20)] public DiscordAnnouncementGuiPacket PlayerDeath { get; set; } = new();
    [ProtoMember(21)] public DiscordAnnouncementGuiPacket ServerStarted { get; set; } = new();
    [ProtoMember(22)] public DiscordAnnouncementGuiPacket ServerStopping { get; set; } = new();
    [ProtoMember(23)] public DiscordAnnouncementGuiPacket DiscordLinked { get; set; } = new();
    [ProtoMember(24)] public DiscordAnnouncementGuiPacket DiscordUnlinked { get; set; } = new();
    [ProtoMember(25)] public bool LinkAvailable { get; set; }
    [ProtoMember(26)] public bool LinkEnabled { get; set; }
    [ProtoMember(27)] public int LinkCodeLength { get; set; } = 8;
    [ProtoMember(28)] public int LinkCodeExpirySeconds { get; set; } = 300;
    [ProtoMember(29)] public int LinkCodeCooldownSeconds { get; set; } = 30;
    [ProtoMember(30)] public bool LinkAllowMultipleVs { get; set; }
    [ProtoMember(31)] public bool LinkLogChanges { get; set; } = false;
    [ProtoMember(32)] public int LinkedAccountCount { get; set; }
    [ProtoMember(33)] public int PendingLinkCodeCount { get; set; }
    [ProtoMember(34)] public bool CommandsEnabled { get; set; }
    [ProtoMember(35)] public bool PlayerInfoCommandsEnabled { get; set; }
    [ProtoMember(36)] public bool StaffCommandsEnabled { get; set; }
    [ProtoMember(37)] public bool RestartCommandsEnabled { get; set; }
    [ProtoMember(38)] public bool GameBoldSenderLabel { get; set; } = true;
    [ProtoMember(39)] public bool GameUseRoleColor { get; set; }
    [ProtoMember(40)] public bool MotdAvailable { get; set; }
    [ProtoMember(41)] public string MotdVersion { get; set; } = "";
    [ProtoMember(42)] public string MotdConfigJson { get; set; } = "{}";
    [ProtoMember(43)] public string MotdMessage { get; set; } = "";
}
