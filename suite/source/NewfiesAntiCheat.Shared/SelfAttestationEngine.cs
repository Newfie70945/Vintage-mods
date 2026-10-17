using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using NewfiesAntiCheat.Shared.Models;

namespace NewfiesAntiCheat.Shared;

public static class SelfAttestationEngine
{
	public static List<int> GetCandidateMethodTokens(Assembly assembly, int max = 4096)
	{
		List<int> list = new List<int>();
		foreach (Type item in SafeTypes(assembly).OrderBy((Type t) => t.FullName, StringComparer.Ordinal))
		{
			if (item.ContainsGenericParameters)
			{
				continue;
			}
			MethodInfo[] methods;
			try
			{
				methods = item.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
			}
			catch
			{
				continue;
			}
			foreach (MethodInfo item2 in methods.OrderBy((MethodInfo m) => m.MetadataToken))
			{
				if (list.Count >= max)
				{
					return list;
				}
				try
				{
					if (item2.MetadataToken > 0 && !item2.ContainsGenericParameters)
					{
						byte[] array = item2.GetMethodBody()?.GetILAsByteArray();
						if (array != null && array.Length >= 4 && array.Length <= 65536)
						{
							list.Add(item2.MetadataToken);
						}
					}
				}
				catch
				{
				}
			}
		}
		return list;
	}

	public static List<int> GetCandidateTypeTokens(Assembly assembly, int max = 2048)
	{
		List<int> list = new List<int>();
		foreach (Type item in SafeTypes(assembly).OrderBy((Type t) => t.FullName, StringComparer.Ordinal))
		{
			if (list.Count >= max)
			{
				break;
			}
			try
			{
				if (item.MetadataToken > 0 && !string.IsNullOrWhiteSpace(item.FullName) && !item.ContainsGenericParameters)
				{
					list.Add(item.MetadataToken);
				}
			}
			catch
			{
			}
		}
		return list;
	}

	public static List<SelfAttestationResultDto> Evaluate(Assembly assembly, IEnumerable<SelfAttestationProbeDto>? probes, string challengeId, byte[]? nonce)
	{
		List<SelfAttestationResultDto> list = new List<SelfAttestationResultDto>();
		foreach (SelfAttestationProbeDto item in (probes ?? Array.Empty<SelfAttestationProbeDto>()).Take(32))
		{
			SelfAttestationResultDto selfAttestationResultDto = new SelfAttestationResultDto
			{
				ProbeId = (item.ProbeId ?? ""),
				Kind = item.Kind,
				MetadataToken = item.MetadataToken
			};
			try
			{
				int kind = item.Kind;
				if (1 == 0)
				{
				}
				string text = kind switch
				{
					1 => MeasureMethod(assembly, item.MetadataToken), 
					2 => MeasureType(assembly, item.MetadataToken), 
					3 => MeasureAssemblyReferences(assembly), 
					_ => throw new InvalidOperationException("UnsupportedProbeKind"), 
				};
				if (1 == 0)
				{
				}
				string measurement = text;
				selfAttestationResultDto.Digest = BindDigest(challengeId, nonce, item, measurement);
				selfAttestationResultDto.Success = selfAttestationResultDto.Digest.Length == 64;
				if (!selfAttestationResultDto.Success)
				{
					selfAttestationResultDto.ErrorCode = "DigestFailed";
				}
			}
			catch (ArgumentException)
			{
				selfAttestationResultDto.ErrorCode = "ResolveFailed";
			}
			catch (InvalidOperationException ex2)
			{
				selfAttestationResultDto.ErrorCode = TrimCode(ex2.Message);
			}
			catch
			{
				selfAttestationResultDto.ErrorCode = "ProbeFailed";
			}
			list.Add(selfAttestationResultDto);
		}
		return list;
	}

