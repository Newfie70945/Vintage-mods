using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;

namespace BlockLogger.Compatibility;

/// <summary>
/// Runtime-level compatibility probes for server replacements that are not
/// normal Vintage Story mods and therefore do not appear in ModLoader.Mods.
/// </summary>
public static class RuntimeCompatibility
{
    private static readonly Lazy<RuntimeProbe> probe = new(ProbeRuntime, true);

    public static bool IsStratum => probe.Value.IsStratum;
    public static string StratumDetection => probe.Value.StratumDetection;

    private static RuntimeProbe ProbeRuntime()
    {
        try
        {
            string processName = "";
            string processPath = "";
            try
            {
                using Process process = Process.GetCurrentProcess();
                processName = process.ProcessName ?? "";
                processPath = process.MainModule?.FileName ?? Environment.ProcessPath ?? "";
            }
            catch
            {
                processPath = Environment.ProcessPath ?? "";
            }

            if (ContainsStratum(processName) || ContainsStratum(processPath))
            {
                return new RuntimeProbe(true, string.IsNullOrWhiteSpace(processName)
                    ? "server process path"
                    : $"server process '{processName}'");
            }

            string entryName = Assembly.GetEntryAssembly()?.GetName().Name ?? "";
            if (ContainsStratum(entryName))
            {
                return new RuntimeProbe(true, $"entry assembly '{entryName}'");
            }

            string stratumAssembly = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a =>
                {
                    try { return a.GetName().Name ?? ""; }
                    catch { return ""; }
                })
                .FirstOrDefault(ContainsStratum) ?? "";

            if (!string.IsNullOrWhiteSpace(stratumAssembly))
            {
                return new RuntimeProbe(true, $"loaded assembly '{stratumAssembly}'");
            }
        }
        catch
        {
            // Compatibility probing must never prevent the suite from loading.
        }

        return new RuntimeProbe(false, "not detected");
    }

    private static bool ContainsStratum(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.IndexOf("stratum", StringComparison.OrdinalIgnoreCase) >= 0;

    private sealed record RuntimeProbe(bool IsStratum, string StratumDetection);
}
