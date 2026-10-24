using System.Globalization;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace NewfiesOptimizer;

public sealed partial class OptimizerMod : ModSystem
{
    private RuntimeProfiler? profiler;
    private EngineOptimizations? optimizations;
    private ICoreServerAPI? api;
    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Server;
    public override void StartServerSide(ICoreServerAPI api)
    {
        this.api = api;
        OptimizerConfig config;
        try { config = api.LoadModConfig<OptimizerConfig>("optimizer.json") ?? new(); config.Normalize(); api.StoreModConfig(config, "optimizer.json"); }
        catch (Exception e)
        {
            api.Logger.Error("[NewfiesOptimizer] optimizer.json could not be loaded ({0}). No hooks installed; fix configuration and restart.", e.GetType().Name);
            return;
        }
        currentConfig=config;
        bridgeActive=true;
        api.Event.RegisterEventBusListener(OnSuiteControl, filterByEventName: ControlEvent);
        try { profiler = new(api, config); }
        catch (Exception error)
        {
            api.Logger.Error("[NewfiesOptimizer] Profiler initialization unavailable ({0}); no optimizer hooks installed. Server gameplay continues.", error.GetType().Name);
            return;
        }
        try { optimizations = new(api, config); profiler.ExternalModules = optimizations.Modules; profiler.ExternalCounters = optimizations.Counters; }
        catch (Exception error) { api.Logger.Error("[NewfiesOptimizer] Engine modules unavailable ({0}); profiling remains available.", error.GetType().Name); }
        var root = api.ChatCommands.Create("optimizer").WithDescription("Server API optimization modules, diagnostics and bounded profiling.")
            .RequiresPrivilege(Privilege.controlserver).HandleWith(_ => TextCommandResult.Success(Status()));
        root.BeginSubCommand("status").HandleWith(_ => TextCommandResult.Success(Status())).EndSubCommand();
        root.BeginSubCommand("modules").HandleWith(_ => TextCommandResult.Success(optimizations?.Status() ?? "Engine modules unavailable.")).EndSubCommand();
        var profile = root.BeginSubCommand("profile").WithDescription("Start/stop a bounded profiler capture.");
        profile.BeginSubCommand("start").HandleWith(_ => TextCommandResult.Success(profiler.Start())).EndSubCommand();
        profile.BeginSubCommand("stop").HandleWith(_ => TextCommandResult.Success(profiler.Stop())).EndSubCommand();
        profile.BeginSubCommand("clear").HandleWith(_ =>
        {
            var result = profiler.ClearCapture();
            return result.Success ? TextCommandResult.Success(result.Message) : TextCommandResult.Error(result.Message);
        }).EndSubCommand();
        profile.EndSubCommand();
        foreach (string command in new[] { "report", "hotspots", "entities", "chunks", "network", "gc", "benchmark", "export" })
        {
            string name = command;
            root.BeginSubCommand(name).HandleWith(_ => TextCommandResult.Success(Describe(name))).EndSubCommand();
        }
        api.Event.ServerRunPhase(EnumServerRunPhase.RunGame, () =>
        {
            if (config.Enabled && config.Profiling.AutoStart) api.Logger.Notification("[NewfiesOptimizer] " + profiler.Start());
        });
        api.Logger.Notification("[NewfiesOptimizer] 0.20.8 engine-patch preview ready. /optimizer status and /optimizer modules show actual activation state.");
    }

    private string Status() => (profiler?.Status() ?? "Profiler unavailable") + "\n" + (optimizations?.Status() ?? "Engine modules unavailable");

    private string Describe(string command)
    {
        if (profiler is null) return "Profiler unavailable; check configuration.";
        if (command == "export") return profiler.QueueExport();
        if (command == "benchmark") return "Benchmark methodology: clone the same world, use authorized real clients, separate warmup/measurement, run >=3 matched A (stock), B (profiler only), C (one evidenced optimization), D (validated set) trials. Test one configured API module at a time and compare stock, prefix-disabled and optimized trials. /optimizer profile start, /optimizer profile stop, /optimizer export. Standalone tools generate comparisons; no fake clients or speedup claims.";
        if (profiler.Session is null) return "No capture yet. /optimizer profile start then /optimizer profile stop.";
        var report = profiler.Snapshot();
        var latest = report.Samples.LastOrDefault();
        static string N(double? value, string unit = "") => value.HasValue ? value.Value.ToString("F2", CultureInfo.InvariantCulture) + unit : "unavailable";
        static string B(long? value) => value.HasValue ? (value.Value / 1048576d).ToString("F1", CultureInfo.InvariantCulture) + " MiB" : "unavailable";
        return command switch
        {
            "report" => $"Capture {report.DurationSeconds:F1}s, completed ticks={report.Tick.Count}; effective TPS={N(report.Tick.EffectiveTps)}, average={N(report.Tick.AverageMs, "ms")}, median={N(report.Tick.MedianMs, "ms")}, P95={N(report.Tick.P95Ms, "ms")}, P99={N(report.Tick.P99Ms, "ms")}, worst={N(report.Tick.WorstMs, "ms")}. Main-thread work={N(report.Tick.MainThreadWorkPercent, "%")}. Historical samples={report.Samples.Length}, overwritten={report.SamplesOverwritten}. /optimizer export writes JSON/CSV.",
            "hotspots" => report.Hotspots.Length == 0 ? "No timed systems. Set Compatibility.SafeMode=false and enable optional Profiling.SystemTimings / PhaseTimings / CallbackTimings, then restart to collect sampled inclusive timings. Individual AI/worker/I/O costs are not inferred." : string.Join("\n", report.Hotspots.Take(12).Select(h => $"{h.Name}: {h.SampledCalls}/{h.Calls} sampled calls, average={N(h.AverageMs,"ms")}, P95={N(h.P95Ms,"ms")}, worst={N(h.WorstMs,"ms")} (inclusive)")),
            "entities" => $"Playing clients={latest?.Players?.ToString() ?? "unavailable"}; loaded entities={latest?.LoadedEntities?.ToString() ?? "unavailable"}. Entity-loop timings are opt-in; loaded does not imply active. AI tick cadence unchanged; inspect the optional main-thread path assistance module for scheduling changes.",
            "chunks" => $"Loaded chunks={latest?.LoadedChunks?.ToString() ?? "unavailable"}; generating queued columns={latest?.GeneratingChunkColumns?.ToString() ?? "unavailable"}. Counts never clone terrain collections. Worker generation/loading/serialization and full save latency unavailable; no world I/O changes.",
            "network" => $"Last sample engine payload delta: outbound={latest?.NetworkSentBytesDelta?.ToString() ?? "unavailable"} bytes; inbound={latest?.NetworkReceivedBytesDelta?.ToString() ?? "unavailable"} bytes. TCP+UDP aggregate, not NIC traffic or per-player bandwidth. Ordering/reliability unchanged.",
            "gc" => $"Managed estimate={B(latest?.ManagedHeapBytes)}; committed={B(latest?.GcCommittedBytes)}; fragmentation={B(latest?.GcFragmentedBytes)}; private={B(latest?.PrivateMemoryBytes)}; working set={B(latest?.WorkingSetBytes)}. Last interval collections Gen0/1/2={latest?.Gc0Delta}/{latest?.Gc1Delta}/{latest?.Gc2Delta}; GC pause delta={N(latest?.GcPauseMsDelta,"ms")}. No forced GC.",
            _ => profiler.Status()
        };
    }
    public override void Dispose() { bridgeActive=false; optimizations?.Dispose(); optimizations = null; profiler?.Dispose(); profiler = null; api = null; base.Dispose(); }
}
