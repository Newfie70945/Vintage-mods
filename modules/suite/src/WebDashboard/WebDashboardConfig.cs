using System;
using System.Collections.Generic;

namespace BlockLogger.WebDashboard;

public sealed class WebDashboardConfig
{
    public const int CurrentSchemaVersion = 2;
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    // Newfies Server Essentials Suite starts the embedded dashboard locally by default.
    // Existing configs keep their saved Enabled value on upgrade.
    public bool Enabled { get; set; } = true;

    // Fresh installs are loopback-only. Remote access remains supported, but
    // changing to 0.0.0.0/LAN/public IP requires an admin password first.
    public string BindAddress { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 9465;

    // Optional externally reachable HTTPS URL used by the in-game Profiler
    // "Open Public Web" button, e.g. https://server.example.com/.
    public string PublicUrl { get; set; } = "";

    public string AdminUsername { get; set; } = "admin";

    // One-time bootstrap field. If non-empty when the config is loaded it is
    // immediately PBKDF2-hashed, this field is cleared, and the config is rewritten.
    public string Password { get; set; } = "";

    public string PasswordSalt { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public int PasswordIterations { get; set; } = 210_000;

    public int SessionTimeoutMinutes { get; set; } = 60;
    public int MaxFailedLogins { get; set; } = 5;
    public int LockoutMinutes { get; set; } = 10;

    // When true the session cookie gets the Secure flag. Turn this on when the
    // dashboard is reached through an HTTPS reverse proxy.
    public bool SecureCookies { get; set; } = false;
    public bool SafeDiagnosticsMode { get; set; } = false;
    // Full engine profiling adds work to every tick, even when we sample its output less often.
    // Opt in explicitly; ordinary diagnostics and manual captures remain independently usable.
    public bool AutomaticIncidentProfilingEnabled { get; set; } = false;
    public bool AllowSameMinorGameModUpdates { get; set; } = false;
    public bool TickDebuggingEnabled { get; set; } = true;

    // Dedicated bounded HTTP worker pool. Network parsing, authentication,
    // serialization, file/database reads, and cached dashboard responses stay
    // off the Vintage Story main thread. Live game mutations still cross the
    // explicit main-thread bridge.
    public int WebWorkerCount { get; set; } = 2;
    public int WebRequestQueueCapacity { get; set; } = 64;
    public int WebQueueMaxWaitMs { get; set; } = 5000;

    // Cached read-only snapshots use stale-while-refresh semantics. A request
    // may receive the previous good snapshot while one main-thread refresh is
    // queued, avoiding routine browser polling stalls on the server tick.
    public int LiveSnapshotMaxAgeMs { get; set; } = 1500;
    public int DashboardSnapshotMaxAgeMs { get; set; } = 2500;

    // Bounded auxiliary-history retention. These are item counts, not days.
    public int MaxArchivedIncidents { get; set; } = 100;
    public int MaxEntityScanHistory { get; set; } = 12;
    public int MaxEnvironmentChanges { get; set; } = 30;
    public int MaxAlerts { get; set; } = 250;
    public int MaxActions { get; set; } = 250;
    public int MaxWebAuditEntries { get; set; } = 500;
    public int MaxConfigSnapshots { get; set; } = 20;
    public int MaxPerformanceSnapshots { get; set; } = 30;
    public int MaxWorldSaveObservations { get; set; } = 100;
    public int MaxNetworkSamples { get; set; } = 120;
    public int MaxOptimizationExperiments { get; set; } = 30;

    // Empty means any source IP may reach the login page. Entries may be exact
    // IPv4/IPv6 addresses or CIDR ranges such as 192.168.1.0/24 or 2001:db8::/64.
    public List<string> AllowedNetworks { get; set; } = new();

    public void Clamp()
    {
        SchemaVersion = Math.Max(CurrentSchemaVersion, SchemaVersion);

        BindAddress = string.IsNullOrWhiteSpace(BindAddress)
            ? "127.0.0.1"
            : BindAddress.Trim();

        Port = Math.Clamp(Port, 1, 65535);

        PublicUrl = (PublicUrl ?? "").Trim();
        if (PublicUrl.Length > 512)
            PublicUrl = PublicUrl[..512];

        AdminUsername = string.IsNullOrWhiteSpace(AdminUsername)
            ? "admin"
            : AdminUsername.Trim();

        if (AdminUsername.Length > 64)
            AdminUsername = AdminUsername[..64];

        PasswordIterations = Math.Clamp(PasswordIterations, 100_000, 1_000_000);
        SessionTimeoutMinutes = Math.Clamp(SessionTimeoutMinutes, 5, 1440);
        MaxFailedLogins = Math.Clamp(MaxFailedLogins, 1, 50);
        LockoutMinutes = Math.Clamp(LockoutMinutes, 1, 1440);

        WebWorkerCount = Math.Clamp(WebWorkerCount, 1, 8);
        WebRequestQueueCapacity = Math.Clamp(WebRequestQueueCapacity, 8, 512);
        WebQueueMaxWaitMs = Math.Clamp(WebQueueMaxWaitMs, 500, 30_000);
        LiveSnapshotMaxAgeMs = Math.Clamp(LiveSnapshotMaxAgeMs, 250, 10_000);
        DashboardSnapshotMaxAgeMs = Math.Clamp(DashboardSnapshotMaxAgeMs, 500, 15_000);

        MaxArchivedIncidents = Math.Clamp(MaxArchivedIncidents, 10, 1000);
        MaxEntityScanHistory = Math.Clamp(MaxEntityScanHistory, 2, 100);
        MaxEnvironmentChanges = Math.Clamp(MaxEnvironmentChanges, 5, 200);
        MaxAlerts = Math.Clamp(MaxAlerts, 25, 2000);
        MaxActions = Math.Clamp(MaxActions, 25, 2000);
        MaxWebAuditEntries = Math.Clamp(MaxWebAuditEntries, 50, 5000);
        MaxConfigSnapshots = Math.Clamp(MaxConfigSnapshots, 5, 100);
        MaxPerformanceSnapshots = Math.Clamp(MaxPerformanceSnapshots, 5, 200);
        MaxWorldSaveObservations = Math.Clamp(MaxWorldSaveObservations, 10, 1000);
        MaxNetworkSamples = Math.Clamp(MaxNetworkSamples, 30, 1440);
        MaxOptimizationExperiments = Math.Clamp(MaxOptimizationExperiments, 5, 200);

        AllowedNetworks ??= new List<string>();
        AllowedNetworks.RemoveAll(string.IsNullOrWhiteSpace);
        for (int i = 0; i < AllowedNetworks.Count; i++)
            AllowedNetworks[i] = AllowedNetworks[i].Trim();
    }
}
