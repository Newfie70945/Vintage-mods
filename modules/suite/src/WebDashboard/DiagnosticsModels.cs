using System.Collections.Generic;

namespace BlockLogger.WebDashboard;

public sealed class WebServerDiagnosticsSnapshot
{
    public long CapturedUnixMilliseconds { get; set; }
    public int ActiveRequests { get; set; }
    public int ConnectionCapacity { get; set; }
    public int WorkerCount { get; set; }
    public int ActiveWorkers { get; set; }
    public int QueueDepth { get; set; }
    public int QueueCapacity { get; set; }
    public int PeakQueueDepth { get; set; }
    public long RejectedBusyRequests { get; set; }
    public double AverageQueueWaitMilliseconds { get; set; }
    public double P95QueueWaitMilliseconds { get; set; }
    public long SnapshotCacheHits { get; set; }
    public long SnapshotCacheMisses { get; set; }
    public long SnapshotRefreshes { get; set; }
    public long TotalRequests { get; set; }
    public long ErrorResponses { get; set; }
    public long UnauthorizedResponses { get; set; }
    public long RateLimitedResponses { get; set; }
    public long TotalResponseBytes { get; set; }
    public int RequestsLastMinute { get; set; }
    public double AverageRequestMilliseconds { get; set; }
    public double P95RequestMilliseconds { get; set; }
    public double MaxRequestMilliseconds { get; set; }
    public double AverageMainThreadWaitMilliseconds { get; set; }
    public double AverageMainThreadWorkMilliseconds { get; set; }
    public List<WebEndpointDiagnostic> Endpoints { get; set; } = new();
    public List<WebRequestDiagnostic> RecentSlowRequests { get; set; } = new();
}

public sealed class WebEndpointDiagnostic
{
    public string Method { get; set; } = "";
    public string Path { get; set; } = "";
    public long Requests { get; set; }
    public long Errors { get; set; }
    public long ResponseBytes { get; set; }
    public double AverageMilliseconds { get; set; }
    public double P95Milliseconds { get; set; }
    public double MaxMilliseconds { get; set; }
    public double AverageMainThreadWaitMilliseconds { get; set; }
    public double AverageMainThreadWorkMilliseconds { get; set; }
    public double AverageQueueWaitMilliseconds { get; set; }
}

public sealed class WebRequestDiagnostic
{
    public long UnixMilliseconds { get; set; }
    public string Method { get; set; } = "";
    public string Path { get; set; } = "";
    public int StatusCode { get; set; }
    public long ResponseBytes { get; set; }
    public double TotalMilliseconds { get; set; }
    public double QueueWaitMilliseconds { get; set; }
    public double MainThreadWaitMilliseconds { get; set; }
    public double MainThreadWorkMilliseconds { get; set; }
    public string Note { get; set; } = "";
}

public sealed class IncidentCauseScore
{
    public string Category { get; set; } = "";
    public int Score { get; set; }
    public string Confidence { get; set; } = "Low";
    public string Evidence { get; set; } = "";
}
