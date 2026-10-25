using System;
using System.Collections.Generic;
using System.Linq;

namespace BlockLogger.WebDashboard;

public sealed class IncidentAuditPlayer
{
    public string Uid { get; set; } = "";
    public string Name { get; set; } = "";
    public int Total { get; set; }
    public int Breaks { get; set; }
    public int Places { get; set; }
    public int Uses { get; set; }
    public int Containers { get; set; }
    public int Interacts { get; set; }
    public int Chunks { get; set; }
}

public sealed class IncidentAuditArea
{
    public string Dimension { get; set; } = "";
    public int ChunkX { get; set; }
    public int ChunkZ { get; set; }
    public int BlockX { get; set; }
    public int BlockZ { get; set; }
    public int Events { get; set; }
    public int Players { get; set; }
}

public sealed class IncidentAuditCount
{
    public string Name { get; set; } = "";
    public string Detail { get; set; } = "";
    public long Count { get; set; }
}

public sealed class IncidentAuditFinding
{
    public string Kind { get; set; } = "";
    public string Title { get; set; } = "";
    public string Confidence { get; set; } = "Low";
    public string Evidence { get; set; } = "";
}

// What the BlockLogger audit recorded around one archived lag spike. This is correlation only:
// the audit covers block, container and entity events; it does not record movement, chunk
// generation or mod timers, so quiet audit data never clears a player.
public sealed class IncidentAuditActivity
{
    public bool Checked { get; set; }
    public bool Available { get; set; }
    public string Status { get; set; } = "";
    public long WindowStartUnixMilliseconds { get; set; }
    public long WindowEndUnixMilliseconds { get; set; }
    public long CapturedUnixMilliseconds { get; set; }
    public int TotalEvents { get; set; }
    public int PlayerEvents { get; set; }
    public int DistinctPlayers { get; set; }
    public long BaselineEvents { get; set; }
    public int BaselineMinutes { get; set; }
    public List<IncidentAuditPlayer> Players { get; set; } = new();
    public List<IncidentAuditArea> Areas { get; set; } = new();
    public List<IncidentAuditCount> Actions { get; set; } = new();
    public List<IncidentAuditCount> Entities { get; set; } = new();
    public List<IncidentAuditFinding> Findings { get; set; } = new();
    public List<string> Caveats { get; set; } = new();
}

public static class IncidentAuditInterpreter
{
    private const int MinBurstEvents = 100;

    // Action names match BlockLogger.Models.LogActionType.
    private static readonly HashSet<string> EntityActions =
        new(StringComparer.Ordinal) { "EntitySpawn", "EntityDeath", "EntityDespawn" };

    public static void Interpret(IncidentAuditActivity audit)
    {
        audit.Findings = new List<IncidentAuditFinding>();
        if (!audit.Available) return;

        double windowSeconds = Math.Max(1, (audit.WindowEndUnixMilliseconds - audit.WindowStartUnixMilliseconds) / 1000d);

        AddPlayerBurst(audit, windowSeconds);
        AddAreaConcentration(audit);
        AddEntityChurn(audit);
        AddSurge(audit, windowSeconds);

        if (audit.Findings.Count == 0 && audit.TotalEvents < 20)
        {
            audit.Findings.Add(new IncidentAuditFinding
            {
                Kind = "Quiet",
                Title = "Little logged player activity near this spike",
                Confidence = "Low",
                Evidence = $"Only {audit.TotalEvents} audit event(s) were recorded in the {windowSeconds:0}s window. " +
                    "The audit does not record movement, chunk loading or generation, or mod timers, so this does not rule out player-driven load."
            });
        }
    }

