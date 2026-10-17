using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NewfiesAntiCheat.Shared;

namespace BlockLogger.AntiCheat;

public static class AntiCheatModDbFingerprintResolver
{
	private sealed class ReleaseCandidate
	{
		public long ReleaseId { get; set; }

		public string Version { get; set; } = "";

		public string FileName { get; set; } = "";

		public string DownloadUrl { get; set; } = "";
	}

	public const int DefaultReleaseFingerprints = 6;

	public const int MaxReleaseFingerprints = 20;

	private const long MaxDownloadBytes = 134217728L;

	private const long MaxEntryBytes = 67108864L;

	private const long MaxTotalUncompressedBytes = 268435456L;

	private const int MaxManifestEntries = 2048;

	private static readonly HttpClient Http = CreateHttpClient();

	private static HttpClient CreateHttpClient()
	{
		HttpClient httpClient = new HttpClient
		{
			Timeout = TimeSpan.FromSeconds(90L)
		};
		httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("NewfiesServerEssentialsSuite/1.2.44");
		return httpClient;
	}

	public static async Task<AntiCheatModDbFingerprintResolution> ResolveExactVersionAsync(string modId, string version, string targetGameVersion, CancellationToken cancellationToken = default(CancellationToken))
	{
		string normalized = NormalizeModId(modId);
		string requestedVersion = (version ?? "").Trim();
		if (normalized.Length == 0)
		{
			return Fail(modId, "A valid Mod DB identifier is required.");
		}
		if (requestedVersion.Length == 0)
		{
			return Fail(normalized, "The client did not report a version, so an exact Mod DB release cannot be verified.");
		}
		try
		{
			List<ReleaseCandidate> candidates = await TryResolveExactReleaseCandidatesFromV1Async(normalized, requestedVersion, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			if (candidates.Count == 0)
			{
				return Fail(normalized, "The Vintage Story Mod DB does not expose an exact '" + requestedVersion + "' release for this mod ID.");
			}
			List<AntiCheatModDbReleaseFingerprintResolution> releases = new List<AntiCheatModDbReleaseFingerprintResolution>();
			List<string> failures = new List<string>();
			foreach (ReleaseCandidate candidate in candidates.Take(8))
			{
				cancellationToken.ThrowIfCancellationRequested();
				try
				{
					AntiCheatModDbReleaseFingerprintResolution release = await FingerprintReleaseAsync(normalized, candidate, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
					if (!VersionEquivalent(release.Version, requestedVersion))
					{
						failures.Add(candidate.FileName + ": package declared '" + release.Version + "'");
					}
					else
					{
						releases.Add(release);
					}
				}
				catch (Exception ex)
				{
					failures.Add(string.Concat(str2: ex.Message, str0: (candidate.Version.Length > 0) ? candidate.Version : candidate.FileName, str1: ": "));
				}
			}
			if (releases.Count == 0)
			{
				return Fail(normalized, "Could not fingerprint any official upload for the exact Mod DB version" + ((failures.Count > 0) ? (": " + string.Join("; ", failures.Take(3))) : "."));
			}
			AntiCheatModDbReleaseFingerprintResolution newest = releases[0];
			AntiCheatModDbFingerprintResolution result = new AntiCheatModDbFingerprintResolution
			{
				Success = true,
				ModId = normalized,
				Version = newest.Version,
				FileName = newest.FileName,
				DownloadUrl = newest.DownloadUrl,
				PackageSha256 = newest.PackageSha256,
				ContentSha256 = newest.ContentSha256,
				AssemblyNames = (from x in releases.SelectMany((AntiCheatModDbReleaseFingerprintResolution x) => x.Assemblies)
					select x.AssemblyName into x
					where !string.IsNullOrWhiteSpace(x)
					select x).Distinct(StringComparer.OrdinalIgnoreCase).Take(32).ToList(),
				AssemblySha256s = (from x in releases.SelectMany((AntiCheatModDbReleaseFingerprintResolution x) => x.Assemblies)
					select x.Sha256).Where(IsSha256).Distinct(StringComparer.OrdinalIgnoreCase).Take(64)
					.ToList(),
				Releases = releases,
				Message = $"Resolved {releases.Count} official Mod DB upload{((releases.Count == 1) ? "" : "s")} for exact release {normalized} {requestedVersion}."
			};
			if (failures.Count > 0)
			{
				result.Message += $" {failures.Count} matching upload(s) could not be processed.";
			}
			return result;
		}
		catch (Exception ex2)
		{
			return Fail(normalized, "Could not resolve exact Mod DB release: " + ex2.GetType().Name + ": " + ex2.Message);
		}
	}

	public static async Task<AntiCheatModDbFingerprintResolution> ResolveAsync(string modId, string targetGameVersion, int releaseDepth = 6, CancellationToken cancellationToken = default(CancellationToken))
	{
		releaseDepth = Math.Clamp(releaseDepth, 1, 20);
		string normalized = NormalizeModId(modId);
		if (normalized.Length == 0)
		{
			return Fail(modId, "A valid Mod DB identifier is required.");
		}
		try
		{
			List<ReleaseCandidate> candidates = await TryResolveLatestReleaseCandidatesFromV1Async(normalized, releaseDepth, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			if (candidates.Count == 0)
			{
				string compatible = await TryResolveCompatibleDownloadUrlAsync(normalized, targetGameVersion, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				if (!string.IsNullOrWhiteSpace(compatible))
				{
					candidates.Add(new ReleaseCandidate
					{
						ReleaseId = 0L,
						DownloadUrl = compatible,
						FileName = FileNameFromUrl(compatible)
					});
				}
			}
			if (candidates.Count == 0)
			{
				return Fail(normalized, "The Vintage Story Mod DB did not expose a downloadable release for this mod ID.");
			}
			List<AntiCheatModDbReleaseFingerprintResolution> releases = new List<AntiCheatModDbReleaseFingerprintResolution>();
			List<string> failures = new List<string>();
			foreach (ReleaseCandidate candidate in candidates.Take(releaseDepth))
			{
				cancellationToken.ThrowIfCancellationRequested();
				try
				{
					List<AntiCheatModDbReleaseFingerprintResolution> list = releases;
					list.Add(await FingerprintReleaseAsync(normalized, candidate, cancellationToken).ConfigureAwait(continueOnCapturedContext: false));
				}
				catch (Exception ex)
				{
					failures.Add(string.Concat(str2: ex.Message, str0: (candidate.Version.Length > 0) ? candidate.Version : candidate.FileName, str1: ": "));
				}
			}
			if (releases.Count == 0)
			{
				return Fail(normalized, "Could not fingerprint any of the latest Mod DB releases" + ((failures.Count > 0) ? (": " + string.Join("; ", failures.Take(3))) : "."));
			}
			AntiCheatModDbReleaseFingerprintResolution newest = releases[0];
			AntiCheatModDbFingerprintResolution result = new AntiCheatModDbFingerprintResolution
			{
				Success = true,
				ModId = normalized,
				Version = newest.Version,
				FileName = newest.FileName,
				DownloadUrl = newest.DownloadUrl,
				PackageSha256 = newest.PackageSha256,
				ContentSha256 = newest.ContentSha256,
				AssemblyNames = (from x in releases.SelectMany((AntiCheatModDbReleaseFingerprintResolution x) => x.Assemblies)
					select x.AssemblyName into x
					where !string.IsNullOrWhiteSpace(x)
					select x).Distinct(StringComparer.OrdinalIgnoreCase).Take(32).ToList(),
				AssemblySha256s = (from x in releases.SelectMany((AntiCheatModDbReleaseFingerprintResolution x) => x.Assemblies)
					select x.Sha256).Where(IsSha256).Distinct(StringComparer.OrdinalIgnoreCase).Take(64)
					.ToList(),
				Releases = releases,
				Message = $"Resolved {releases.Count} release fingerprint{((releases.Count == 1) ? "" : "s")} for {normalized}: {string.Join(", ", releases.Select((AntiCheatModDbReleaseFingerprintResolution x) => string.IsNullOrWhiteSpace(x.Version) ? x.FileName : x.Version))}."
			};
			if (failures.Count > 0)
			{
				result.Message += $" {failures.Count} release(s) could not be processed.";
			}
			return result;
		}
		catch (Exception ex2)
		{
			return Fail(normalized, "Could not resolve Mod DB fingerprints: " + ex2.GetType().Name + ": " + ex2.Message);
		}
	}

	private static async Task<List<ReleaseCandidate>> TryResolveLatestReleaseCandidatesFromV1Async(string modId, int releaseDepth, CancellationToken ct)
	{
		string url = "https://mods.vintagestory.at/api/mod/" + Uri.EscapeDataString(modId);
		using HttpResponseMessage response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(continueOnCapturedContext: false);
		if (!response.IsSuccessStatusCode)
		{
			return new List<ReleaseCandidate>();
		}
		List<ReleaseCandidate> result;
		await using (Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(continueOnCapturedContext: false))
		{
			using JsonDocument doc = await JsonDocument.ParseAsync(stream, default, ct).ConfigureAwait(continueOnCapturedContext: false);
			if (!doc.RootElement.TryGetProperty("mod", out var mod) || mod.ValueKind != JsonValueKind.Object || !mod.TryGetProperty("releases", out var releases) || releases.ValueKind != JsonValueKind.Array)
			{
				result = new List<ReleaseCandidate>();
			}
			else
			{
				List<ReleaseCandidate> result2 = new List<ReleaseCandidate>();
				foreach (JsonElement item in releases.EnumerateArray())
				{
					if (item.ValueKind == JsonValueKind.Object)
					{
						string direct = GetString(item, "mainfile");
						string fileName = GetString(item, "filename");
						string version = GetString(item, "modversion");
						long releaseId = GetInt64(item, "releaseid");
						if (!string.IsNullOrWhiteSpace(direct) && LooksLikeZipUrl(direct))
						{
							result2.Add(new ReleaseCandidate
							{
								ReleaseId = releaseId,
								Version = version,
								FileName = fileName,
								DownloadUrl = direct
							});
						}
					}
				}
				result = result2.OrderByDescending((ReleaseCandidate x) => x.ReleaseId).ThenByDescending((ReleaseCandidate x) => x.Version, StringComparer.OrdinalIgnoreCase).Take(Math.Clamp(releaseDepth, 1, 20))
					.ToList();
			}
		}
		return result;
	}

	private static async Task<List<ReleaseCandidate>> TryResolveExactReleaseCandidatesFromV1Async(string modId, string requestedVersion, CancellationToken ct)
	{
		string url = "https://mods.vintagestory.at/api/mod/" + Uri.EscapeDataString(modId);
		using HttpResponseMessage response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(continueOnCapturedContext: false);
		if (!response.IsSuccessStatusCode)
		{
			return new List<ReleaseCandidate>();
		}
		List<ReleaseCandidate> result;
		await using (Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(continueOnCapturedContext: false))
		{
			using JsonDocument doc = await JsonDocument.ParseAsync(stream, default, ct).ConfigureAwait(continueOnCapturedContext: false);
			if (!doc.RootElement.TryGetProperty("mod", out var mod) || mod.ValueKind != JsonValueKind.Object || !mod.TryGetProperty("releases", out var releases) || releases.ValueKind != JsonValueKind.Array)
			{
				result = new List<ReleaseCandidate>();
			}
			else
			{
				List<ReleaseCandidate> result2 = new List<ReleaseCandidate>();
				foreach (JsonElement item in releases.EnumerateArray())
				{
					if (item.ValueKind != JsonValueKind.Object)
					{
						continue;
					}
					string version = GetString(item, "modversion");
					if (VersionEquivalent(version, requestedVersion))
					{
						string direct = GetString(item, "mainfile");
						if (!string.IsNullOrWhiteSpace(direct) && LooksLikeZipUrl(direct))
						{
							result2.Add(new ReleaseCandidate
							{
								ReleaseId = GetInt64(item, "releaseid"),
								Version = version,
								FileName = GetString(item, "filename"),
								DownloadUrl = direct
							});
						}
					}
				}
				result = result2.OrderByDescending((ReleaseCandidate x) => x.ReleaseId).Take(8).ToList();
			}
		}
		return result;
	}

	private static bool VersionEquivalent(string? left, string? right)
	{
		return string.Equals(NormalizeVersion(left), NormalizeVersion(right), StringComparison.OrdinalIgnoreCase);
		static string NormalizeVersion(string? value)
		{
			string text = (value ?? "").Trim();
			if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase) && text.Length > 1 && char.IsDigit(text[1]))
			{
				text = text.Substring(1);
			}
			return text.Trim();
		}
	}

	private static async Task<AntiCheatModDbReleaseFingerprintResolution> FingerprintReleaseAsync(string requestedModId, ReleaseCandidate candidate, CancellationToken ct)
	{
		Uri uri = MakeOfficialDownloadUri(candidate.DownloadUrl);
		byte[] bytes = await DownloadBoundedAsync(uri, ct).ConfigureAwait(continueOnCapturedContext: false);
		if (bytes.Length == 0)
		{
			throw new InvalidDataException("Release download was empty.");
		}
		string packageSha = Sha256(bytes);
		using MemoryStream memory = new MemoryStream(bytes, writable: false);
		using ZipArchive archive = new ZipArchive(memory, ZipArchiveMode.Read, leaveOpen: false);
		ZipArchiveEntry modInfoEntry = archive.Entries.FirstOrDefault((ZipArchiveEntry e) => string.Equals(NormalizeEntryPath(e.FullName), "modinfo.json", StringComparison.OrdinalIgnoreCase));
		if (modInfoEntry == null)
		{
			throw new InvalidDataException("Package does not contain a root modinfo.json.");
		}
		string reportedModId = "";
		string packageVersion = "";
		using (Stream info = modInfoEntry.Open())
		{
			using JsonDocument doc = JsonDocument.Parse(info);
			if (doc.RootElement.ValueKind == JsonValueKind.Object)
			{
				reportedModId = GetString(doc.RootElement, "modid").Trim();
				packageVersion = GetString(doc.RootElement, "version").Trim();
			}
		}
		if (!string.Equals(reportedModId, requestedModId, StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidDataException($"Package identity mismatch: requested '{requestedModId}', package declares '{reportedModId}'.");
		}
		List<AntiCheatModDbAssemblyFingerprintResolution> assemblyResults = new List<AntiCheatModDbAssemblyFingerprintResolution>();
		long assemblyBytes = 0L;
		foreach (ZipArchiveEntry entry in archive.Entries.Where((ZipArchiveEntry e) => !string.IsNullOrEmpty(e.Name) && e.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).OrderBy((ZipArchiveEntry e) => e.FullName, StringComparer.Ordinal))
		{
			if (entry.Length >= 0 && entry.Length <= 67108864)
			{
				assemblyBytes += entry.Length;
				if (assemblyBytes > 268435456)
				{
					break;
				}
				byte[] dll = ReadEntryBounded(entry);
				AntiCheatModDbAssemblyFingerprintResolution similarity = TryBuildAssemblySimilarity(dll, Path.GetFileNameWithoutExtension(entry.Name));
				similarity.Sha256 = Sha256(dll);
				assemblyResults.Add(similarity);
			}
		}
		AntiCheatModDbPackageManifestResolution manifest = ComputeZipContentManifest(archive);
		return new AntiCheatModDbReleaseFingerprintResolution
		{
			Version = (string.IsNullOrWhiteSpace(packageVersion) ? candidate.Version : packageVersion),
			FileName = (string.IsNullOrWhiteSpace(candidate.FileName) ? FileNameFromUrl(uri.ToString()) : candidate.FileName),
			DownloadUrl = uri.ToString(),
			PackageSha256 = packageSha,
			ContentSha256 = manifest.ContentSha256,
			PackageBytes = bytes.LongLength,
			DllCount = assemblyResults.Count,
			DllBytes = assemblyResults.Sum((AntiCheatModDbAssemblyFingerprintResolution x) => Math.Max(0L, x.FileSizeBytes)),
			FileCount = manifest.FileCount,
			ManifestComplete = manifest.ManifestComplete,
			Files = manifest.Files,
			Assemblies = assemblyResults.Take(32).ToList()
		};
	}

	private static AntiCheatModDbAssemblyFingerprintResolution TryBuildAssemblySimilarity(byte[]? dll, string fallbackName)
	{
		AntiCheatModDbAssemblyFingerprintResolution antiCheatModDbAssemblyFingerprintResolution = new AntiCheatModDbAssemblyFingerprintResolution
		{
			AssemblyName = fallbackName,
			FileSizeBytes = (dll?.LongLength ?? 0)
		};
		if (dll == null || dll.Length <= 0)
		{
			return antiCheatModDbAssemblyFingerprintResolution;
		}
		try
		{
			using MemoryStream peStream = new MemoryStream(dll, writable: false);
			using PEReader pEReader = new PEReader(peStream, PEStreamOptions.LeaveOpen);
			if (!pEReader.HasMetadata)
			{
				return antiCheatModDbAssemblyFingerprintResolution;
			}
			MetadataReader metadataReader = pEReader.GetMetadataReader();
			if (metadataReader.IsAssembly)
			{
				string text = metadataReader.GetString(metadataReader.GetAssemblyDefinition().Name);
				if (!string.IsNullOrWhiteSpace(text))
				{
					antiCheatModDbAssemblyFingerprintResolution.AssemblyName = text;
				}
			}
			HashSet<string> hashSet = new HashSet<string>(StringComparer.Ordinal);
			List<string> list = new List<string>();
			foreach (AssemblyReferenceHandle assemblyReference in metadataReader.AssemblyReferences)
			{
				string text2 = metadataReader.GetString(metadataReader.GetAssemblyReference(assemblyReference).Name);
				if (!string.IsNullOrWhiteSpace(text2))
				{
					hashSet.Add("R:" + text2);
				}
			}
			foreach (TypeDefinitionHandle typeDefinition2 in metadataReader.TypeDefinitions)
			{
				TypeDefinition typeDefinition = metadataReader.GetTypeDefinition(typeDefinition2);
				string text3 = metadataReader.GetString(typeDefinition.Name);
				if (string.IsNullOrWhiteSpace(text3) || text3.StartsWith("<", StringComparison.Ordinal))
				{
					continue;
				}
				string text4 = metadataReader.GetString(typeDefinition.Namespace);
				string text5 = (string.IsNullOrWhiteSpace(text4) ? text3 : (text4 + "." + text3));
				hashSet.Add("T:" + text5);
				foreach (MethodDefinitionHandle method in typeDefinition.GetMethods())
				{
					MethodDefinition methodDefinition = metadataReader.GetMethodDefinition(method);
					string text6 = metadataReader.GetString(methodDefinition.Name);
					if (string.IsNullOrWhiteSpace(text6) || text6.StartsWith("<", StringComparison.Ordinal))
					{
						continue;
					}
					int num = 0;
					foreach (ParameterHandle parameter in methodDefinition.GetParameters())
					{
						if (metadataReader.GetParameter(parameter).SequenceNumber > 0)
						{
							num++;
						}
					}
					hashSet.Add("M:" + text5 + ":" + text6 + "/" + num);
					if (methodDefinition.RelativeVirtualAddress == 0 || list.Count >= 4096)
					{
						continue;
					}
					try
					{
						MethodBodyBlock methodBody = pEReader.GetMethodBody(methodDefinition.RelativeVirtualAddress);
						byte[] iLBytes = methodBody.GetILBytes();
						string text7 = IlBehaviorFingerprint.BuildMethodToken(iLBytes, num, methodBody.ExceptionRegions.Length);
						if (text7.Length > 0)
						{
							list.Add(text7);
						}
					}
					catch
					{
					}
				}
				foreach (FieldDefinitionHandle field in typeDefinition.GetFields())
				{
					string text8 = metadataReader.GetString(metadataReader.GetFieldDefinition(field).Name);
					if (!string.IsNullOrWhiteSpace(text8) && !text8.StartsWith("<", StringComparison.Ordinal))
					{
						hashSet.Add("F:" + text5 + ":" + text8);
					}
				}
				if (hashSet.Count < 4096)
				{
					continue;
				}
				break;
			}
			List<string> list2 = hashSet.OrderBy((string x) => x, StringComparer.Ordinal).Take(4096).ToList();
			antiCheatModDbAssemblyFingerprintResolution.StructureTokenCount = list2.Count;
			antiCheatModDbAssemblyFingerprintResolution.SimilarityMinHash = StructuralSimilarity.ComputeMinHash(list2);
			List<string> list3 = list.OrderBy((string x) => x, StringComparer.Ordinal).Take(4096).ToList();
			antiCheatModDbAssemblyFingerprintResolution.BehaviorTokenCount = list3.Count;
			if (list3.Count > 0)
			{
				antiCheatModDbAssemblyFingerprintResolution.BehaviorSha256 = Sha256(Encoding.UTF8.GetBytes(string.Join("\n", list3)));
				antiCheatModDbAssemblyFingerprintResolution.BehaviorMinHash = StructuralSimilarity.ComputeMinHash(list3);
			}
		}
		catch
		{
		}
		return antiCheatModDbAssemblyFingerprintResolution;
	}

	private static async Task<string?> TryResolveCompatibleDownloadUrlAsync(string modId, string targetGameVersion, CancellationToken ct)
	{
		string url = "https://mods.vintagestory.at/api/v2/mods/install-information?ids=" + Uri.EscapeDataString(modId) + "&gv=" + Uri.EscapeDataString(targetGameVersion);
		using HttpResponseMessage response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(continueOnCapturedContext: false);
		if (!response.IsSuccessStatusCode)
		{
			return null;
		}
		string result;
		await using (Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(continueOnCapturedContext: false))
		{
			using JsonDocument doc = await JsonDocument.ParseAsync(stream, default, ct).ConfigureAwait(continueOnCapturedContext: false);
			if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
			{
				result = null;
			}
			else
			{
				foreach (JsonProperty prop in data.EnumerateObject())
				{
					if (!string.Equals(prop.Name, modId, StringComparison.OrdinalIgnoreCase) || prop.Value.ValueKind != JsonValueKind.Object || !TryGetString(prop.Value, "fileUrl", out string fileUrl) || !LooksLikeZipUrl(fileUrl))
					{
						fileUrl = null;
						continue;
					}
					result = fileUrl;
					goto end_IL_0267;
				}
				result = null;
			}
			end_IL_0267:;
		}
		return result;
	}

	private static async Task<byte[]> DownloadBoundedAsync(Uri uri, CancellationToken ct)
	{
		using HttpResponseMessage response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(continueOnCapturedContext: false);
		response.EnsureSuccessStatusCode();
		long? contentLength = response.Content.Headers.ContentLength;
		if (contentLength.HasValue && contentLength.Value > 134217728)
		{
			throw new InvalidDataException($"Release exceeds the {128L} MiB fingerprint download limit.");
		}
		byte[] result;
		await using (Stream input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(continueOnCapturedContext: false))
		{
			int capacity = (int)((contentLength.HasValue && contentLength.Value > 0 && contentLength.Value <= int.MaxValue) ? contentLength.Value : 0);
			using MemoryStream output = new MemoryStream(capacity);
			byte[] buffer = new byte[65536];
			long total = 0L;
			while (true)
			{
				int read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(continueOnCapturedContext: false);
				if (read <= 0)
				{
					break;
				}
				total += read;
				if (total > 134217728)
				{
					throw new InvalidDataException($"Release exceeds the {128L} MiB fingerprint download limit.");
				}
				await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(continueOnCapturedContext: false);
			}
			result = output.ToArray();
		}
		return result;
	}

	private static byte[] ReadEntryBounded(ZipArchiveEntry entry)
	{
		if (entry.Length < 0 || entry.Length > 67108864)
		{
			throw new InvalidDataException("Assembly entry exceeds the fingerprint safety limit.");
		}
		using Stream stream = entry.Open();
		using MemoryStream memoryStream = new MemoryStream((int)((entry.Length <= int.MaxValue) ? entry.Length : 0));
		stream.CopyTo(memoryStream);
		if (memoryStream.Length > 67108864)
		{
			throw new InvalidDataException("Assembly entry exceeds the fingerprint safety limit.");
		}
		return memoryStream.ToArray();
	}

	private static Uri MakeOfficialDownloadUri(string value)
	{
		Uri uri = (Uri.TryCreate(value, UriKind.Absolute, out Uri result) ? result : new Uri(new Uri("https://mods.vintagestory.at/"), value.TrimStart('/')));
		if (!uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) || (!uri.Host.Equals("mods.vintagestory.at", StringComparison.OrdinalIgnoreCase) && !uri.Host.EndsWith(".vintagestory.at", StringComparison.OrdinalIgnoreCase)))
		{
			throw new InvalidDataException("Mod DB returned a download outside an approved Vintage Story HTTPS host.");
		}
		if (!LooksLikeZipUrl(uri.ToString()))
		{
			throw new InvalidDataException("Mod DB release is not a ZIP package.");
		}
		return uri;
	}

	public static AntiCheatModDbPackageManifestResolution FingerprintLocalPackageManifest(string zipPath)
	{
		if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath))
		{
			throw new FileNotFoundException("Evidence package was not found.", zipPath);
		}
		FileInfo fileInfo = new FileInfo(zipPath);
		if (fileInfo.Length <= 0 || fileInfo.Length > 134217728)
		{
			throw new InvalidDataException($"Package exceeds the {128L} MiB fingerprint limit.");
		}
		using FileStream stream = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
		using ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
		return ComputeZipContentManifest(archive);
	}

