using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using HarmonyLib;
using BlockLogger.ServerHealth;
using Vintagestory.API.Server;

namespace BlockLogger.Services;

/// <summary>
/// Server-side Patch Studio. It intentionally supports only a small set of declarative Harmony
/// recipes instead of arbitrary uploaded C# so administrators can create, edit and disable
/// emergency compatibility/performance patches without turning the WebPanel into remote code execution.
/// </summary>
public sealed partial class CustomPatchService : IDisposable
{
    public const string ConfigFile = "NewfiesCustomPatches.json";
    public const string ContentConfigFile = "NewfiesContentPatches.json";
    private const string ContentPatchModId = "newfiessuitecontentpatches";
    private const string ContentPatchFolderName = "NewfiesSuiteContentPatches";
    private const string HarmonyId = "newfies.serveressentials.custompatches";
    private readonly ICoreServerAPI api;
    private readonly Harmony harmony = new(HarmonyId);
    private readonly object sync = new();
    private CustomPatchStore store = new();
    private ContentPatchStore contentStore = new();
    private bool contentRestartRequired;
    private static CustomPatchService? current;
    private readonly ConcurrentDictionary<MethodBase, RuntimePatchState> runtime = new();

    public CustomPatchService(ICoreServerAPI api) => this.api = api;

    public void Start()
    {
        lock (sync)
        {
            store = SuiteConfigStorage.Load<CustomPatchStore>(api, ConfigFile) ?? new CustomPatchStore();
            store.Clamp();
            contentStore = SuiteConfigStorage.Load<ContentPatchStore>(api, ContentConfigFile) ?? new ContentPatchStore();
            contentStore.Clamp();
            contentRestartRequired = false;
            current = this;
            ApplyAllUnsafe();
            SaveUnsafe();
        }
    }

    public CustomPatchSnapshot GetSnapshot(ServerHealthSnapshot? health = null)
    {
        lock (sync)
        {
            var rows = store.Patches.Select(ToRow).ToList();
            return new CustomPatchSnapshot
            {
                Available = true,
                HarmonyId = HarmonyId,
                ConfigPath = SuiteConfigStorage.DisplayPath(ConfigFile),
                Patches = rows,
                Suggestions = BuildSuggestions(health),
                SupportedModes = new[] { "Observe", "ThrottleVoid", "SkipVoid" },
                Note = "Patch Studio uses constrained declarative Harmony recipes. It does not compile or execute arbitrary C# from the WebPanel."
            };
        }
    }

    public ContentPatchSnapshot GetContentSnapshot()
    {
        lock (sync)
        {
            return new ContentPatchSnapshot
            {
                Available = true,
                ConfigPath = SuiteConfigStorage.DisplayPath(ContentConfigFile),
                GeneratedModPath = GetGeneratedContentModPath(),
                RestartRequired = contentRestartRequired,
                Patches = contentStore.Patches.Select(x => x.Clone()).ToList(),
                SupportedOperations = new[] { "add", "addMerge", "addEach", "remove", "replace", "move", "copy" },
                Note = "Content patches are generated as a managed Vintage Story content mod. Changes take effect after the next server restart because JSON patches are applied during asset loading."
            };
        }
    }

    public ContentPatchActionResult SaveContentPatch(ContentPatchDefinition incoming)
    {
        lock (sync)
        {
            incoming = (incoming ?? new ContentPatchDefinition()).Clone();
            incoming.Clamp();
            incoming.Operation = NormalizeContentOperation(incoming.Operation);
            incoming.Side = NormalizeSide(incoming.Side);
            string? validation = ValidateContentPatch(incoming);
            if (validation != null) return ContentFail(validation);

            if (string.IsNullOrWhiteSpace(incoming.Id)) incoming.Id = Guid.NewGuid().ToString("N");
            int index = contentStore.Patches.FindIndex(x => string.Equals(x.Id, incoming.Id, StringComparison.OrdinalIgnoreCase));
            if (index < 0 && contentStore.Patches.Count >= 200) return ContentFail("The patch pack contains 200 definitions. Delete an unused definition before adding another.");
            var previous = contentStore.Patches.Select(x => x.Clone()).ToList();
            if (index >= 0) contentStore.Patches[index] = incoming;
            else contentStore.Patches.Add(incoming);

            string? persistenceError = PersistContentChange(previous);
            if (persistenceError != null) return ContentFail(persistenceError);
            contentRestartRequired = true;
            return ContentSuccess(index >= 0 ? "JSON/content patch updated. Restart required to apply asset patches." : "JSON/content patch created. Restart required to apply asset patches.");
        }
    }