    private static void AddPlayerBurst(IncidentAuditActivity audit, double windowSeconds)
    {
        var top = audit.Players.OrderByDescending(p => p.Total).FirstOrDefault();
        if (top == null || audit.PlayerEvents < MinBurstEvents || top.Total < MinBurstEvents) return;
        double share = top.Total / (double)audit.PlayerEvents;
        if (share < 0.6) return;

        double rate = top.Total / windowSeconds;
        string name = string.IsNullOrWhiteSpace(top.Name) ? top.Uid : top.Name;
        var parts = new List<string>();
        if (top.Breaks > 0) parts.Add($"{top.Breaks} breaks");
        if (top.Places > 0) parts.Add($"{top.Places} places");
        if (top.Uses > 0) parts.Add($"{top.Uses} block uses");
        if (top.Containers > 0) parts.Add($"{top.Containers} container actions");
        if (top.Interacts > 0) parts.Add($"{top.Interacts} entity interactions");
        audit.Findings.Add(new IncidentAuditFinding
        {
            Kind = "Player",
            Title = $"{name} made {share:P0} of logged player actions",
            Confidence = rate >= 10 || top.Total >= 1000 ? "Medium" : "Low",
            Evidence = $"{top.Total} actions over {windowSeconds:0}s ({rate:0.#}/s)" +
                (parts.Count > 0 ? ": " + string.Join(", ", parts) : "") +
                $", touching {top.Chunks} chunk(s). Heavy activity is a correlation with the spike, not proof that it caused the stall."
        });
    }

    private static void AddAreaConcentration(IncidentAuditActivity audit)
    {
        var top = audit.Areas.OrderByDescending(a => a.Events).FirstOrDefault();
        if (top == null || audit.TotalEvents < MinBurstEvents || top.Events < MinBurstEvents) return;
        double share = top.Events / (double)audit.TotalEvents;
        if (share < 0.5) return;

        audit.Findings.Add(new IncidentAuditFinding
        {
            Kind = "Area",
            Title = $"Logged activity is concentrated near block {top.BlockX}, {top.BlockZ}",
            Confidence = top.Events >= 500 ? "Medium" : "Low",
            Evidence = $"{top.Events} of {audit.TotalEvents} events ({share:P0}) fell in chunk {top.ChunkX},{top.ChunkZ} (dimension {top.Dimension}) involving {top.Players} player(s). " +
                "Look for farms, redstone-style machines, mass block edits or crowded containers there."
        });
    }

    private static void AddEntityChurn(IncidentAuditActivity audit)
    {
        long logged = audit.Actions.Where(a => EntityActions.Contains(a.Name)).Sum(a => a.Count);
        long bucketed = audit.Entities.Sum(e => e.Count);
        long churn = Math.Max(logged, bucketed);
        if (churn < 200) return;

        string top = string.Join(", ", audit.Entities.OrderByDescending(e => e.Count).Take(3)
            .Select(e => $"{e.Detail} {e.Name} ×{e.Count}"));
        audit.Findings.Add(new IncidentAuditFinding
        {
            Kind = "Entities",
            Title = "Heavy entity spawn/death/despawn churn in the window",
            Confidence = churn >= 1000 ? "Medium" : "Low",
            Evidence = $"{churn} entity lifecycle events were recorded" + (top.Length > 0 ? $" (top: {top})." : ".") +
                " Mass spawns or despawns cost main-thread time; compare with the entity-growth and chunk-pressure evidence."
        });
    }

    private static void AddSurge(IncidentAuditActivity audit, double windowSeconds)
    {
        if (audit.TotalEvents < MinBurstEvents || audit.BaselineMinutes <= 0) return;
        double windowPerMinute = audit.TotalEvents / (windowSeconds / 60d);
        double baselinePerMinute = audit.BaselineEvents / (double)audit.BaselineMinutes;
        if (baselinePerMinute > 0 && windowPerMinute < baselinePerMinute * 3) return;

        audit.Findings.Add(new IncidentAuditFinding
        {
            Kind = "Surge",
            Title = baselinePerMinute > 0
                ? $"Logged activity ran {windowPerMinute / baselinePerMinute:0.#}× the prior {audit.BaselineMinutes} minutes"
                : "Logged activity started after a quiet period",
            Confidence = "Low",
            Evidence = $"{audit.TotalEvents} events in {windowSeconds:0}s ({windowPerMinute:0}/min) against {baselinePerMinute:0.#}/min over the preceding {audit.BaselineMinutes} minutes. " +
                "Pre-spike activity is included because work done by players can precede the stall."
        });
    }
}