	private static AntiCheatModDbPackageManifestResolution ComputeZipContentManifest(ZipArchive archive)
	{
		StringBuilder stringBuilder = new StringBuilder();
		List<AntiCheatModDbFileFingerprintResolution> list = new List<AntiCheatModDbFileFingerprintResolution>();
		long num = 0L;
		int num2 = 0;
		bool flag = true;
		HashSet<string> hashSet = new HashSet<string>(StringComparer.Ordinal);
		foreach (ZipArchiveEntry item in archive.Entries.Where((ZipArchiveEntry e) => !string.IsNullOrEmpty(e.Name)).OrderBy((ZipArchiveEntry e) => e.FullName, StringComparer.Ordinal))
		{
			if (item.Length < 0 || item.Length > 67108864)
			{
				throw new InvalidDataException("Archive entry '" + item.FullName + "' exceeds the fingerprint safety limit.");
			}
			num += item.Length;
			if (num > 268435456)
			{
				throw new InvalidDataException("Archive exceeds the uncompressed fingerprint safety limit.");
			}
			string text = NormalizeEntryPath(item.FullName);
			using Stream stream = item.Open();
			string text2 = Sha256(stream);
			if (!string.Equals(text, "modinfo.json", StringComparison.OrdinalIgnoreCase))
			{
				stringBuilder.Append(text).Append('|').Append(item.Length)
					.Append('|')
					.Append(text2)
					.Append('\n');
			}
			num2++;
			bool flag2 = hashSet.Add(text);
			if ((list.Count < 2048) & flag2)
			{
				list.Add(new AntiCheatModDbFileFingerprintResolution
				{
					Path = text,
					Sha256 = text2,
					Bytes = item.Length
				});
			}
			else
			{
				flag = false;
			}
		}
		return new AntiCheatModDbPackageManifestResolution
		{
			ContentSha256 = Sha256(Encoding.UTF8.GetBytes(stringBuilder.ToString())),
			FileCount = num2,
			ManifestComplete = (flag && list.Count == num2),
			Files = list
		};
	}

