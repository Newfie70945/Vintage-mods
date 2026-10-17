using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NewfiesOptimizer;

public static class ReportExporter
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };
    public static string ToJson(ProfileReport report) => JsonSerializer.Serialize(report, JsonOptions);
    public static string ToCsv(ProfileReport report)
    {
        var b = new StringBuilder("timestampUtc,elapsedSeconds,tickCount,effectiveTps,averageTickMs,p95TickMs,processCpuPercent,mainThreadCpuPercent,managedHeapBytes,gcCommittedBytes,gcFragmentedBytes,workingSetBytes,privateMemoryBytes,allocatedBytesDelta,gc0Delta,gc1Delta,gc2Delta,gcPauseMsDelta,players,loadedEntities,loadedChunks,generatingChunkColumns,networkSentBytesDelta,networkReceivedBytesDelta,profilerOwnWorkMs\n");
        foreach (var s in report.Samples)
        {
            object?[] row = [s.TimestampUtc.ToString("O"), s.ElapsedSeconds, s.TickCount, s.EffectiveTps, s.AverageTickMs, s.P95TickMs,
                s.ProcessCpuPercent, s.MainThreadCpuPercent, s.ManagedHeapBytes, s.GcCommittedBytes, s.GcFragmentedBytes,
                s.WorkingSetBytes, s.PrivateMemoryBytes, s.AllocatedBytesDelta, s.Gc0Delta, s.Gc1Delta, s.Gc2Delta,
                s.GcPauseMsDelta, s.Players, s.LoadedEntities, s.LoadedChunks, s.GeneratingChunkColumns,
                s.NetworkSentBytesDelta, s.NetworkReceivedBytesDelta, s.ProfilerOwnWorkMs];
            b.AppendJoin(',', row.Select(x => Convert.ToString(x, CultureInfo.InvariantCulture) ?? "")).Append('\n');
        }
        return b.ToString();
    }
    public static void Write(string directory, string stem, ProfileReport report, int keep)
    {
        Directory.CreateDirectory(directory);
        string json = ToJson(report), csvData = ToCsv(report);
        AtomicWrite(Path.Combine(directory, stem + ".json"), json);
        AtomicWrite(Path.Combine(directory, stem + ".csv"), csvData);
        AtomicWrite(Path.Combine(directory, "latest.json"), json);
        AtomicWrite(Path.Combine(directory, "latest.csv"), csvData);
        // Only this application's numbered capture files are retention candidates.
        foreach (var old in new DirectoryInfo(directory).GetFiles("profile-*.json").OrderByDescending(x => x.LastWriteTimeUtc).Skip(keep))
        {
            old.Delete();
            string csv = Path.ChangeExtension(old.FullName, ".csv");
            if (File.Exists(csv)) File.Delete(csv);
        }
    }
    private static void AtomicWrite(string path, string content)
    {
        string temporary = path + ".tmp";
        try { File.WriteAllText(temporary, content, new UTF8Encoding(false)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
