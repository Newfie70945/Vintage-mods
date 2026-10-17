using System.Collections.Generic;

namespace BlockLogger.WebDashboard;

public sealed class DatabaseStorageViewerSnapshot
{
    public long CapturedUnixMilliseconds { get; set; }
    public bool DetailedAnalysisAvailable { get; set; }
    public long DetailedAnalysisUnixMilliseconds { get; set; }
    public string Message { get; set; } = "";
    public List<DatabaseStorageDatabaseSnapshot> Databases { get; set; } = new();
    public List<DatabaseStorageRecommendation> Recommendations { get; set; } = new();
}

public sealed class DatabaseStorageDatabaseSnapshot
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string FileName { get; set; } = "";
    public bool Available { get; set; }
    public bool Detailed { get; set; }
    public bool PayloadEstimates {get;set;}
    public bool DbStatAvailable { get; set; }
    public string AnalysisMessage { get; set; } = "";
    public long DatabaseBytes { get; set; }
    public long WalBytes { get; set; }
    public long ShmBytes { get; set; }
    public long BackupBytes { get; set; }
    public int BackupCount { get; set; }
    public long PageSizeBytes { get; set; }
    public long PageCount { get; set; }
    public long LogicalDatabaseBytes { get; set; }
    public long FreePageCount { get; set; }
    public long FreeBytes { get; set; }
    public long TableDataBytes { get; set; }
    public long IndexBytes { get; set; }
    public long SQLiteOverheadBytes { get; set; }
    public int RetentionDays { get; set; }
    public List<DatabaseStorageContentSnapshot> Contents { get; set; } = new();
    public List<DatabaseStorageTableSnapshot> Tables { get; set; } = new();
    public List<DatabaseStorageRecommendation> Recommendations { get; set; } = new();
}

public sealed class DatabaseStorageContentSnapshot
{
    public string Subsystem { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string TableName { get; set; } = "";
    public string Description { get; set; } = "";
    public string StoredFields { get; set; } = "";
    public bool Present { get; set; }
    public bool Analyzed { get; set; }
    public long ApproxRows { get; set; }
    public long DataBytes { get; set; }
    public long IndexBytes { get; set; }
    public long TotalBytes { get; set; }
}

public sealed class DatabaseStorageTableSnapshot
{
    public bool SizeEstimated {get;set;}
    public bool RowCountKnown {get;set;}
    public int SampledRows {get;set;}
    public double AveragePayloadBytes {get;set;}
    public long EstimatedPayloadBytes {get;set;}
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Subsystem { get; set; } = "";
    public string StoredFields { get; set; } = "";
    public long ApproxRows { get; set; }
    public long DataBytes { get; set; }
    public long IndexBytes { get; set; }
    public long TotalBytes { get; set; }
    public double PercentOfDatabase { get; set; }
    public string Description { get; set; } = "";
    public string GrowthDriver { get; set; } = "";
    public string Improvement { get; set; } = "";
    public string Safety { get; set; } = "Safe";
}

public sealed class DatabaseStorageRecommendation
{
    public string Severity { get; set; } = "Info";
    public string Database { get; set; } = "";
    public string Area { get; set; } = "";
    public string Finding { get; set; } = "";
    public string Improvement { get; set; } = "";
    public string Safety { get; set; } = "Safe";
}
