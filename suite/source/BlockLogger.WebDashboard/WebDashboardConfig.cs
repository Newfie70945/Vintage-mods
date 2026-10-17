using System;
using System.Collections.Generic;

namespace BlockLogger.WebDashboard;

public sealed class WebDashboardConfig
{
	public const int CurrentSchemaVersion = 2;

	public int SchemaVersion { get; set; } = 2;

	public bool Enabled { get; set; } = true;

	public string BindAddress { get; set; } = "127.0.0.1";

	public int Port { get; set; } = 9465;

	public string PublicUrl { get; set; } = "";

	public string AdminUsername { get; set; } = "admin";

	public string Password { get; set; } = "";

	public string PasswordSalt { get; set; } = "";

	public string PasswordHash { get; set; } = "";

	public int PasswordIterations { get; set; } = 210000;

	public int SessionTimeoutMinutes { get; set; } = 60;

	public int MaxFailedLogins { get; set; } = 5;

	public int LockoutMinutes { get; set; } = 10;

	public bool SecureCookies { get; set; } = false;

	public bool SafeDiagnosticsMode { get; set; } = false;

	public bool TickDebuggingEnabled { get; set; } = true;

	public int WebWorkerCount { get; set; } = 2;

	public int WebRequestQueueCapacity { get; set; } = 64;

	public int WebQueueMaxWaitMs { get; set; } = 5000;

	public int LiveSnapshotMaxAgeMs { get; set; } = 1500;

	public int DashboardSnapshotMaxAgeMs { get; set; } = 2500;

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

	public List<string> AllowedNetworks { get; set; } = new List<string>();

	public void Clamp()
	{
		SchemaVersion = Math.Max(2, SchemaVersion);
		BindAddress = (string.IsNullOrWhiteSpace(BindAddress) ? "127.0.0.1" : BindAddress.Trim());
		Port = Math.Clamp(Port, 1, 65535);
		PublicUrl = (PublicUrl ?? "").Trim();
		if (PublicUrl.Length > 512)
		{
			PublicUrl = PublicUrl.Substring(0, 512);
		}
		AdminUsername = (string.IsNullOrWhiteSpace(AdminUsername) ? "admin" : AdminUsername.Trim());
		if (AdminUsername.Length > 64)
		{
			AdminUsername = AdminUsername.Substring(0, 64);
		}
		PasswordIterations = Math.Clamp(PasswordIterations, 100000, 1000000);
		SessionTimeoutMinutes = Math.Clamp(SessionTimeoutMinutes, 5, 1440);
		MaxFailedLogins = Math.Clamp(MaxFailedLogins, 1, 50);
		LockoutMinutes = Math.Clamp(LockoutMinutes, 1, 1440);
		WebWorkerCount = Math.Clamp(WebWorkerCount, 1, 8);
		WebRequestQueueCapacity = Math.Clamp(WebRequestQueueCapacity, 8, 512);
		WebQueueMaxWaitMs = Math.Clamp(WebQueueMaxWaitMs, 500, 30000);
		LiveSnapshotMaxAgeMs = Math.Clamp(LiveSnapshotMaxAgeMs, 250, 10000);
		DashboardSnapshotMaxAgeMs = Math.Clamp(DashboardSnapshotMaxAgeMs, 500, 15000);
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
		if (AllowedNetworks == null)
		{
			List<string> list = (AllowedNetworks = new List<string>());
		}
		AllowedNetworks.RemoveAll(string.IsNullOrWhiteSpace);
		for (int i = 0; i < AllowedNetworks.Count; i++)
		{
			AllowedNetworks[i] = AllowedNetworks[i].Trim();
		}
	}
}
