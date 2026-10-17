using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using NewfiesAntiCheat.Shared.Models;

namespace NewfiesAntiCheat.Shared;

public static class ReportHasher
{
	public static string Compute(string challengeId, byte[]? nonce, IEnumerable<ClientModDto> mods, IEnumerable<ScanFindingDto> findings, IEnumerable<ClientAssemblyDto>? assemblies = null, ClientIntegrityDto? integrity = null, ClientRuntimeDto? runtime = null, IEnumerable<ClientScannerMetricDto>? scannerMetrics = null, ClientHarmonyDto? harmony = null, IEnumerable<SelfAttestationResultDto>? selfAttestation = null)
	{
		StringBuilder stringBuilder = new StringBuilder();
		stringBuilder.Append(challengeId).Append('|').Append(Convert.ToHexString(nonce ?? Array.Empty<byte>()));
		foreach (ClientModDto item in (mods ?? Array.Empty<ClientModDto>()).OrderBy((ClientModDto m) => m.ModId, StringComparer.OrdinalIgnoreCase).ThenBy((ClientModDto m) => m.FileName, StringComparer.OrdinalIgnoreCase))
		{
			stringBuilder.Append('|').Append(item.ModId).Append('@')
				.Append(item.Version)
				.Append('#')
				.Append(item.Sha256)
				.Append('#')
				.Append(item.ContentSha256)
				.Append('#')
				.Append(item.FileName)
				.Append("#bytes:")
				.Append(item.PackageBytes)
				.Append("#dlls:")
				.Append(item.DllCount)
				.Append("#dllbytes:")
				.Append(item.DllBytes);
		}
		foreach (ClientAssemblyDto item2 in (assemblies ?? Array.Empty<ClientAssemblyDto>()).OrderBy((ClientAssemblyDto a) => a.AssemblyName, StringComparer.OrdinalIgnoreCase).ThenBy((ClientAssemblyDto a) => a.ModuleVersionId, StringComparer.OrdinalIgnoreCase))
		{
			stringBuilder.Append("|asm:").Append(item2.AssemblyName).Append('@')
				.Append(item2.Version)
				.Append('#')
				.Append(item2.Sha256)
				.Append('#')
				.Append(item2.ModuleVersionId)
				.Append('#')
				.Append(item2.StructureSha256)
				.Append("#behavior:")
				.Append(item2.BehaviorSha256)
				.Append("#bytes:")
				.Append(item2.FileSizeBytes);
			foreach (string item3 in (item2.AssociatedModIds ?? new List<string>()).OrderBy((string x) => x, StringComparer.OrdinalIgnoreCase))
			{
				stringBuilder.Append('#').Append(item3);
			}
			foreach (string item4 in (item2.ModSystemTypeNames ?? new List<string>()).OrderBy((string x) => x, StringComparer.Ordinal))
			{
				stringBuilder.Append('#').Append(item4);
			}
			stringBuilder.Append("#tokens:").Append(item2.StructureTokenCount);
			foreach (ulong item5 in (item2.SimilarityMinHash ?? new List<ulong>()).Take(64))
			{
				stringBuilder.Append('#').Append(item5);
			}
			stringBuilder.Append("#behaviorTokens:").Append(item2.BehaviorTokenCount);
			foreach (ulong item6 in (item2.BehaviorMinHash ?? new List<ulong>()).Take(64))
			{
				stringBuilder.Append('#').Append(item6);
			}
		}
		if (integrity != null)
		{
			stringBuilder.Append("|integrity:").Append(integrity.AssemblyName).Append('@')
				.Append(integrity.AssemblyVersion)
				.Append('#')
				.Append(integrity.AssemblySha256)
				.Append('#')
				.Append(integrity.ModuleVersionId)
				.Append('#')
				.Append(integrity.PackageSha256)
				.Append("#content:")
				.Append(integrity.ContentSha256)
				.Append("#asmbytes:")
				.Append(integrity.AssemblyFileSizeBytes)
				.Append("#pkgbytes:")
				.Append(integrity.PackageBytes)
				.Append("#dlls:")
				.Append(integrity.DllCount)
				.Append("#dllbytes:")
				.Append(integrity.DllBytes)
				.Append("#manifest:")
				.Append(integrity.BuildManifestSha256)
				.Append("#manifestsig:")
				.Append(integrity.BuildManifestSignatureSha256)
				.Append("#build:")
				.Append(integrity.BuildId);
		}
		if (runtime != null)
		{
			stringBuilder.Append("|runtime:").Append(runtime.BaselineUtc).Append('#')
				.Append(runtime.BaselineAssemblyCount)
				.Append('#')
				.Append(runtime.LoadedAssemblyCount)
				.Append('#')
				.Append(runtime.DynamicAssemblyCount)
				.Append('#')
				.Append(runtime.RuntimeGeneration)
				.Append('#')
				.Append(runtime.AssemblySetDigest)
				.Append('#')
				.Append(runtime.DuplicateAssemblyIdentityCount)
				.Append('#')
				.Append(runtime.UnassociatedNewAssemblyCount);
			foreach (ClientRuntimeAssemblyDto item7 in (runtime.NewAssemblies ?? new List<ClientRuntimeAssemblyDto>()).OrderBy((ClientRuntimeAssemblyDto x) => x.AssemblyName, StringComparer.OrdinalIgnoreCase).ThenBy((ClientRuntimeAssemblyDto x) => x.ModuleVersionId, StringComparer.OrdinalIgnoreCase))
			{
				stringBuilder.Append("|runtimeasm:").Append(item7.AssemblyName).Append('@')
					.Append(item7.Version)
					.Append('#')
					.Append(item7.FileName)
					.Append('#')
					.Append(item7.ModuleVersionId)
					.Append('#')
					.Append(item7.Sha256)
					.Append('#')
					.Append(item7.IsDynamic ? '1' : '0')
					.Append('#')
					.Append(item7.LoadContextName)
					.Append('#')
					.Append(item7.AssociatedWithLoadedMod ? '1' : '0');
			}
		}
		if (harmony != null)
		{
			stringBuilder.Append("|harmony:").Append(harmony.HarmonyDetected ? '1' : '0').Append('#')
				.Append(harmony.PatchCount)
				.Append('#')
				.Append(harmony.PatchSetDigest)
				.Append('#')
				.Append(harmony.SuspiciousOwnerCount);
			foreach (ClientHarmonyPatchDto item8 in (harmony.Patches ?? new List<ClientHarmonyPatchDto>()).OrderBy((ClientHarmonyPatchDto x) => x.TargetAssembly, StringComparer.OrdinalIgnoreCase).ThenBy((ClientHarmonyPatchDto x) => x.TargetType, StringComparer.Ordinal).ThenBy((ClientHarmonyPatchDto x) => x.TargetMethod, StringComparer.Ordinal)
				.ThenBy((ClientHarmonyPatchDto x) => x.Owner, StringComparer.OrdinalIgnoreCase)
				.ThenBy((ClientHarmonyPatchDto x) => x.Kind, StringComparer.OrdinalIgnoreCase)
				.Take(512))
			{
				stringBuilder.Append("|patch:").Append(item8.Owner).Append('#')
					.Append(item8.TargetAssembly)
					.Append('#')
					.Append(item8.TargetType)
					.Append('#')
					.Append(item8.TargetMethod)
					.Append('#')
					.Append(item8.PatchAssembly)
					.Append('#')
					.Append(item8.PatchType)
					.Append('#')
					.Append(item8.PatchMethod)
					.Append('#')
					.Append(item8.Kind);
				foreach (string item9 in (item8.PatchAssemblyModIds ?? new List<string>()).OrderBy((string x) => x, StringComparer.OrdinalIgnoreCase))
				{
					stringBuilder.Append('#').Append(item9);
				}
			}
		}
		foreach (ClientScannerMetricDto item10 in (scannerMetrics ?? Array.Empty<ClientScannerMetricDto>()).OrderBy((ClientScannerMetricDto x) => x.Scanner, StringComparer.OrdinalIgnoreCase))
		{
			stringBuilder.Append("|metric:").Append(item10.Scanner).Append('#')
				.Append(item10.DurationMs)
				.Append('#')
				.Append(item10.Success ? '1' : '0')
				.Append('#')
				.Append(item10.ModCount)
				.Append('#')
				.Append(item10.AssemblyCount)
				.Append('#')
				.Append(item10.FindingCount)
				.Append('#')
				.Append(item10.CacheHits)
				.Append('#')
				.Append(item10.CacheMisses);
		}
		foreach (SelfAttestationResultDto item11 in (selfAttestation ?? Array.Empty<SelfAttestationResultDto>()).OrderBy((SelfAttestationResultDto x) => x.ProbeId, StringComparer.Ordinal).ThenBy((SelfAttestationResultDto x) => x.Kind).ThenBy((SelfAttestationResultDto x) => x.MetadataToken))
		{
			stringBuilder.Append("|selfprobe:").Append(item11.ProbeId).Append('#')
				.Append(item11.Kind)
				.Append('#')
				.Append(item11.MetadataToken)
				.Append('#')
				.Append(item11.Success ? '1' : '0')
				.Append('#')
				.Append(item11.Digest)
				.Append('#')
				.Append(item11.ErrorCode);
		}
		foreach (ScanFindingDto item12 in from f in findings ?? Array.Empty<ScanFindingDto>()
			orderby f.Scanner, f.Code, f.Subject
			select f)
		{
			stringBuilder.Append('|').Append(item12.Scanner).Append(':')
				.Append(item12.Code)
				.Append(':')
				.Append(item12.Subject)
				.Append(':')
				.Append(item12.Severity)
				.Append(':')
				.Append(item12.Evidence);
		}
		using SHA256 sHA = SHA256.Create();
		return Convert.ToHexString(sHA.ComputeHash(Encoding.UTF8.GetBytes(stringBuilder.ToString()))).ToLowerInvariant();
	}

