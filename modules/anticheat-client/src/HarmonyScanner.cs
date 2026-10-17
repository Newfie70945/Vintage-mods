using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using NewfiesAntiCheat.Shared;
using NewfiesAntiCheat.Shared.Models;

namespace NewfiesAntiCheatClient;

internal static class HarmonyScanner
{
    private const int MaxPatches = 512;
    private const string KnownModMenuOwner = "com.br3zzly.modmenu";

    // Resolving Harmony's reflection API is stable for the lifetime of a normal VS client
    // process. Cache those MethodInfo objects instead of rescanning HarmonyLib's type/method
    // metadata for every heartbeat and deep attestation.
    private static readonly object HarmonyApiLock = new();
    private static Assembly? cachedHarmonyAssembly;
    private static MethodInfo? cachedGetAllPatchedMethods;
    private static MethodInfo? cachedGetPatchInfo;

    public static ClientHarmonyDto Scan(Dictionary<Assembly, HashSet<string>>? associations, List<ScanFindingDto>? findings = null)
    {
        var result = new ClientHarmonyDto { PatchSetDigest = ModContentHasher.HashBytes(Array.Empty<byte>()) };
        try
        {
            if (!TryGetHarmonyApi(out MethodInfo getAll, out MethodInfo getInfo))
                return result;
            result.HarmonyDetected = true;

            var all = getAll.Invoke(null, null) as IEnumerable;
            if (all == null) return result;

            int targetCount = 0;
            foreach (object? targetObject in all)
            {
                if (result.Patches.Count >= MaxPatches) break;
                if ((++targetCount & 63) == 0) Thread.Yield();
                if (targetObject is not MethodBase target) continue;
                object? patchInfo;
                try { patchInfo = getInfo.Invoke(null, new object?[] { target }); }
                catch { continue; }
                if (patchInfo == null) continue;

                AddKind(result.Patches, patchInfo, "Prefixes", "prefix", target, associations);
                AddKind(result.Patches, patchInfo, "Postfixes", "postfix", target, associations);
                AddKind(result.Patches, patchInfo, "Transpilers", "transpiler", target, associations);
                AddKind(result.Patches, patchInfo, "Finalizers", "finalizer", target, associations);
            }

            result.PatchCount = result.Patches.Count;
            result.SuspiciousOwnerCount = result.Patches
                .Select(p => p.Owner).Where(o => o.Equals(KnownModMenuOwner, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase).Count();
            result.PatchSetDigest = ComputeDigest(result.Patches);

            // Suspicious owner/target policy is evaluated again on the server from the
            // census below. The client intentionally does not award its own cheat risk here.
        }
        catch (Exception ex)
        {
            findings?.Add(new ScanFindingDto
            {
                Scanner = "Harmony",
                Code = "HARMONY_SCAN_ERROR",
                Severity = (int)AntiCheatSeverity.Info,
                Summary = "Harmony patch census could not be completed.",
                Subject = "HarmonyLib",
                Evidence = Trim(ex.Message, 200)
            });
        }
        return result;
    }

    private static bool TryGetHarmonyApi(out MethodInfo getAll, out MethodInfo getInfo)
    {
        lock (HarmonyApiLock)
        {
            if (cachedHarmonyAssembly != null &&
                cachedGetAllPatchedMethods != null &&
                cachedGetPatchInfo != null)
            {
                getAll = cachedGetAllPatchedMethods;
                getInfo = cachedGetPatchInfo;
                return true;
            }

            Assembly? harmonyAssembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a =>
                string.Equals(SafeAssemblyName(a), "0Harmony", StringComparison.OrdinalIgnoreCase) ||
                a.GetType("HarmonyLib.Harmony", throwOnError: false) != null);
            Type? harmonyType = harmonyAssembly?.GetType("HarmonyLib.Harmony", throwOnError: false);
            if (harmonyType == null)
            {
                getAll = null!;
                getInfo = null!;
                return false;
            }

            MethodInfo? resolvedGetAll = harmonyType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "GetAllPatchedMethods" && m.GetParameters().Length == 0);
            MethodInfo? resolvedGetInfo = harmonyType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "GetPatchInfo" && m.GetParameters().Length == 1);
            if (resolvedGetAll == null || resolvedGetInfo == null)
            {
                getAll = null!;
                getInfo = null!;
                return false;
            }