    public ContentPatchActionResult DeleteContentPatch(string id)
    {
        lock (sync)
        {
            var previous = contentStore.Patches.Select(x => x.Clone()).ToList();
            int removed = contentStore.Patches.RemoveAll(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
            if (removed == 0) return ContentFail("Content patch was not found.");
            string? persistenceError = PersistContentChange(previous);
            if (persistenceError != null) return ContentFail(persistenceError);
            contentRestartRequired = true;
            return ContentSuccess("Content patch deleted. Restart required to unload the previous asset patch state.");
        }
    }

    public ContentPatchActionResult SetContentPatchEnabled(string id, bool enabled)
    {
        lock (sync)
        {
            var patch = contentStore.Patches.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
            if (patch == null) return ContentFail("Content patch was not found.");
            var previous = contentStore.Patches.Select(x => x.Clone()).ToList();
            patch.Enabled = enabled;
            string? persistenceError = PersistContentChange(previous);
            if (persistenceError != null) return ContentFail(persistenceError);
            contentRestartRequired = true;
            return ContentSuccess(enabled ? "Content patch enabled. Restart required to apply it." : "Content patch disabled. Restart required to remove its loaded effect.");
        }
    }

    public ContentPatchActionResult GenerateContentPatch(ContentPatchGenerateRequest request)
    {
        request ??= new ContentPatchGenerateRequest();
        if (request.Guided) return GenerateGuidedContentPatch(request);
        string preset = (request.Preset ?? "generic").Trim().ToLowerInvariant();
        var patch = new ContentPatchDefinition
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = string.IsNullOrWhiteSpace(request.Name) ? "Generated content patch" : request.Name.Trim(),
            Enabled = false,
            File = (request.File ?? "").Trim(),
            Operation = NormalizeContentOperation(request.Operation),
            Path = (request.Path ?? "").Trim(),
            FromPath = (request.FromPath ?? "").Trim(),
            ValueJson = string.IsNullOrWhiteSpace(request.ValueJson) ? "null" : request.ValueJson.Trim(),
            Side = NormalizeSide(request.Side),
            Notes = "Generated by Patch Studio. Review the asset file and JSON pointer before enabling.",
            DependsOnModId = (request.DependsOnModId ?? "").Trim(),
            InvertDependency = request.InvertDependency
        };

        string ingredient = EscapeJsonPointerToken(request.IngredientKey ?? "A");
        switch (preset)
        {
            case "recipe-output-quantity":
                patch.Operation = "add";
                patch.Path = "/output/quantity";
                patch.ValueJson = Math.Max(1, request.Quantity).ToString(System.Globalization.CultureInfo.InvariantCulture);
                break;
            case "recipe-ingredient-quantity":
                patch.Operation = "add";
                patch.Path = $"/ingredients/{ingredient}/quantity";
                patch.ValueJson = Math.Max(1, request.Quantity).ToString(System.Globalization.CultureInfo.InvariantCulture);
                break;
            case "recipe-ingredient-code":
                patch.Operation = "add";
                patch.Path = $"/ingredients/{ingredient}/code";
                patch.ValueJson = JsonSerializer.Serialize(request.Code ?? "game:stick");
                break;
            case "recipe-enabled":
                patch.Operation = "add";
                patch.Path = "/enabled";
                patch.ValueJson = request.Enabled ? "true" : "false";
                break;
        }

        patch.Clamp();
        string? validation = ValidateContentPatch(patch);
        if (validation != null && preset != "generic")
            return new ContentPatchActionResult { Success = false, Message = validation, Snapshot = GetContentSnapshot(), Generated = patch };

        return new ContentPatchActionResult
        {
            Success = true,
            Message = "Content patch template generated. Review it before saving/enabling.",
            Snapshot = GetContentSnapshot(),
            Generated = patch
        };
    }