	public static string ComputeV2(int protocolVersion, string clientVersion, long scanStartedUtc, long scanCompletedUtc, int completedScanners, string challengeId, byte[]? nonce, IEnumerable<ClientModDto> mods, IEnumerable<ScanFindingDto> findings, IEnumerable<ClientAssemblyDto>? assemblies = null, ClientIntegrityDto? integrity = null, ClientRuntimeDto? runtime = null, IEnumerable<ClientScannerMetricDto>? scannerMetrics = null, ClientHarmonyDto? harmony = null, IEnumerable<SelfAttestationResultDto>? selfAttestation = null)
	{
		string text = Compute(challengeId, nonce, mods, findings, assemblies, integrity, runtime, scannerMetrics, harmony, selfAttestation);
		string s = string.Join("|", new string[9]
		{
			"report-v2",
			protocolVersion.ToString(CultureInfo.InvariantCulture),
			clientVersion ?? "",
			scanStartedUtc.ToString(CultureInfo.InvariantCulture),
			scanCompletedUtc.ToString(CultureInfo.InvariantCulture),
			completedScanners.ToString(CultureInfo.InvariantCulture),
			challengeId ?? "",
			Convert.ToHexString(nonce ?? Array.Empty<byte>()),
			text
		});
		using SHA256 sHA = SHA256.Create();
		return Convert.ToHexString(sHA.ComputeHash(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
	}
}
