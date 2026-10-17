using System;
using System.Linq;
using BlockLogger.Roles;

namespace BlockLogger.WebDashboard;

public sealed partial class WebDashboardService
{
    private object BuildRetentionDiagnostics()
    {
        var storage = module.Storage;
        return new {
            schemaVersion = 1, buildId = BuildId, capturedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            memory = CaptureMemoryInspectorSnapshot("Diagnostic export"),
            webPanel = snapshotCache.GetDiagnostics(),
            logger = storage == null ? null : new { pendingEvents = storage.PendingEvents, pendingSnapshots = storage.PendingSnapshots,
                pendingEntityAggregates = storage.PendingEntityAggregates, capacity = storage.QueueCapacity, droppedEvents = storage.DroppedEvents,
                cleanup = "The worker drains bounded queues; shutdown completes waiting queries." },
            admin = module.Admin?.GetRetentionDiagnostics(), antiCheat = module.AntiCheat?.GetRetentionDiagnostics(),
            oreShield = module.AntiCheat?.GetOreShieldSnapshot(),
            note = "Counts and sampled payload bytes describe Suite-owned structures. This export contains no tokens, inventory contents or player names. It does not attribute the entire process heap." };
    }

    private object? BuildPlayerInvestigation(string player)
    {
        var admin = module.Admin; var summary = admin?.GetPlayer(player);
        if (admin == null || summary == null) return null;
        var anti = module.AntiCheat?.GetInvestigationSnapshot(summary.PlayerUid);
        return new { capturedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), player = summary,
            antiCheat = anti == null ? null : new { anti.Status, anti.RiskScore, anti.RiskFamilies, anti.LastConsequence, anti.LastConsequenceUtc,
                findings = anti.Findings.OrderByDescending(f => f.ObservedUtc).Take(32).ToArray(), anti.Decision },
            riskHistory = module.AntiCheat?.GetHistorySnapshot(summary.PlayerUid, 32).Incidents,
            warnings = admin.GetWarnings(summary.PlayerUid).Take(32).ToArray(),
            notes = admin.GetNotes(summary.PlayerUid).Take(32).ToArray(),
            history = admin.GetHistory(summary.PlayerUid, 64),
            inventorySnapshots = admin.GetInventorySnapshots(summary.PlayerUid, 12),
            role = new RoleManagementService(api, () => module.PlayerCommands?.Config, () => module.PlayerList).PreviewPlayer(summary.PlayerUid, ""),
            detailNote = "Inventory contents and block history load only when requested in their existing tabs." };
    }
}