    private string? ValidateContentPatch(ContentPatchDefinition patch)
    {
        if (string.IsNullOrWhiteSpace(patch.Name)) return "Patch name is required.";
        if (string.IsNullOrWhiteSpace(patch.File) || !patch.File.Contains(':')) return "Asset file must include a domain, for example game:recipes/grid/tool/axe.json.";
        if (!IsContentOperationSupported(patch.Operation)) return "Unsupported JSON patch operation.";
        if (string.IsNullOrWhiteSpace(patch.Path) || !patch.Path.StartsWith('/')) return "JSON patch path must start with '/'.";
        if ((patch.Operation.Equals("move", StringComparison.OrdinalIgnoreCase) || patch.Operation.Equals("copy", StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(patch.FromPath) || !patch.FromPath.StartsWith('/'))) return "Move/copy patches require a fromPath beginning with '/'.";
        if (OperationNeedsValue(patch.Operation))
        {
            try { using var _ = JsonDocument.Parse(string.IsNullOrWhiteSpace(patch.ValueJson) ? "null" : patch.ValueJson); }
            catch (Exception ex) { return "Patch value is not valid JSON: " + ex.Message; }
        }
        return null;
    }

    private void RebuildContentPatchModUnsafe()
    {
        string modPath = GetGeneratedContentModPath();
        string patchDir = Path.Combine(modPath, "assets", ContentPatchModId, "patches");
        Directory.CreateDirectory(patchDir);

        var modInfo = new Dictionary<string, object?>
        {
            ["type"] = "content",
            ["modid"] = ContentPatchModId,
            ["name"] = "Newfie's Suite Content Patches",
            ["authors"] = new[] { "Newfie" },
            ["description"] = "Generated JSON/recipe patches managed by Newfie's Server Suite Patch Studio.",
            ["version"] = "1.0.0",
            ["side"] = "Universal",
            ["requiredOnServer"] = true,
            ["requiredOnClient"] = contentStore.Patches.Any(x => x.Enabled && x.Side != "server"),
            ["dependencies"] = new Dictionary<string, string> { ["game"] = "1.22.7" }
        };
        WriteContentAtomically(Path.Combine(modPath, "modinfo.json"), JsonSerializer.Serialize(modInfo, new JsonSerializerOptions { WriteIndented = true }));

        var generated = new List<Dictionary<string, object?>>();
        foreach (var patch in contentStore.Patches.Where(x => x.Enabled))
        {
            string? validation = ValidateContentPatch(patch);
            if (validation != null)
            {
                api.Logger.Warning("[ServerEssentials/PatchStudio] Content patch '{0}' skipped while generating pack: {1}", patch.Name, validation);
                continue;
            }

            var row = new Dictionary<string, object?>
            {
                ["file"] = patch.File,
                ["op"] = NormalizeContentOperation(patch.Operation),
                ["path"] = patch.Path
            };
            if (!string.IsNullOrWhiteSpace(patch.Side)) row["side"] = patch.Side;
            if (!string.IsNullOrWhiteSpace(patch.DependsOnModId))
            {
                var dep = new Dictionary<string, object?> { ["modid"] = patch.DependsOnModId };
                if (patch.InvertDependency) dep["invert"] = true;
                row["dependsOn"] = new[] { dep };
            }
            if (patch.Operation.Equals("move", StringComparison.OrdinalIgnoreCase) || patch.Operation.Equals("copy", StringComparison.OrdinalIgnoreCase))
                row["fromPath"] = patch.FromPath;
            if (OperationNeedsValue(patch.Operation))
            {
                using JsonDocument valueDoc = JsonDocument.Parse(string.IsNullOrWhiteSpace(patch.ValueJson) ? "null" : patch.ValueJson);
                row["value"] = valueDoc.RootElement.Clone();
            }
            generated.Add(row);
        }

        WriteContentAtomically(Path.Combine(patchDir, "generated.json"), JsonSerializer.Serialize(generated, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void WriteContentAtomically(string path, string json)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false), leaveOpen: true);
                writer.Write(json); writer.Flush(); stream.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { try { File.Delete(temporary); } catch { } }
    }

    private string GetGeneratedContentModPath() => Path.Combine(api.GetOrCreateDataPath("Mods"), ContentPatchFolderName);
    private string? PersistContentChange(List<ContentPatchDefinition> previous)
    {
        try { RebuildContentPatchModUnsafe(); SaveContentUnsafe(); return null; }
        catch (Exception ex)
        {
            contentStore.Patches = previous;
            try { RebuildContentPatchModUnsafe(); SaveContentUnsafe(); }
            catch (Exception restore) { api.Logger.Error("[PatchGenerator] Failed to restore patch files after a save error: {0}", restore.Message); }
            return "Content patch could not be saved: " + ex.Message;
        }
    }
    private void SaveContentUnsafe() => SuiteConfigStorage.Store(api, contentStore, ContentConfigFile);
    private ContentPatchActionResult ContentSuccess(string message) => new() { Success = true, Message = message, Snapshot = GetContentSnapshot() };
    private ContentPatchActionResult ContentFail(string message) => new() { Success = false, Message = message, Snapshot = GetContentSnapshot() };

    private static bool OperationNeedsValue(string operation) =>
        operation.Equals("add", StringComparison.OrdinalIgnoreCase) ||
        operation.Equals("addMerge", StringComparison.OrdinalIgnoreCase) ||
        operation.Equals("addEach", StringComparison.OrdinalIgnoreCase) ||
        operation.Equals("replace", StringComparison.OrdinalIgnoreCase);

    private static bool IsContentOperationSupported(string? operation)
    {
        string op = NormalizeContentOperation(operation);
        return op is "add" or "addMerge" or "addEach" or "remove" or "replace" or "move" or "copy";
    }

    private static string NormalizeContentOperation(string? operation)
    {
        string op = (operation ?? "add").Trim();
        if (op.Equals("addmerge", StringComparison.OrdinalIgnoreCase)) return "addMerge";
        if (op.Equals("addeach", StringComparison.OrdinalIgnoreCase)) return "addEach";
        if (op.Equals("remove", StringComparison.OrdinalIgnoreCase)) return "remove";
        if (op.Equals("replace", StringComparison.OrdinalIgnoreCase)) return "replace";
        if (op.Equals("move", StringComparison.OrdinalIgnoreCase)) return "move";
        if (op.Equals("copy", StringComparison.OrdinalIgnoreCase)) return "copy";
        return "add";
    }

    private static string NormalizeSide(string? side)
    {
        string s = (side ?? "server").Trim().ToLowerInvariant();
        return s is "server" or "client" ? s : "";
    }

    private static string EscapeJsonPointerToken(string value) => (value ?? "").Replace("~", "~0").Replace("/", "~1");

    public CustomPatchActionResult SaveFromRequest(CustomPatchDefinition incoming)
    {
        lock (sync)
        {
            incoming ??= new CustomPatchDefinition();
            incoming.Clamp();
            if (string.IsNullOrWhiteSpace(incoming.Name)) return Fail("Patch name is required.");
            if (string.IsNullOrWhiteSpace(incoming.AssemblyName) || string.IsNullOrWhiteSpace(incoming.TypeName) || string.IsNullOrWhiteSpace(incoming.MethodName))
                return Fail("Assembly, type and method are required.");
            if (!IsModeSupported(incoming.Mode)) return Fail("Unsupported patch mode.");

            if (string.IsNullOrWhiteSpace(incoming.Id)) incoming.Id = Guid.NewGuid().ToString("N");
            int index = store.Patches.FindIndex(x => string.Equals(x.Id, incoming.Id, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) store.Patches[index] = incoming;
            else store.Patches.Add(incoming);

            if (store.Patches.Count > 100) store.Patches = store.Patches.TakeLast(100).ToList();
            ApplyAllUnsafe();
            SaveUnsafe();
            return Success(index >= 0 ? "Custom patch updated." : "Custom patch created.");
        }
    }

    public CustomPatchActionResult Delete(string id)
    {
        lock (sync)
        {
            int removed = store.Patches.RemoveAll(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
            if (removed == 0) return Fail("Patch was not found.");
            ApplyAllUnsafe();
            SaveUnsafe();
            return Success("Custom patch deleted.");
        }
    }

    public CustomPatchActionResult SetEnabled(string id, bool enabled)
    {
        lock (sync)
        {
            var patch = store.Patches.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
            if (patch == null) return Fail("Patch was not found.");
            patch.Enabled = enabled;
            ApplyAllUnsafe();
            SaveUnsafe();
            return Success(enabled ? "Custom patch enabled." : "Custom patch disabled.");
        }
    }

    public CustomPatchActionResult Generate(CustomPatchGenerateRequest request)
    {
        request ??= new CustomPatchGenerateRequest();
        string mode = string.IsNullOrWhiteSpace(request.Mode) ? "Observe" : request.Mode.Trim();
        if (!IsModeSupported(mode)) mode = "Observe";
        var patch = new CustomPatchDefinition
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = string.IsNullOrWhiteSpace(request.Name) ? $"{request.TypeName}.{request.MethodName} {mode}" : request.Name.Trim(),
            AssemblyName = request.AssemblyName?.Trim() ?? "",
            TypeName = request.TypeName?.Trim() ?? "",
            MethodName = request.MethodName?.Trim() ?? "",
            ParameterTypes = request.ParameterTypes?.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Take(16).ToList() ?? new(),
            Mode = mode,
            ThrottleMilliseconds = Math.Clamp(request.ThrottleMilliseconds, 50, 60_000),
            Enabled = false,
            Notes = "Generated by Patch Studio. Validate the exact target before enabling."
        };
        patch.Clamp();
        return new CustomPatchActionResult { Success = true, Message = "Patch template generated. Review it before saving/enabling.", Generated = patch };
    }

    private void ApplyAllUnsafe()
    {
        try { harmony.UnpatchAll(HarmonyId); } catch { }
        runtime.Clear();

        foreach (var patch in store.Patches)
        {
            patch.RuntimeStatus = patch.Enabled ? "Pending" : "Disabled";
            patch.RuntimeMessage = patch.Enabled ? "Waiting for validation." : "Disabled by administrator.";
            if (!patch.Enabled) continue;

            try
            {
                MethodInfo method = ResolveMethod(patch);
                ValidateTarget(patch, method);

                if (runtime.ContainsKey(method))
                    throw new InvalidOperationException("Another enabled Patch Studio definition already targets this exact method. Merge or disable the duplicate first.");

                var state = new RuntimePatchState(patch.Id, patch.Mode, patch.ThrottleMilliseconds);
                runtime[method] = state;

                if (patch.Mode.Equals("Observe", StringComparison.OrdinalIgnoreCase))
                {
                    harmony.Patch(method,
                        prefix: new HarmonyMethod(typeof(CustomPatchService), nameof(ObservePrefix)),
                        postfix: new HarmonyMethod(typeof(CustomPatchService), nameof(ObservePostfix)));
                }
                else if (patch.Mode.Equals("ThrottleVoid", StringComparison.OrdinalIgnoreCase))
                {
                    harmony.Patch(method, prefix: new HarmonyMethod(typeof(CustomPatchService), nameof(ThrottlePrefix)));
                }
                else if (patch.Mode.Equals("SkipVoid", StringComparison.OrdinalIgnoreCase))
                {
                    harmony.Patch(method, prefix: new HarmonyMethod(typeof(CustomPatchService), nameof(SkipVoidPrefix)));
                }

                patch.RuntimeStatus = "Applied";
                patch.RuntimeMessage = method.ToString() ?? method.Name;
            }
            catch (Exception ex)
            {
                patch.RuntimeStatus = "Error";
                patch.RuntimeMessage = $"{ex.GetType().Name}: {ex.Message}";
                api.Logger.Warning("[ServerEssentials/PatchStudio] Patch '{0}' was not applied: {1}", patch.Name, patch.RuntimeMessage);
            }
        }
    }

    private MethodInfo ResolveMethod(CustomPatchDefinition patch)
    {
        Assembly? assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a =>
            string.Equals(a.GetName().Name, patch.AssemblyName, StringComparison.OrdinalIgnoreCase));
        if (assembly == null) throw new InvalidOperationException($"Assembly '{patch.AssemblyName}' is not loaded.");

        Type type = assembly.GetType(patch.TypeName, throwOnError: false, ignoreCase: false)
            ?? throw new InvalidOperationException($"Type '{patch.TypeName}' was not found in {patch.AssemblyName}.");

        var methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(x => string.Equals(x.Name, patch.MethodName, StringComparison.Ordinal))
            .ToList();

        if (patch.ParameterTypes.Count > 0)
        {
            methods = methods.Where(m => ParametersMatch(m, patch.ParameterTypes)).ToList();
        }

        if (methods.Count == 0) throw new MissingMethodException(type.FullName, patch.MethodName);
        if (methods.Count > 1) throw new AmbiguousMatchException("Multiple overloads match. Add parameter type names to the patch definition.");
        return methods[0];
    }

    private static bool ParametersMatch(MethodInfo method, IReadOnlyList<string> expected)
    {
        var parameters = method.GetParameters();
        if (parameters.Length != expected.Count) return false;
        for (int i = 0; i < parameters.Length; i++)
        {
            string actualFull = parameters[i].ParameterType.FullName ?? parameters[i].ParameterType.Name;
            string actualName = parameters[i].ParameterType.Name;
            if (!string.Equals(actualFull, expected[i], StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(actualName, expected[i], StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    private static void ValidateTarget(CustomPatchDefinition patch, MethodInfo method)
    {
        if (method.ContainsGenericParameters) throw new InvalidOperationException("Open generic methods are not supported.");
        string assemblyName = method.DeclaringType?.Assembly.GetName().Name ?? "";
        if (assemblyName.Equals("NewfiesServerEssentialsSuite", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Patch Studio cannot patch Server Suite itself. Use a normal Suite build for Suite changes.");

        if (!patch.Mode.Equals("Observe", StringComparison.OrdinalIgnoreCase) && method.ReturnType != typeof(void))
            throw new InvalidOperationException("ThrottleVoid and SkipVoid are limited to void-returning methods so skipped calls cannot fabricate return values.");
    }

    private static void ObservePrefix(MethodBase __originalMethod, out long __state)
    {
        __state = Stopwatch.GetTimestamp();
    }

    private static void ObservePostfix(MethodBase __originalMethod, long __state)
    {
        current?.RecordObserved(__originalMethod, __state);
    }

    private static bool ThrottlePrefix(MethodBase __originalMethod)
    {
        return current?.ShouldRun(__originalMethod) ?? true;
    }

    private static bool SkipVoidPrefix(MethodBase __originalMethod)
    {
        current?.RecordSkipped(__originalMethod);
        return false;
    }

    private void RecordObserved(MethodBase method, long started)
    {
        if (!runtime.TryGetValue(method, out RuntimePatchState? state)) return;
        long elapsed = Math.Max(0, Stopwatch.GetTimestamp() - started);
        Interlocked.Increment(ref state.Calls);
        Interlocked.Add(ref state.TotalElapsedTicks, elapsed);
        long oldMax = Volatile.Read(ref state.MaxElapsedTicks);
        while (elapsed > oldMax)
        {
            long observed = Interlocked.CompareExchange(ref state.MaxElapsedTicks, elapsed, oldMax);
            if (observed == oldMax) break;
            oldMax = observed;
        }
    }

    private bool ShouldRun(MethodBase method)
    {
        if (!runtime.TryGetValue(method, out RuntimePatchState? state)) return true;
        long now = Stopwatch.GetTimestamp();
        long intervalTicks = Math.Max(1, (long)(state.ThrottleMilliseconds / 1000d * Stopwatch.Frequency));
        while (true)
        {
            long previous = Volatile.Read(ref state.LastAllowedTicks);
            if (previous != 0 && now - previous < intervalTicks)
            {
                Interlocked.Increment(ref state.Skipped);
                return false;
            }
            if (Interlocked.CompareExchange(ref state.LastAllowedTicks, now, previous) == previous)
            {
                Interlocked.Increment(ref state.Calls);
                return true;
            }
        }
    }

    private void RecordSkipped(MethodBase method)
    {
        if (runtime.TryGetValue(method, out RuntimePatchState? state)) Interlocked.Increment(ref state.Skipped);
    }

    private CustomPatchRow ToRow(CustomPatchDefinition patch)
    {
        RuntimePatchState? stats = runtime.FirstOrDefault(x => string.Equals(x.Value.Id, patch.Id, StringComparison.OrdinalIgnoreCase)).Value;
        long calls = stats == null ? 0 : Volatile.Read(ref stats.Calls);
        long skipped = stats == null ? 0 : Volatile.Read(ref stats.Skipped);
        long totalTicks = stats == null ? 0 : Volatile.Read(ref stats.TotalElapsedTicks);
        long maxTicks = stats == null ? 0 : Volatile.Read(ref stats.MaxElapsedTicks);
        return new CustomPatchRow
        {
            Definition = patch.Clone(),
            Calls = calls,
            Skipped = skipped,
            AverageMilliseconds = calls <= 0 || totalTicks <= 0 ? 0 : totalTicks / (double)Stopwatch.Frequency * 1000d / calls,
            MaxMilliseconds = maxTicks <= 0 ? 0 : maxTicks / (double)Stopwatch.Frequency * 1000d
        };
    }

    private List<CustomPatchSuggestion> BuildSuggestions(ServerHealthSnapshot? health)
    {
        var suggestions = new List<CustomPatchSuggestion>();
        if (health == null || health.LastProfile?.CompletedUnixMilliseconds <= 0) return suggestions;

        foreach (var mod in health.ModHotspots
                     .Where(x => !x.IsCore && !string.IsNullOrWhiteSpace(x.ModId))
                     .OrderByDescending(x => x.TotalMs)
                     .Take(6))
        {
            suggestions.Add(new CustomPatchSuggestion
            {
                Source = "Profiler",
                Title = $"Inspect {mod.ModName}",
                Detail = $"{mod.PercentOfProfileTickTime:0.0}% of profiled tick time, max segment {mod.MaxSingleMs:0.0} ms. Top marker: {mod.TopMarker}",
                Recommendation = "Use Observe first. Only consider throttling a confirmed non-critical void method after measuring it.",
                ModId = mod.ModId,
                Confidence = mod.Confidence
            });
        }
        return suggestions;
    }

    private static bool IsModeSupported(string? mode) =>
        string.Equals(mode, "Observe", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(mode, "ThrottleVoid", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(mode, "SkipVoid", StringComparison.OrdinalIgnoreCase);

    private void SaveUnsafe() => SuiteConfigStorage.Store(api, store, ConfigFile);
    private CustomPatchActionResult Success(string message) => new() { Success = true, Message = message, Snapshot = GetSnapshot() };
    private CustomPatchActionResult Fail(string message) => new() { Success = false, Message = message, Snapshot = GetSnapshot() };

    public void Dispose()
    {
        lock (sync)
        {
            try { harmony.UnpatchAll(HarmonyId); } catch { }
            runtime.Clear();
            if (ReferenceEquals(current, this)) current = null;
        }
    }

    private sealed class RuntimePatchState
    {
        public RuntimePatchState(string id, string mode, int throttleMilliseconds)
        {
            Id = id;
            Mode = mode;
            ThrottleMilliseconds = throttleMilliseconds;
        }
        public string Id { get; }
        public string Mode { get; }
        public int ThrottleMilliseconds { get; }
        public long Calls;
        public long Skipped;
        public long TotalElapsedTicks;
        public long MaxElapsedTicks;
        public long LastAllowedTicks;
    }
}

public sealed class CustomPatchStore
{
    public int SchemaVersion { get; set; } = 1;
    public List<CustomPatchDefinition> Patches { get; set; } = new();
    public void Clamp()
    {
        Patches ??= new();
        Patches = Patches.Where(x => x != null).Take(100).ToList();
        foreach (var patch in Patches) patch.Clamp();
    }
}

public sealed class CustomPatchDefinition
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Enabled { get; set; }
    public string AssemblyName { get; set; } = "";
    public string TypeName { get; set; } = "";
    public string MethodName { get; set; } = "";
    public List<string> ParameterTypes { get; set; } = new();
    public string Mode { get; set; } = "Observe";
    public int ThrottleMilliseconds { get; set; } = 250;
    public string Notes { get; set; } = "";
    public string RuntimeStatus { get; set; } = "Disabled";
    public string RuntimeMessage { get; set; } = "";

    public void Clamp()
    {
        Id = (Id ?? "").Trim();
        Name = (Name ?? "").Trim();
        AssemblyName = (AssemblyName ?? "").Trim();
        TypeName = (TypeName ?? "").Trim();
        MethodName = (MethodName ?? "").Trim();
        Mode = string.IsNullOrWhiteSpace(Mode) ? "Observe" : Mode.Trim();
        ThrottleMilliseconds = Math.Clamp(ThrottleMilliseconds, 50, 60_000);
        Notes = (Notes ?? "").Trim();
        ParameterTypes ??= new();
        ParameterTypes = ParameterTypes.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Take(16).ToList();
    }

    public CustomPatchDefinition Clone() => new()
    {
        Id = Id, Name = Name, Enabled = Enabled, AssemblyName = AssemblyName, TypeName = TypeName,
        MethodName = MethodName, ParameterTypes = new List<string>(ParameterTypes), Mode = Mode,
        ThrottleMilliseconds = ThrottleMilliseconds, Notes = Notes, RuntimeStatus = RuntimeStatus, RuntimeMessage = RuntimeMessage
    };
}

public sealed class CustomPatchGenerateRequest
{
    public string Name { get; set; } = "";
    public string AssemblyName { get; set; } = "";
    public string TypeName { get; set; } = "";
    public string MethodName { get; set; } = "";
    public List<string> ParameterTypes { get; set; } = new();
    public string Mode { get; set; } = "Observe";
    public int ThrottleMilliseconds { get; set; } = 250;
}

public sealed class CustomPatchSnapshot
{
    public bool Available { get; set; }
    public string HarmonyId { get; set; } = "";
    public string ConfigPath { get; set; } = "";
    public string[] SupportedModes { get; set; } = Array.Empty<string>();
    public List<CustomPatchRow> Patches { get; set; } = new();
    public List<CustomPatchSuggestion> Suggestions { get; set; } = new();
    public string Note { get; set; } = "";
}

public sealed class CustomPatchRow
{
    public CustomPatchDefinition Definition { get; set; } = new();
    public long Calls { get; set; }
    public long Skipped { get; set; }
    public double AverageMilliseconds { get; set; }
    public double MaxMilliseconds { get; set; }
}

public sealed class CustomPatchSuggestion
{
    public string Source { get; set; } = "";
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    public string Recommendation { get; set; } = "";
    public string ModId { get; set; } = "";
    public string Confidence { get; set; } = "Low";
}

public sealed class CustomPatchActionResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public CustomPatchSnapshot? Snapshot { get; set; }
    public CustomPatchDefinition? Generated { get; set; }
}

public sealed class ContentPatchStore
{
    public int SchemaVersion { get; set; } = 1;
    public List<ContentPatchDefinition> Patches { get; set; } = new();
    public void Clamp()
    {
        Patches ??= new();
        Patches = Patches.Where(x => x != null).Take(200).ToList();
        foreach (var patch in Patches) patch.Clamp();
    }
}

public sealed class ContentPatchDefinition
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Enabled { get; set; }
    public string File { get; set; } = "";
    public string Operation { get; set; } = "add";
    public string Path { get; set; } = "/";
    public string FromPath { get; set; } = "";
    public string ValueJson { get; set; } = "null";
    public string Side { get; set; } = "server";
    public string DependsOnModId { get; set; } = "";
    public bool InvertDependency { get; set; }
    public string Notes { get; set; } = "";

    public void Clamp()
    {
        Id = (Id ?? "").Trim();
        Name = Limit((Name ?? "").Trim(), 120);
        File = Limit((File ?? "").Trim(), 500);
        Operation = Limit((Operation ?? "add").Trim(), 20);
        Path = Limit((Path ?? "/").Trim(), 800);
        FromPath = Limit((FromPath ?? "").Trim(), 800);
        ValueJson = Limit(string.IsNullOrWhiteSpace(ValueJson) ? "null" : ValueJson.Trim(), 131072);
        Side = Limit((Side ?? "server").Trim().ToLowerInvariant(), 16);
        DependsOnModId = Limit((DependsOnModId ?? "").Trim().ToLowerInvariant(), 128);
        Notes = Limit((Notes ?? "").Trim(), 1200);
    }

    private static string Limit(string value, int max) => value.Length <= max ? value : value[..max];

    public ContentPatchDefinition Clone() => new()
    {
        Id = Id, Name = Name, Enabled = Enabled, File = File, Operation = Operation,
        Path = Path, FromPath = FromPath, ValueJson = ValueJson, Side = Side, DependsOnModId = DependsOnModId, InvertDependency = InvertDependency, Notes = Notes
    };
}

public sealed class ContentPatchGenerateRequest
{
    public bool Guided { get; set; }
    public string Id { get; set; } = "";
    public string RootPath { get; set; } = "";
    public bool Activate { get; set; }
    public string Notes { get; set; } = "";
    public string Preset { get; set; } = "generic";
    public string Name { get; set; } = "";
    public string File { get; set; } = "";
    public string Operation { get; set; } = "add";
    public string Path { get; set; } = "/";
    public string FromPath { get; set; } = "";
    public string ValueJson { get; set; } = "null";
    public string Side { get; set; } = "server";
    public string DependsOnModId { get; set; } = "";
    public bool InvertDependency { get; set; }
    public string IngredientKey { get; set; } = "A";
    public int Quantity { get; set; } = 1;
    public string Code { get; set; } = "game:stick";
    public bool Enabled { get; set; } = true;
}

public sealed class ContentPatchSnapshot
{
    public bool Available { get; set; }
    public string ConfigPath { get; set; } = "";
    public string GeneratedModPath { get; set; } = "";
    public bool RestartRequired { get; set; }
    public string[] SupportedOperations { get; set; } = Array.Empty<string>();
    public List<ContentPatchDefinition> Patches { get; set; } = new();
    public string Note { get; set; } = "";
}

public sealed class ContentPatchActionResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public ContentPatchSnapshot? Snapshot { get; set; }
    public ContentPatchDefinition? Generated { get; set; }
    public ContentPatchPreview? Preview { get; set; }
}