            cachedHarmonyAssembly = harmonyAssembly;
            cachedGetAllPatchedMethods = resolvedGetAll;
            cachedGetPatchInfo = resolvedGetInfo;
            getAll = resolvedGetAll;
            getInfo = resolvedGetInfo;
            return true;
        }
    }

    private static void AddKind(List<ClientHarmonyPatchDto> output, object patchInfo, string propertyName, string kind, MethodBase target, Dictionary<Assembly, HashSet<string>>? associations)
    {
        if (output.Count >= MaxPatches) return;
        PropertyInfo? property = patchInfo.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property?.GetValue(patchInfo) is not IEnumerable list) return;
        foreach (object? patch in list)
        {
            if (output.Count >= MaxPatches) break;
            if (patch == null) continue;
            string owner = ReadString(patch, "owner", "Owner");
            MethodInfo? patchMethod = ReadMember(patch, "PatchMethod", "patchMethod") as MethodInfo;
            Assembly? patchAssembly = patchMethod?.DeclaringType?.Assembly;
            output.Add(new ClientHarmonyPatchDto
            {
                Owner = Trim(owner, 128),
                TargetAssembly = Trim(target.DeclaringType?.Assembly.GetName().Name ?? "", 128),
                TargetType = Trim(target.DeclaringType?.FullName ?? "", 256),
                TargetMethod = Trim(target.Name ?? "", 128),
                PatchAssembly = Trim(patchAssembly?.GetName().Name ?? "", 128),
                PatchType = Trim(patchMethod?.DeclaringType?.FullName ?? "", 256),
                PatchMethod = Trim(patchMethod?.Name ?? "", 128),
                Kind = kind,
                PatchAssemblyModIds = patchAssembly != null && associations != null && associations.TryGetValue(patchAssembly, out var ids)
                    ? ids.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(16).ToList()
                    : new List<string>()
            });
        }
    }

    private static object? ReadMember(object source, params string[] names)
    {
        var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        Type t = source.GetType();
        foreach (string name in names)
        {
            try
            {
                var p = t.GetProperty(name, flags);
                if (p != null) return p.GetValue(source);
                var f = t.GetField(name, flags);
                if (f != null) return f.GetValue(source);
            }
            catch { }
        }
        return null;
    }

    private static string ReadString(object source, params string[] names) => ReadMember(source, names)?.ToString() ?? "";

    private static string ComputeDigest(IEnumerable<ClientHarmonyPatchDto> patches)
    {
        string raw = string.Join("\n", patches.OrderBy(p => p.TargetAssembly, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.TargetType, StringComparer.Ordinal).ThenBy(p => p.TargetMethod, StringComparer.Ordinal)
            .ThenBy(p => p.Owner, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Kind, StringComparer.OrdinalIgnoreCase)
            .Select(p => string.Join("|", p.Owner, p.TargetAssembly, p.TargetType, p.TargetMethod, p.PatchAssembly, p.PatchType, p.PatchMethod, p.Kind,
                string.Join(",", (p.PatchAssemblyModIds ?? new()).OrderBy(x => x, StringComparer.OrdinalIgnoreCase)))));
        return ModContentHasher.HashBytes(Encoding.UTF8.GetBytes(raw));
    }

    internal static string SuspiciousCategory(ClientHarmonyPatchDto patch)
    {
        string type = patch.TargetType ?? "";
        string method = patch.TargetMethod ?? "";
        if (type.EndsWith("EntityControls", StringComparison.Ordinal) && method.Equals("CalcMovementVectors", StringComparison.Ordinal)) return "movement";
        if (method.Equals("RayTraceForSelection", StringComparison.Ordinal) || method.Equals("GetPickingRayByMouseCoordinates", StringComparison.Ordinal)) return "reach";
        if (method.Equals("OnGettingBroken", StringComparison.Ordinal) || method.Equals("OnPlayerTryDestroyBlock", StringComparison.Ordinal)) return "mining";
        if (method.Contains("ReceiveDamage", StringComparison.OrdinalIgnoreCase) || method.Contains("Attack", StringComparison.OrdinalIgnoreCase)) return "combat";
        return "";
    }

    private static string SafeAssemblyName(Assembly asm) { try { return asm.GetName().Name ?? ""; } catch { return ""; } }
    private static string Trim(string? value, int max) => string.IsNullOrEmpty(value) ? "" : value.Length <= max ? value : value[..max];
}
