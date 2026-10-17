using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Threading;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace BlockLogger.WebMap;

/// <summary>
/// Export-scoped filter for a very specific Vintage Story chunk-deserialization noise source.
///
/// Terrain exports intentionally touch many saved chunk columns. Some worlds contain temporary
/// or mod-owned entities that cannot be reconstructed while those chunks are inspected (for
/// example stale outlaw entities or magic dummy/spell entities). Vintage Story discards those
/// failed entity instances and normally emits an Error + VerboseDebug pair for every occurrence.
///
/// The WebMap does not use chunk entities at all; it only needs terrain/block data. While a WebMap
/// export is active we therefore suppress only that exact engine message pair. No other Error,
/// Warning, Fatal, block-entity, or mod log entry is filtered. Outside an export this class is inert.
/// </summary>
internal static class WebMapExportLogSuppressor
{
    private const string HarmonyId = "newfies.serveressentials.webmap.exportlognoise";
    private const string EntityErrorPrefix = "Failed loading an entity (type ";
    private const string EntityErrorSuffix = ") in a chunk. Will discard, sorry. Exception logged to verbose debug.";
    private const string EntityVerbosePrefix = "Failed loading an entity in a chunk. Will discard, sorry. Exception:";

    private static readonly object InstallLock = new();
    private static readonly ConcurrentDictionary<string, int> SuppressedByType = new(StringComparer.OrdinalIgnoreCase);
    private static int activeScopes;
    private static int installed;
    private static int installFailed;
    private static ICoreServerAPI? serverApi;
    private static object? harmonyInstance;

    public static IDisposable Begin(ICoreServerAPI api)
    {
        serverApi ??= api;
        EnsureInstalled(api);

        if (Volatile.Read(ref installed) == 1)
        {
            if (Interlocked.Increment(ref activeScopes) == 1)
                SuppressedByType.Clear();
            return new Scope(enabled: true);
        }

        return new Scope(enabled: false);
    }