	private static string Sha256(byte[] data)
	{
		return Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
	}

	private static string Sha256(Stream stream)
	{
		using SHA256 sHA = SHA256.Create();
		return Convert.ToHexString(sHA.ComputeHash(stream)).ToLowerInvariant();
	}

	private static string NormalizeEntryPath(string value)
	{
		return (value ?? "").Replace('\\', '/').TrimStart('/');
	}

	private static string FileNameFromUrl(string value)
	{
		if (!Uri.TryCreate(value, UriKind.Absolute, out Uri result))
		{
			return Path.GetFileName(value);
		}
		string text = Uri.UnescapeDataString(result.Query ?? "");
		int num = text.IndexOf("dl=", StringComparison.OrdinalIgnoreCase);
		if (num >= 0)
		{
			return Uri.UnescapeDataString(text.Substring(num + 3).TrimStart(new char[2] { '?', '&' }));
		}
		return Path.GetFileName(Uri.UnescapeDataString(result.AbsolutePath));
	}

	private static bool LooksLikeZipUrl(string? value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return false;
		}
		return value.Contains(".zip", StringComparison.OrdinalIgnoreCase);
	}

	private static string GetString(JsonElement obj, string name)
	{
		string value;
		return TryGetString(obj, name, out value) ? value : "";
	}

	private static long GetInt64(JsonElement obj, string name)
	{
		foreach (JsonProperty item in obj.EnumerateObject())
		{
			if (item.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && item.Value.TryGetInt64(out var value))
			{
				return value;
			}
		}
		return 0L;
	}

	private static bool TryGetString(JsonElement obj, string name, out string value)
	{
		value = "";
		foreach (JsonProperty item in obj.EnumerateObject())
		{
			if (item.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && item.Value.ValueKind == JsonValueKind.String)
			{
				value = item.Value.GetString() ?? "";
				return true;
			}
		}
		return false;
	}

	private static string NormalizeModId(string? value)
	{
		string text = (value ?? "").Trim().ToLowerInvariant();
		int length = text.Length;
		if ((length < 1 || length > 64) ? true : false)
		{
			return "";
		}
		string text2 = text;
		foreach (char c in text2)
		{
			bool flag = char.IsLetterOrDigit(c);
			bool flag2 = flag;
			if (!flag2)
			{
				bool flag3 = ((c == '-' || c == '.' || c == '_') ? true : false);
				flag2 = flag3;
			}
			if (!flag2)
			{
				return "";
			}
		}
		return text;
	}

	private static bool IsSha256(string? value)
	{
		return value != null && value.Length == 64 && value.All(Uri.IsHexDigit);
	}

	private static AntiCheatModDbFingerprintResolution Fail(string modId, string message)
	{
		return new AntiCheatModDbFingerprintResolution
		{
			Success = false,
			ModId = NormalizeModId(modId),
			Message = message
		};
	}
}