	public static string ComputeAggregateDigest(IEnumerable<SelfAttestationResultDto>? results)
	{
		StringBuilder stringBuilder = new StringBuilder("self-attestation-v1");
		foreach (SelfAttestationResultDto item in (results ?? Array.Empty<SelfAttestationResultDto>()).OrderBy((SelfAttestationResultDto r) => r.ProbeId, StringComparer.Ordinal).ThenBy((SelfAttestationResultDto r) => r.Kind).ThenBy((SelfAttestationResultDto r) => r.MetadataToken))
		{
			stringBuilder.Append('|').Append(item.ProbeId).Append('#')
				.Append(item.Kind)
				.Append('#')
				.Append(item.MetadataToken)
				.Append('#')
				.Append(item.Success ? '1' : '0')
				.Append('#')
				.Append(item.Digest ?? "")
				.Append('#')
				.Append(item.ErrorCode ?? "");
		}
		return Sha256(stringBuilder.ToString());
	}

	public static bool SameProbe(SelfAttestationProbeDto? a, SelfAttestationResultDto? b)
	{
		return a != null && b != null && string.Equals(a.ProbeId ?? "", b.ProbeId ?? "", StringComparison.Ordinal) && a.Kind == b.Kind && a.MetadataToken == b.MetadataToken;
	}

	private static string MeasureMethod(Assembly assembly, int token)
	{
		MethodBase methodBase = assembly.ManifestModule.ResolveMethod(token);
		if (methodBase == null || methodBase.MetadataToken != token)
		{
			throw new InvalidOperationException("MethodMissing");
		}
		MethodBody methodBody = methodBase.GetMethodBody();
		if (methodBody == null)
		{
			throw new InvalidOperationException("MethodBodyMissing");
		}
		byte[] iLAsByteArray = methodBody.GetILAsByteArray();
		if (iLAsByteArray == null)
		{
			throw new InvalidOperationException("MethodBodyMissing");
		}
		string text = ((methodBase is MethodInfo methodInfo) ? TypeName(methodInfo.ReturnType) : "void");
		string text2 = string.Join(",", from p in methodBase.GetParameters()
			select TypeName(p.ParameterType));
		string text3 = string.Join(",", methodBody.LocalVariables.Select((LocalVariableInfo v) => TypeName(v.LocalType) + (v.IsPinned ? "!" : "")));
		string text4 = string.Join(",", methodBody.ExceptionHandlingClauses.Select((ExceptionHandlingClause c) => ((int)c.Flags).ToString(CultureInfo.InvariantCulture) + ":" + c.TryOffset + ":" + c.TryLength + ":" + c.HandlerOffset + ":" + c.HandlerLength + ":" + ((c.Flags == ExceptionHandlingClauseOptions.Clause) ? TypeName(c.CatchType) : "") + ":" + ((c.Flags == ExceptionHandlingClauseOptions.Filter) ? c.FilterOffset.ToString(CultureInfo.InvariantCulture) : "")));
		return string.Join("|", new string[13]
		{
			"method-v1",
			token.ToString(CultureInfo.InvariantCulture),
			TypeName(methodBase.DeclaringType),
			methodBase.Name,
			text,
			text2,
			((int)methodBase.Attributes).ToString(CultureInfo.InvariantCulture),
			((int)methodBase.GetMethodImplementationFlags()).ToString(CultureInfo.InvariantCulture),
			methodBody.MaxStackSize.ToString(CultureInfo.InvariantCulture),
			methodBody.InitLocals ? "1" : "0",
			text3,
			text4,
			Sha256(iLAsByteArray)
		});
	}

	private static string MeasureType(Assembly assembly, int token)
	{
		Type type = assembly.ManifestModule.ResolveType(token);
		BindingFlags bindingAttr = BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
		string text = string.Join(",", type.GetInterfaces().Select(TypeName).OrderBy((string x) => x, StringComparer.Ordinal));
		string text2 = string.Join(";", (from f in type.GetFields(bindingAttr)
			select f.Name + ":" + TypeName(f.FieldType) + ":" + (int)f.Attributes).OrderBy((string x) => x, StringComparer.Ordinal));
		string text3 = string.Join(";", type.GetMethods(bindingAttr).Select(MethodSignature).OrderBy((string x) => x, StringComparer.Ordinal));
		string text4 = string.Join(";", (from p in type.GetProperties(bindingAttr)
			select p.Name + ":" + TypeName(p.PropertyType) + ":" + string.Join(",", from x in p.GetIndexParameters()
				select TypeName(x.ParameterType))).OrderBy((string x) => x, StringComparer.Ordinal));
		string text5 = string.Join(";", (from e in type.GetEvents(bindingAttr)
			select e.Name + ":" + TypeName(e.EventHandlerType)).OrderBy((string x) => x, StringComparer.Ordinal));
		string text6;
		try
		{
			text6 = string.Join(",", (from a in type.GetCustomAttributesData()
				select TypeName(a.AttributeType)).OrderBy((string x) => x, StringComparer.Ordinal));
		}
		catch
		{
			text6 = "";
		}
		return string.Join("|", new string[11]
		{
			"type-v1",
			token.ToString(CultureInfo.InvariantCulture),
			TypeName(type),
			TypeName(type.BaseType),
			((int)type.Attributes).ToString(CultureInfo.InvariantCulture),
			text,
			text2,
			text3,
			text4,
			text5,
			text6
		});
	}

