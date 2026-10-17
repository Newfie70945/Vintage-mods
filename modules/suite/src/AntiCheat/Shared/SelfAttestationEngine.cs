using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using NewfiesAntiCheat.Shared.Models;

namespace NewfiesAntiCheat.Shared;

/// <summary>
/// Challenge-bound reflection/IL measurements used as an additional anti-tamper layer.
/// The server chooses fresh probes from its own trusted Client Integrity assembly and compares
/// the client's answers against measurements made from that server-installed assembly.
/// </summary>
public static class SelfAttestationEngine
{
    public static List<int> GetCandidateMethodTokens(Assembly assembly, int max = 4096)
    {
        var tokens = new List<int>();
        foreach (var type in SafeTypes(assembly).OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            if (type.ContainsGenericParameters) continue;
            MethodInfo[] methods;
            try { methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly); }
            catch { continue; }
            foreach (var method in methods.OrderBy(m => m.MetadataToken))
            {
                if (tokens.Count >= max) return tokens;
                try
                {
                    if (method.MetadataToken <= 0 || method.ContainsGenericParameters) continue;
                    var body = method.GetMethodBody();
                    var il = body?.GetILAsByteArray();
                    if (il == null || il.Length < 4 || il.Length > 64 * 1024) continue;
                    tokens.Add(method.MetadataToken);
                }
                catch { }
            }
        }
        return tokens;
    }

    public static List<int> GetCandidateTypeTokens(Assembly assembly, int max = 2048)
    {
        var tokens = new List<int>();
        foreach (var type in SafeTypes(assembly).OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            if (tokens.Count >= max) break;
            try
            {
                if (type.MetadataToken <= 0 || string.IsNullOrWhiteSpace(type.FullName) || type.ContainsGenericParameters) continue;
                tokens.Add(type.MetadataToken);
            }
            catch { }
        }
        return tokens;
    }

    public static List<SelfAttestationResultDto> Evaluate(Assembly assembly, IEnumerable<SelfAttestationProbeDto>? probes, string challengeId, byte[]? nonce)
    {
        var results = new List<SelfAttestationResultDto>();
        foreach (var probe in (probes ?? Array.Empty<SelfAttestationProbeDto>()).Take(32))
        {
            var result = new SelfAttestationResultDto
            {
                ProbeId = probe.ProbeId ?? "",
                Kind = probe.Kind,
                MetadataToken = probe.MetadataToken
            };
            try
            {
                string measurement = probe.Kind switch
                {
                    (int)SelfAttestationProbeKind.MethodIl => MeasureMethod(assembly, probe.MetadataToken),
                    (int)SelfAttestationProbeKind.TypeMetadata => MeasureType(assembly, probe.MetadataToken),
                    (int)SelfAttestationProbeKind.AssemblyReferences => MeasureAssemblyReferences(assembly),
                    _ => throw new InvalidOperationException("UnsupportedProbeKind")
                };
                result.Digest = BindDigest(challengeId, nonce, probe, measurement);
                result.Success = result.Digest.Length == 64;
                if (!result.Success) result.ErrorCode = "DigestFailed";
            }
            catch (ArgumentException) { result.ErrorCode = "ResolveFailed"; }
            catch (InvalidOperationException ex) { result.ErrorCode = TrimCode(ex.Message); }
            catch { result.ErrorCode = "ProbeFailed"; }
            results.Add(result);
        }
        return results;
    }

    public static string ComputeAggregateDigest(IEnumerable<SelfAttestationResultDto>? results)
    {
        var builder = new StringBuilder("self-attestation-v1");
        foreach (var result in (results ?? Array.Empty<SelfAttestationResultDto>())
                     .OrderBy(r => r.ProbeId, StringComparer.Ordinal)
                     .ThenBy(r => r.Kind)
                     .ThenBy(r => r.MetadataToken))
        {
            builder.Append('|').Append(result.ProbeId)
                .Append('#').Append(result.Kind)
                .Append('#').Append(result.MetadataToken)
                .Append('#').Append(result.Success ? '1' : '0')
                .Append('#').Append(result.Digest ?? "")
                .Append('#').Append(result.ErrorCode ?? "");
        }
        return Sha256(builder.ToString());
    }

    public static bool SameProbe(SelfAttestationProbeDto? a, SelfAttestationResultDto? b) =>
        a != null && b != null &&
        string.Equals(a.ProbeId ?? "", b.ProbeId ?? "", StringComparison.Ordinal) &&
        a.Kind == b.Kind && a.MetadataToken == b.MetadataToken;

    private static string MeasureMethod(Assembly assembly, int token)
    {
        MethodBase? method = assembly.ManifestModule.ResolveMethod(token);
        if (method == null || method.MetadataToken != token) throw new InvalidOperationException("MethodMissing");
        MethodBody? body = method.GetMethodBody();
        if (body == null) throw new InvalidOperationException("MethodBodyMissing");
        byte[]? il = body.GetILAsByteArray();
        if (il == null) throw new InvalidOperationException("MethodBodyMissing");

        string returnType = method is MethodInfo mi ? TypeName(mi.ReturnType) : "void";
        string parameters = string.Join(",", method.GetParameters().Select(p => TypeName(p.ParameterType)));
        string locals = string.Join(",", body.LocalVariables.Select(v => TypeName(v.LocalType) + (v.IsPinned ? "!" : "")));
        string clauses = string.Join(",", body.ExceptionHandlingClauses.Select(c =>
            ((int)c.Flags).ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + c.TryOffset + ":" + c.TryLength + ":" + c.HandlerOffset + ":" + c.HandlerLength + ":" +
            (c.Flags == ExceptionHandlingClauseOptions.Clause ? TypeName(c.CatchType) : "") + ":" +
            (c.Flags == ExceptionHandlingClauseOptions.Filter ? c.FilterOffset.ToString(System.Globalization.CultureInfo.InvariantCulture) : "")));
        string raw = string.Join("|", new[]
        {
            "method-v1", token.ToString(System.Globalization.CultureInfo.InvariantCulture),
            TypeName(method.DeclaringType), method.Name, returnType, parameters,
            ((int)method.Attributes).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ((int)method.GetMethodImplementationFlags()).ToString(System.Globalization.CultureInfo.InvariantCulture),
            body.MaxStackSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
            body.InitLocals ? "1" : "0", locals, clauses,
            Sha256(il)
        });
        return raw;
    }

    private static string MeasureType(Assembly assembly, int token)
    {
        Type type = assembly.ManifestModule.ResolveType(token);
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        string interfaces = string.Join(",", type.GetInterfaces().Select(TypeName).OrderBy(x => x, StringComparer.Ordinal));
        string fields = string.Join(";", type.GetFields(flags).Select(f => f.Name + ":" + TypeName(f.FieldType) + ":" + (int)f.Attributes).OrderBy(x => x, StringComparer.Ordinal));
        string methods = string.Join(";", type.GetMethods(flags).Select(MethodSignature).OrderBy(x => x, StringComparer.Ordinal));
        string properties = string.Join(";", type.GetProperties(flags).Select(p => p.Name + ":" + TypeName(p.PropertyType) + ":" + string.Join(",", p.GetIndexParameters().Select(x => TypeName(x.ParameterType)))).OrderBy(x => x, StringComparer.Ordinal));
        string events = string.Join(";", type.GetEvents(flags).Select(e => e.Name + ":" + TypeName(e.EventHandlerType)).OrderBy(x => x, StringComparer.Ordinal));
        string attributes;
        try { attributes = string.Join(",", type.GetCustomAttributesData().Select(a => TypeName(a.AttributeType)).OrderBy(x => x, StringComparer.Ordinal)); }
        catch { attributes = ""; }
        return string.Join("|", new[]
        {
            "type-v1", token.ToString(System.Globalization.CultureInfo.InvariantCulture), TypeName(type), TypeName(type.BaseType),
            ((int)type.Attributes).ToString(System.Globalization.CultureInfo.InvariantCulture), interfaces, fields, methods, properties, events, attributes
        });
    }

    private static string MeasureAssemblyReferences(Assembly assembly)
    {
        string refs = string.Join(";", assembly.GetReferencedAssemblies().Select(a => a.FullName ?? a.Name ?? "").OrderBy(x => x, StringComparer.Ordinal));
        string mvid;
        try { mvid = assembly.ManifestModule.ModuleVersionId.ToString("D"); } catch { mvid = ""; }
        return string.Join("|", "refs-v1", assembly.GetName().FullName ?? "", mvid, refs);
    }

    private static string MethodSignature(MethodInfo method)
    {
        string parameters = string.Join(",", method.GetParameters().Select(p => TypeName(p.ParameterType)));
        return method.Name + "(" + parameters + ")->" + TypeName(method.ReturnType) + ":" + (int)method.Attributes + ":" + method.GetGenericArguments().Length;
    }

    private static string BindDigest(string challengeId, byte[]? nonce, SelfAttestationProbeDto probe, string measurement)
    {
        string measured = Sha256(measurement);
        return Sha256(string.Join("|", "self-probe-v1", challengeId ?? "", Convert.ToHexString(nonce ?? Array.Empty<byte>()), probe.ProbeId ?? "", probe.Kind, probe.MetadataToken, measured));
    }

    private static IEnumerable<Type> SafeTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.OfType<Type>(); }
        catch { return Array.Empty<Type>(); }
    }

    private static string TypeName(Type? type)
    {
        if (type == null) return "";
        if (type.IsByRef) return TypeName(type.GetElementType()) + "&";
        if (type.IsPointer) return TypeName(type.GetElementType()) + "*";
        if (type.IsArray) return TypeName(type.GetElementType()) + "[" + new string(',', Math.Max(0, type.GetArrayRank() - 1)) + "]";
        if (type.IsGenericType)
            return (type.GetGenericTypeDefinition().FullName ?? type.Name) + "<" + string.Join(",", type.GetGenericArguments().Select(TypeName)) + ">";
        return type.FullName ?? type.Name;
    }

    private static string Sha256(string text) => Sha256(Encoding.UTF8.GetBytes(text ?? ""));
    private static string Sha256(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(bytes ?? Array.Empty<byte>())).ToLowerInvariant();
    }

    private static string TrimCode(string? value)
    {
        string v = (value ?? "ProbeFailed").Trim();
        if (v.Length == 0) return "ProbeFailed";
        return v.Length <= 48 ? v : v[..48];
    }
}