    private static void EnsureInstalled(ICoreServerAPI api)
    {
        if (Volatile.Read(ref installed) == 1 || Volatile.Read(ref installFailed) == 1) return;

        lock (InstallLock)
        {
            if (installed == 1 || installFailed == 1) return;

            try
            {
                Assembly? harmonyAssembly = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => string.Equals(a.GetName().Name, "0Harmony", StringComparison.OrdinalIgnoreCase));
                if (harmonyAssembly == null)
                {
                    try { harmonyAssembly = Assembly.Load("0Harmony"); } catch { }
                }

                Type? harmonyType = harmonyAssembly?.GetType("HarmonyLib.Harmony");
                Type? harmonyMethodType = harmonyAssembly?.GetType("HarmonyLib.HarmonyMethod");
                if (harmonyType == null || harmonyMethodType == null)
                    throw new InvalidOperationException("0Harmony was not available in the server AppDomain.");

                MethodInfo? target = typeof(LoggerBase).GetMethods(BindingFlags.Instance | BindingFlags.Public)
                    .FirstOrDefault(m =>
                    {
                        if (!string.Equals(m.Name, nameof(LoggerBase.Log), StringComparison.Ordinal)) return false;
                        ParameterInfo[] ps = m.GetParameters();
                        return ps.Length == 3 &&
                               ps[0].ParameterType == typeof(EnumLogType) &&
                               ps[1].ParameterType == typeof(string) &&
                               ps[2].ParameterType == typeof(object[]);
                    });
                MethodInfo? prefix = typeof(WebMapExportLogSuppressor).GetMethod(nameof(BeforeLoggerLog), BindingFlags.Static | BindingFlags.NonPublic);
                if (target == null || prefix == null)
                    throw new MissingMethodException("Vintage Story LoggerBase.Log(EnumLogType,string,object[]) was not found.");

                object? harmony = Activator.CreateInstance(harmonyType, new object[] { HarmonyId });
                object? harmonyMethod = Activator.CreateInstance(harmonyMethodType, new object[] { prefix });
                if (harmony == null || harmonyMethod == null)
                    throw new InvalidOperationException("Harmony patch objects could not be created.");

                MethodInfo? patch = harmonyType.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                    .Where(m => string.Equals(m.Name, "Patch", StringComparison.Ordinal))
                    .FirstOrDefault(m =>
                    {
                        ParameterInfo[] ps = m.GetParameters();
                        return ps.Length >= 2 &&
                               typeof(MethodBase).IsAssignableFrom(ps[0].ParameterType) &&
                               ps[1].ParameterType == harmonyMethodType;
                    });
                if (patch == null)
                    throw new MissingMethodException("Compatible Harmony.Patch method was not found.");

                ParameterInfo[] patchParams = patch.GetParameters();
                object?[] args = new object?[patchParams.Length];
                args[0] = target;
                args[1] = harmonyMethod;
                patch.Invoke(harmony, args);

                harmonyInstance = harmony;
                Volatile.Write(ref installed, 1);
                api.Logger.Notification("[ServerEssentials/WebMap] export-only entity-load log suppression is active. Only Vintage Story's failed chunk-entity load message pair is filtered while terrain export is running.");
            }
            catch (Exception ex)
            {
                Volatile.Write(ref installFailed, 1);
                api.Logger.Warning("[ServerEssentials/WebMap] export-only entity-load log suppression could not be installed; terrain export will continue normally. {0}: {1}", ex.GetType().Name, ex.Message);
            }
        }
    }

    // Harmony prefix. Returning false prevents LoggerBase.Log from reaching LogImpl/EntryAdded.
    private static bool BeforeLoggerLog(EnumLogType logType, string format, object[] args)
    {
        try
        {
            if (Volatile.Read(ref activeScopes) <= 0 || string.IsNullOrEmpty(format)) return true;

            if (logType == EnumLogType.Error &&
                format.StartsWith(EntityErrorPrefix, StringComparison.Ordinal) &&
                format.EndsWith(EntityErrorSuffix, StringComparison.Ordinal))
            {
                string entityType = ExtractEntityType(format);
                SuppressedByType.AddOrUpdate(entityType, 1, static (_, count) => count + 1);
                return false;
            }

            if (logType == EnumLogType.VerboseDebug &&
                format.StartsWith(EntityVerbosePrefix, StringComparison.Ordinal))
            {
                return false;
            }
        }
        catch
        {
            // Logging safety code must never interfere with the server logger.
        }

        return true;
    }

    private static string ExtractEntityType(string message)
    {
        int start = EntityErrorPrefix.Length;
        int end = message.IndexOf(EntityErrorSuffix, start, StringComparison.Ordinal);
        if (end <= start) return "unknown";
        string value = message[start..end].Trim();
        return value.Length == 0 ? "unknown" : value;
    }

    private static void EndScope()
    {
        int remaining = Interlocked.Decrement(ref activeScopes);
        if (remaining > 0) return;
        if (remaining < 0)
        {
            Interlocked.Exchange(ref activeScopes, 0);
            return;
        }

        int total = SuppressedByType.Values.Sum();
        if (total <= 0) return;

        try
        {
            string detail = string.Join(", ", SuppressedByType
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .Select(kv => $"{kv.Key}={kv.Value}"));

            serverApi?.Logger.Notification(
                "[ServerEssentials/WebMap] terrain export suppressed {0:n0} expected chunk-entity load error(s){1}. These messages were export-only noise; unrelated errors were not filtered.",
                total,
                string.IsNullOrWhiteSpace(detail) ? "" : " (" + detail + ")");
        }
        catch
        {
            // Summary logging is best-effort only.
        }
        finally
        {
            SuppressedByType.Clear();
        }
    }

    private sealed class Scope : IDisposable
    {
        private readonly bool enabled;
        private int disposed;

        public Scope(bool enabled) => this.enabled = enabled;

        public void Dispose()
        {
            if (!enabled || Interlocked.Exchange(ref disposed, 1) != 0) return;
            EndScope();
        }
    }
}