	private static string MeasureAssemblyReferences(Assembly assembly)
	{
		string text = string.Join(";", (from a in assembly.GetReferencedAssemblies()
			select a.FullName ?? a.Name ?? "").OrderBy((string x) => x, StringComparer.Ordinal));
		string text2;
		try
		{
			text2 = assembly.ManifestModule.ModuleVersionId.ToString("D");
		}
		catch
		{
			text2 = "";
		}
		InlineArray4<string> buffer = default;
		buffer[0] = "refs-v1";
		buffer[1] = assembly.GetName().FullName ?? "";
		buffer[2] = text2;
		buffer[3] = text;
		return string.Join("|", (ReadOnlySpan<string?>)buffer);
	}

	private static string MethodSignature(MethodInfo method)
	{
		string text = string.Join(",", from p in method.GetParameters()
			select TypeName(p.ParameterType));
		return method.Name + "(" + text + ")->" + TypeName(method.ReturnType) + ":" + (int)method.Attributes + ":" + method.GetGenericArguments().Length;
	}

	private static string BindDigest(string challengeId, byte[]? nonce, SelfAttestationProbeDto probe, string measurement)
	{
		string text = Sha256(measurement);
		InlineArray7<object> buffer = default;
		buffer[0] = "self-probe-v1";
		buffer[1] = challengeId ?? "";
		buffer[2] = Convert.ToHexString(nonce ?? Array.Empty<byte>());
		buffer[3] = probe.ProbeId ?? "";
		buffer[4] = probe.Kind;
		buffer[5] = probe.MetadataToken;
		buffer[6] = text;
		return Sha256(string.Join("|", (ReadOnlySpan<object?>)buffer));
	}

	private static IEnumerable<Type> SafeTypes(Assembly assembly)
	{
		try
		{
			return assembly.GetTypes();
		}
		catch (ReflectionTypeLoadException ex)
		{
			return ex.Types.OfType<Type>();
		}
		catch
		{
			return Array.Empty<Type>();
		}
	}

	private static string TypeName(Type? type)
	{
		if (type == null)
		{
			return "";
		}
		if (type.IsByRef)
		{
			return TypeName(type.GetElementType()) + "&";
		}
		if (type.IsPointer)
		{
			return TypeName(type.GetElementType()) + "*";
		}
		if (type.IsArray)
		{
			return TypeName(type.GetElementType()) + "[" + new string(',', Math.Max(0, type.GetArrayRank() - 1)) + "]";
		}
		if (type.IsGenericType)
		{
			return (type.GetGenericTypeDefinition().FullName ?? type.Name) + "<" + string.Join(",", type.GetGenericArguments().Select(TypeName)) + ">";
		}
		return type.FullName ?? type.Name;
	}

	private static string Sha256(string text)
	{
		return Sha256(Encoding.UTF8.GetBytes(text ?? ""));
	}

	private static string Sha256(byte[] bytes)
	{
		using SHA256 sHA = SHA256.Create();
		return Convert.ToHexString(sHA.ComputeHash(bytes ?? Array.Empty<byte>())).ToLowerInvariant();
	}

	private static string TrimCode(string? value)
	{
		string text = (value ?? "ProbeFailed").Trim();
		if (text.Length == 0)
		{
			return "ProbeFailed";
		}
		return (text.Length <= 48) ? text : text.Substring(0, 48);
	}
}
