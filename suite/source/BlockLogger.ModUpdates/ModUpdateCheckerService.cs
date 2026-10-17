using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BlockLogger.Services;
using ProperVersion;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace BlockLogger.ModUpdates;

public sealed class ModUpdateCheckerService : IDisposable
{
	private sealed class ModDbBatchResult
	{
		public bool Success { get; set; }

		public string Error { get; set; } = "";

		public Dictionary<string, JsonElement> Data { get; set; } = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
	}

	private readonly ICoreServerAPI api;

	private readonly string targetGameVersion;

	private readonly HttpClient http = new HttpClient
	{
		Timeout = TimeSpan.FromSeconds(15L)
	};

	private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);

	private readonly object sync = new object();

	private ModUpdateSnapshot snapshot = new ModUpdateSnapshot();

	private long tickListenerId = -1L;

	private bool disposed;

	private List<InstalledModInfo> installedMods = new List<InstalledModInfo>();

	private readonly Dictionary<string, string> stagedUpdates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

	private Dictionary<string, string> loadedModVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

	private readonly Dictionary<string, string> stagedVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

	private readonly Dictionary<string, Dictionary<string, string>> loadedDependencies = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

	public ModUpdateCheckerService(ICoreServerAPI api, string targetGameVersion)
	{
		this.api = api;
		this.targetGameVersion = targetGameVersion;
		http.DefaultRequestHeaders.UserAgent.ParseAdd("NewfiesServerEssentialsSuite/1.2.44");
	}

	public void Start()
	{
		installedMods = (from g in (from x in api.ModLoader.Mods
				where x?.Info != null && !x.Info.CoreMod
				select new InstalledModInfo
				{
					Id = (x.Info.ModID ?? "").Trim(),
					Version = (x.Info.Version ?? "").Trim(),
					SourcePath = (x.SourcePath ?? "").Trim(),
					FileName = (x.FileName ?? "").Trim()
				} into x
				where x.Id.Length > 0 && x.Version.Length > 0 && !string.Equals(x.Id, "game", StringComparison.OrdinalIgnoreCase)
				select x).GroupBy((InstalledModInfo x) => x.Id, StringComparer.OrdinalIgnoreCase)
			select g.First()).OrderBy((InstalledModInfo x) => x.Id, StringComparer.OrdinalIgnoreCase).ToList();
		loadedModVersions = api.ModLoader.Mods.Where((Mod x) => x?.Info != null && !string.IsNullOrWhiteSpace(x.Info.ModID) && api.ModLoader.IsModEnabled(x.Info.ModID)).GroupBy((Mod x) => x.Info.ModID, StringComparer.OrdinalIgnoreCase).ToDictionary((IGrouping<string, Mod> g) => g.Key, (IGrouping<string, Mod> g) => g.First().Info.Version ?? "", StringComparer.OrdinalIgnoreCase);
		loadedModVersions["game"] = targetGameVersion;
		foreach (Mod item in api.ModLoader.Mods.Where((Mod x) => x?.Info != null && loadedModVersions.ContainsKey(x.Info.ModID ?? "")))
		{
			loadedDependencies[item.Info.ModID] = (item.Info.Dependencies ?? Array.Empty<ModDependency>()).GroupBy((ModDependency d) => d.ModID, StringComparer.OrdinalIgnoreCase).ToDictionary((IGrouping<string, ModDependency> g) => g.Key, (IGrouping<string, ModDependency> g) => g.First().Version ?? "", StringComparer.OrdinalIgnoreCase);
		}
		lock (sync)
		{
			snapshot.TargetGameVersion = targetGameVersion;
		}
		api.Event.RegisterCallback((float dt) =>
		{
			CheckAsync(force: false);
		}, 60000);
		tickListenerId = api.Event.RegisterGameTickListener((float dt) =>
		{
			CheckAsync(force: false);
		}, 21600000);
	}

	public ModUpdateSnapshot GetSnapshot()
	{
		lock (sync)
		{
			return new ModUpdateSnapshot
			{
				CheckedUnixMilliseconds = snapshot.CheckedUnixMilliseconds,
				TargetGameVersion = snapshot.TargetGameVersion,
				CheckInProgress = snapshot.CheckInProgress,
				LastError = snapshot.LastError,
				LastWarning = snapshot.LastWarning,
				LookupFailedCount = snapshot.LookupFailedCount,
				UpdateCount = snapshot.UpdateCount,
				CurrentCount = snapshot.CurrentCount,
				UnknownCount = snapshot.UnknownCount,
				RestartRequired = snapshot.RestartRequired,
				StagedUpdates = snapshot.StagedUpdates.ToList(),
				Mods = snapshot.Mods.Select((ModUpdateEntry x) => new ModUpdateEntry
				{
					ModId = x.ModId,
					InstalledVersion = x.InstalledVersion,
					RecommendedVersion = x.RecommendedVersion,
					Status = x.Status,
					Detail = x.Detail,
					ModPageUrl = x.ModPageUrl,
					AutoUpdateSupported = x.AutoUpdateSupported,
					AutoUpdateReason = x.AutoUpdateReason
				}).ToList()
			};
		}
	}

	public async Task<ModUpdateSnapshot> CheckAsync(bool force)
	{
		if (disposed)
		{
			return GetSnapshot();
		}
		if (!force)
		{
			ModUpdateSnapshot current = GetSnapshot();
			if (current.CheckedUnixMilliseconds > 0 && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - current.CheckedUnixMilliseconds < 300000)
			{
				return current;
			}
		}
		if (!(await gate.WaitAsync(0).ConfigureAwait(continueOnCapturedContext: false)))
		{
			return GetSnapshot();
		}
		try
		{
			lock (sync)
			{
				snapshot.CheckInProgress = true;
				snapshot.LastError = "";
				snapshot.LastWarning = "";
				snapshot.LookupFailedCount = 0;
			}
			List<InstalledModInfo> installed = installedMods.ToList();
			Dictionary<string, ModUpdateEntry> results = installed.ToDictionary((InstalledModInfo x) => x.Id, (InstalledModInfo x) => CreateEntry(x, "Unknown", "Not checked."), StringComparer.OrdinalIgnoreCase);
			int lookupFailures = 0;
			Dictionary<string, ModUpdateEntry> prior = GetSnapshot().Mods.ToDictionary((ModUpdateEntry x) => x.ModId, (ModUpdateEntry x) => x, StringComparer.OrdinalIgnoreCase);
			foreach (InstalledModInfo[] batch in installed.Chunk(20))
			{
				List<InstalledModInfo> batchItems = batch.ToList();
				ModDbBatchResult lookup = await FetchInstallInformationAsync(batchItems, includeInstalledVersions: true).ConfigureAwait(continueOnCapturedContext: false);
				if (!lookup.Success)
				{
					lookup = await FetchInstallInformationAsync(batchItems, includeInstalledVersions: false).ConfigureAwait(continueOnCapturedContext: false);
				}
				foreach (InstalledModInfo item in batchItems)
				{
					ModUpdateEntry entry = results[item.Id];
					if (!TryGetInfo(lookup.Data, item.Id, out var info))
					{
						ModDbBatchResult single = await FetchInstallInformationAsync(new InstalledModInfo[1] { item }, includeInstalledVersions: true).ConfigureAwait(continueOnCapturedContext: false);
						if (!TryGetInfo(single.Data, item.Id, out info))
						{
							single = await FetchInstallInformationAsync(new InstalledModInfo[1] { item }, includeInstalledVersions: false).ConfigureAwait(continueOnCapturedContext: false);
						}
						if (!TryGetInfo(single.Data, item.Id, out info))
						{
							lookupFailures++;
							bool flag = prior.TryGetValue(item.Id, out var cached);
							bool flag2 = flag;
							if (flag2)
							{
								bool flag3;
								switch (cached.Status)
								{
								case "Current":
								case "Update":
								case "Staged":
									flag3 = true;
									break;
								default:
									flag3 = false;
									break;
								}
								flag2 = flag3;
							}
							if (flag2)
							{
								entry = CloneEntry(cached);
								entry.Detail = "Cached result retained; latest Mod DB lookup failed. " + ((single.Error.Length > 0) ? single.Error : lookup.Error);
								results[item.Id] = entry;
							}
							else
							{
								entry.Status = "Error";
								ModUpdateEntry modUpdateEntry = entry;
								string detail;
								if (single.Error.Length > 0)
								{
									detail = single.Error;
								}
								else
								{
									detail = ((lookup.Error.Length > 0) ? lookup.Error : "The Mod DB did not return this mod identifier.");
								}
								modUpdateEntry.Detail = detail;
							}
							continue;
						}
					}
					ApplyInstallInfo(item, entry, info);
					if (item.Id.Equals("newfiesserveressentialssuite", StringComparison.OrdinalIgnoreCase) && info.TryGetProperty("errorCode", out var missingIndex) && missingIndex.TryGetInt32(out var code) && code == 4041)
					{
						await DescribeKnownSuiteListingAsync(entry).ConfigureAwait(continueOnCapturedContext: false);
					}
					if (stagedUpdates.TryGetValue(item.Id, out string stagedDescription))
					{
						entry.Status = "Staged";
						entry.Detail = stagedDescription + ". Server restart required.";
					}
					info = default;
					missingIndex = default;
					stagedDescription = null;
				}
			}
			List<ModUpdateEntry> list = results.Values.OrderByDescending((ModUpdateEntry x) => x.Status == "Update").ThenBy((ModUpdateEntry x) => x.ModId, StringComparer.OrdinalIgnoreCase).ToList();
			lock (sync)
			{
				snapshot.Mods = list;
				snapshot.CheckedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
				snapshot.UpdateCount = list.Count((ModUpdateEntry x) => x.Status == "Update");
				snapshot.CurrentCount = list.Count((ModUpdateEntry x) => x.Status == "Current");
				snapshot.UnknownCount = list.Count((ModUpdateEntry x) => x.Status != "Update" && x.Status != "Current" && x.Status != "Staged");
				snapshot.RestartRequired = stagedUpdates.Count > 0;
				snapshot.StagedUpdates = stagedUpdates.Values.OrderBy((string x) => x, StringComparer.OrdinalIgnoreCase).ToList();
				snapshot.CheckInProgress = false;
				snapshot.LookupFailedCount = lookupFailures;
				snapshot.LastError = ((list.Count > 0 && list.All((ModUpdateEntry x) => x.Status == "Error")) ? "All Mod DB requests failed after retries." : "");
				snapshot.LastWarning = ((lookupFailures > 0 && string.IsNullOrWhiteSpace(snapshot.LastError)) ? $"{lookupFailures} mod lookup(s) could not be refreshed; cached/partial results were retained." : "");
			}
			return GetSnapshot();
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			lock (sync)
			{
				snapshot.CheckInProgress = false;
				snapshot.LastError = ex2.GetType().Name + ": " + ex2.Message;
				snapshot.CheckedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
			}
			api.Logger.Warning("[ServerEssentials/ModUpdates] Update check failed: {0}: {1}", ex2.GetType().Name, ex2.Message);
			return GetSnapshot();
		}
		finally
		{
			gate.Release();
		}
	}

	public async Task<ModUpdateApplyResult> ApplyUpdateAsync(string modId)
	{
		modId = (modId ?? "").Trim();
		ModUpdateApplyResult result = new ModUpdateApplyResult
		{
			ModId = modId,
			ModPageUrl = BuildModPageUrl(modId)
		};
		if (disposed)
		{
			result.Message = "Mod update checker is shutting down.";
			return result;
		}
		if (modId.Length == 0)
		{
			result.Message = "Mod ID is required.";
			return result;
		}
		InstalledModInfo installed = installedMods.FirstOrDefault((InstalledModInfo x) => string.Equals(x.Id, modId, StringComparison.OrdinalIgnoreCase));
		if (installed == null)
		{
			result.Message = "That mod is not a loaded third-party mod.";
			return result;
		}
		result.InstalledVersion = installed.Version;
		if (stagedUpdates.TryGetValue(installed.Id, out string alreadyStaged))
		{
			result.RestartRequired = true;
			result.Message = alreadyStaged + ". This update is already staged; restart the server to load it.";
			return result;
		}
		if (!CanAutoUpdate(installed, out string reason))
		{
			result.Message = reason + " Open the Mod DB page for a manual update.";
			return result;
		}
		if (!(await gate.WaitAsync(0).ConfigureAwait(continueOnCapturedContext: false)))
		{
			result.Message = "Another Mod DB check or update operation is already running.";
			return result;
		}
		string stagingRoot = Path.Combine(SuiteDataPaths.GetSuiteRoot(api), "mod-update-staging");
		string backupRoot = Path.Combine(SuiteDataPaths.GetSuiteRoot(api), "mod-update-backups");
		Directory.CreateDirectory(stagingRoot);
		Directory.CreateDirectory(backupRoot);
		string downloadedPath = null;
		string sameDirTemp = null;
		try
		{
			(bool Success, string Version, string FileName, string FileUrl, string Error) selected = await GetRecommendedDownloadAsync(installed.Id, installed.Version).ConfigureAwait(continueOnCapturedContext: false);
			if (!selected.Success)
			{
				result.Message = selected.Error;
				return result;
			}
			if (string.Equals(selected.Version, installed.Version, StringComparison.OrdinalIgnoreCase))
			{
				result.Message = $"{installed.Id} {installed.Version} is already current for Vintage Story {targetGameVersion}.";
				return result;
			}
			result.NewVersion = selected.Version;
			string safeDownloadName = Path.GetFileName(selected.FileName);
			if (string.IsNullOrWhiteSpace(safeDownloadName) || !safeDownloadName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
			{
				result.Message = "The recommended Mod DB release is not a ZIP package, so NSE will not replace it automatically.";
				return result;
			}
			downloadedPath = Path.Combine(stagingRoot, $"{SanitizeFilePart(installed.Id)}-{Guid.NewGuid():N}.zip");
			await DownloadWithLimitAsync(selected.FileUrl, downloadedPath, 268435456L).ConfigureAwait(continueOnCapturedContext: false);
			(bool Success, string Error, Dictionary<string, string> Dependencies) validation = ValidateDownloadedMod(downloadedPath, installed.Id, selected.Version);
			if (!validation.Success)
			{
				result.Message = "Downloaded package validation failed: " + validation.Error;
				return result;
			}
			if (disposed)
			{
				result.Message = "Server is shutting down; no update was staged.";
				return result;
			}
			Dictionary<string, string> availableVersions = new Dictionary<string, string>(loadedModVersions, StringComparer.OrdinalIgnoreCase);
			foreach (KeyValuePair<string, string> staged in stagedVersions)
			{
				availableVersions[staged.Key] = staged.Value;
			}
			availableVersions[installed.Id] = selected.Version;
			List<string> dependencyProblems = ValidateDependencyVersions(validation.Dependencies, availableVersions);
			foreach (KeyValuePair<string, Dictionary<string, string>> dependent in loadedDependencies.Where((KeyValuePair<string, Dictionary<string, string>> d) => !d.Key.Equals(installed.Id, StringComparison.OrdinalIgnoreCase) && d.Value.ContainsKey(installed.Id)))
			{
				dependencyProblems.AddRange(from problem in ValidateDependencyVersions(new Dictionary<string, string> { [installed.Id] = dependent.Value[installed.Id] }, availableVersions)
					select dependent.Key + ": " + problem);
			}
			if (dependencyProblems.Count > 0)
			{
				result.Message = "Update dependency checks failed: " + string.Join("; ", dependencyProblems) + ". Update the required dependencies first.";
				return result;
			}
			string oldPath = Path.GetFullPath(installed.SourcePath);
			string modsDirectory = Path.GetDirectoryName(oldPath) ?? "";
			string targetPath = Path.Combine(modsDirectory, safeDownloadName);
			targetPath = Path.GetFullPath(targetPath);
			if (!string.Equals(Path.GetDirectoryName(targetPath), modsDirectory, StringComparison.OrdinalIgnoreCase))
			{
				result.Message = "Refusing an unsafe Mod DB filename.";
				return result;
			}
			if (!string.Equals(targetPath, oldPath, StringComparison.OrdinalIgnoreCase) && File.Exists(targetPath))
			{
				result.Message = "A file named " + safeDownloadName + " already exists in the Mods folder. Resolve that duplicate manually before updating.";
				return result;
			}
			string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
			string backupDir = Path.Combine(backupRoot, stamp + "-" + SanitizeFilePart(installed.Id));
			Directory.CreateDirectory(backupDir);
			string backupPath = Path.Combine(backupDir, Path.GetFileName(oldPath));
			File.Copy(oldPath, backupPath, overwrite: false);
			result.BackupPath = backupPath;
			sameDirTemp = Path.Combine(modsDirectory, ".nse-update-" + Guid.NewGuid().ToString("N") + ".tmp");
			File.Copy(downloadedPath, sameDirTemp, overwrite: true);
			try
			{
				if (string.Equals(targetPath, oldPath, StringComparison.OrdinalIgnoreCase))
				{
					File.Move(sameDirTemp, oldPath, overwrite: true);
					sameDirTemp = null;
				}
				else
				{
					File.Move(sameDirTemp, targetPath, overwrite: false);
					sameDirTemp = null;
					try
					{
						File.Delete(oldPath);
					}
					catch
					{
						try
						{
							File.Delete(targetPath);
						}
						catch
						{
						}
						throw;
					}
				}
			}
			catch
			{
				if (!File.Exists(oldPath) && File.Exists(backupPath))
				{
					try
					{
						File.Copy(backupPath, oldPath, overwrite: false);
					}
					catch
					{
					}
				}
				throw;
			}
			result.Success = true;
			result.RestartRequired = true;
			result.InstalledPath = targetPath;
			result.Message = $"Staged {installed.Id} {installed.Version} → {selected.Version}. Restart the Vintage Story server to load the new version.";
			stagedVersions[installed.Id] = selected.Version;
			loadedDependencies[installed.Id] = validation.Dependencies;
			stagedUpdates[installed.Id] = $"{installed.Id} {installed.Version} → {selected.Version}";
			lock (sync)
			{
				snapshot.RestartRequired = true;
				snapshot.StagedUpdates = stagedUpdates.Values.OrderBy((string x) => x, StringComparer.OrdinalIgnoreCase).ToList();
				ModUpdateEntry existing = snapshot.Mods.FirstOrDefault((ModUpdateEntry x) => string.Equals(x.ModId, installed.Id, StringComparison.OrdinalIgnoreCase));
				if (existing != null)
				{
					existing.Status = "Staged";
					existing.Detail = $"Update {installed.Version} → {selected.Version} staged on disk. Server restart required.";
					existing.RecommendedVersion = selected.Version;
				}
				snapshot.UpdateCount = snapshot.Mods.Count((ModUpdateEntry x) => x.Status == "Update");
				snapshot.CurrentCount = snapshot.Mods.Count((ModUpdateEntry x) => x.Status == "Current");
				snapshot.UnknownCount = snapshot.Mods.Count((ModUpdateEntry x) => x.Status != "Update" && x.Status != "Current" && x.Status != "Staged");
			}
			api.Logger.Notification("[ServerEssentials/ModUpdates] Staged mod update {0} {1} -> {2}. Backup: {3}", installed.Id, installed.Version, selected.Version, backupPath);
			return result;
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			result.Message = ex2.GetType().Name + ": " + ex2.Message;
			api.Logger.Warning("[ServerEssentials/ModUpdates] Failed to stage update for {0}: {1}: {2}", modId, ex2.GetType().Name, ex2.Message);
			return result;
		}
		finally
		{
			if (sameDirTemp != null)
			{
				try
				{
					File.Delete(sameDirTemp);
				}
				catch
				{
				}
			}
			if (downloadedPath != null)
			{
				try
				{
					File.Delete(downloadedPath);
				}
				catch
				{
				}
			}
			gate.Release();
		}
	}

	private ModUpdateEntry CreateEntry(InstalledModInfo mod, string status, string detail)
	{
		bool flag = CanAutoUpdate(mod, out string reason);
		return new ModUpdateEntry
		{
			ModId = mod.Id,
			InstalledVersion = mod.Version,
			Status = status,
			Detail = detail,
			ModPageUrl = BuildModPageUrl(mod.Id),
			AutoUpdateSupported = flag,
			AutoUpdateReason = ((!flag) ? reason : (string.Equals(mod.Id, "newfiesserveressentialssuite", StringComparison.OrdinalIgnoreCase) ? "Suite self-update is supported when the installed Suite is a ZIP and the Mod DB has a published compatible release; restart loads the new build." : "ZIP package can be safely staged with backup and restart."))
		};
	}

	private static string BuildModPageUrl(string modId)
	{
		return "https://mods.vintagestory.at/" + Uri.EscapeDataString((modId ?? "").Trim());
	}

	private static bool CanAutoUpdate(InstalledModInfo mod, out string reason)
	{
		if (string.IsNullOrWhiteSpace(mod.SourcePath))
		{
			reason = "Vintage Story did not expose a source package path for this mod.";
			return false;
		}
		if (!File.Exists(mod.SourcePath))
		{
			reason = "The loaded mod source is not a regular file (folder/dev mods are manual-update only).";
			return false;
		}
		if (!mod.SourcePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
		{
			reason = "Only loaded ZIP packages are eligible for automatic replacement.";
			return false;
		}
		reason = "";
		return true;
	}

	private async Task<(bool Success, string Version, string FileName, string FileUrl, string Error)> GetRecommendedDownloadAsync(string modId, string installedVersion)
	{
		InstalledModInfo probe = new InstalledModInfo
		{
			Id = modId,
			Version = installedVersion
		};
		ModDbBatchResult lookup = await FetchInstallInformationAsync(new InstalledModInfo[1] { probe }, includeInstalledVersions: false).ConfigureAwait(continueOnCapturedContext: false);
		if (!TryGetInfo(lookup.Data, modId, out var info))
		{
			ModDbBatchResult versioned = await FetchInstallInformationAsync(new InstalledModInfo[1] { probe }, includeInstalledVersions: true).ConfigureAwait(continueOnCapturedContext: false);
			string detail = ((versioned.Error.Length > 0) ? versioned.Error : lookup.Error);
			if (TryGetInfo(versioned.Data, modId, out var currentInfo) && currentInfo.TryGetProperty("recommendedUpgrade", out var upgrade) && upgrade.ValueKind == JsonValueKind.String)
			{
				string recommended = upgrade.GetString() ?? "";
				if (recommended.Length > 0)
				{
					detail = "Mod DB recommends " + recommended + ", but the downloadable compatible release could not be resolved. Retry the check or open the Mod DB page.";
				}
			}
			return (Success: false, Version: "", FileName: "", FileUrl: "", Error: (detail.Length > 0) ? detail : "The Mod DB did not return this mod identifier or a published compatible release.");
		}
		if (info.TryGetProperty("errorCode", out var ec))
		{
			return (Success: false, Version: "", FileName: "", FileUrl: "", Error: DescribeModDbError(ec));
		}
		string version = ((info.TryGetProperty("recommendedUpgrade", out var rec) && rec.ValueKind == JsonValueKind.String) ? (rec.GetString() ?? "") : "");
		string fileName = ((info.TryGetProperty("fileName", out var fn) && fn.ValueKind == JsonValueKind.String) ? (fn.GetString() ?? "") : "");
		string fileUrl = ((info.TryGetProperty("fileUrl", out var fu) && fu.ValueKind == JsonValueKind.String) ? (fu.GetString() ?? "") : "");
		if (version.Length == 0 || fileName.Length == 0 || fileUrl.Length == 0)
		{
			return (Success: false, Version: "", FileName: "", FileUrl: "", Error: "The Mod DB did not provide a downloadable compatible release. Draft mods and mods with no published files cannot self-update.");
		}
		Uri absoluteUri = new Uri(new Uri("https://mods.vintagestory.at/"), fileUrl);
		if (!string.Equals(absoluteUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) || (!string.Equals(absoluteUri.Host, "vintagestory.at", StringComparison.OrdinalIgnoreCase) && !absoluteUri.Host.EndsWith(".vintagestory.at", StringComparison.OrdinalIgnoreCase)))
		{
			return (Success: false, Version: "", FileName: "", FileUrl: "", Error: "Mod DB returned a download URL outside the Vintage Story HTTPS domain.");
		}
		return (Success: true, Version: version, FileName: fileName, FileUrl: absoluteUri.ToString(), Error: "");
	}

	private async Task<ModDbBatchResult> FetchInstallInformationAsync(IEnumerable<InstalledModInfo> mods, bool includeInstalledVersions)
	{
		List<InstalledModInfo> list = mods.Where((InstalledModInfo x) => !string.IsNullOrWhiteSpace(x.Id)).ToList();
		if (list.Count == 0)
		{
			return new ModDbBatchResult
			{
				Success = true
			};
		}
		string ids = string.Join(',', list.Select((InstalledModInfo x) => Uri.EscapeDataString((includeInstalledVersions && !string.IsNullOrWhiteSpace(x.Version)) ? (x.Id + "@" + x.Version) : x.Id)));
		string url = "https://mods.vintagestory.at/api/v2/mods/install-information?ids=" + ids + "&gv=" + Uri.EscapeDataString(targetGameVersion);
		string lastError = "";
		for (int attempt = 1; attempt <= 3; attempt++)
		{
			try
			{
				using HttpResponseMessage response = await http.GetAsync(url).ConfigureAwait(continueOnCapturedContext: false);
				string json = await response.Content.ReadAsStringAsync().ConfigureAwait(continueOnCapturedContext: false);
				int status = (int)response.StatusCode;
				if (!response.IsSuccessStatusCode)
				{
					lastError = $"Mod DB returned HTTP {status}.";
					if (status != 408 && status != 429 && status < 500)
					{
						return new ModDbBatchResult
						{
							Error = lastError
						};
					}
					await Task.Delay(250 * attempt * attempt).ConfigureAwait(continueOnCapturedContext: false);
					continue;
				}
				using JsonDocument doc = JsonDocument.Parse(json);
				if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
				{
					lastError = "Mod DB response did not contain a data object.";
					await Task.Delay(200 * attempt).ConfigureAwait(continueOnCapturedContext: false);
					continue;
				}
				Dictionary<string, JsonElement> copied = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
				foreach (JsonProperty prop in data.EnumerateObject())
				{
					copied[prop.Name] = prop.Value.Clone();
				}
				return new ModDbBatchResult
				{
					Success = true,
					Data = copied
				};
			}
			catch (TaskCanceledException ex)
			{
				lastError = "Mod DB request timed out: " + ex.Message;
				goto IL_05fd;
			}
			catch (HttpRequestException ex2)
			{
				lastError = "Mod DB network request failed: " + ex2.Message;
				goto IL_05fd;
			}
			catch (JsonException ex3)
			{
				lastError = "Mod DB returned invalid JSON: " + ex3.Message;
				break;
			}
			IL_05fd:
			if (attempt < 3)
			{
				await Task.Delay(250 * attempt * attempt).ConfigureAwait(continueOnCapturedContext: false);
			}
		}
		return new ModDbBatchResult
		{
			Error = ((lastError.Length > 0) ? lastError : "Mod DB request failed.")
		};
	}

	private static bool TryGetInfo(Dictionary<string, JsonElement> data, string modId, out JsonElement info)
	{
		if (data.TryGetValue(modId, out info))
		{
			return true;
		}
		foreach (KeyValuePair<string, JsonElement> datum in data)
		{
			string text = datum.Key;
			int num = text.IndexOf('@');
			if (num > 0)
			{
				text = text.Substring(0, num);
			}
			if (string.Equals(text, modId, StringComparison.OrdinalIgnoreCase))
			{
				info = datum.Value;
				return true;
			}
		}
		info = default;
		return false;
	}

	private static void ApplyInstallInfo(InstalledModInfo item, ModUpdateEntry entry, JsonElement info)
	{
		JsonElement value2;
		if (info.TryGetProperty("recommendedUpgrade", out var value) && value.ValueKind == JsonValueKind.String)
		{
			string text = (entry.RecommendedVersion = value.GetString() ?? "");
			if (text.Length > 0 && !string.Equals(text, item.Version, StringComparison.OrdinalIgnoreCase))
			{
				entry.Status = "Update";
				entry.Detail = "Compatible update available from the official Vintage Story Mod DB.";
			}
			else
			{
				entry.Status = "Current";
				entry.Detail = "Installed version is current for the target game version.";
			}
		}
		else if (info.TryGetProperty("errorCode", out value2))
		{
			int num = ((value2.ValueKind == JsonValueKind.Number && value2.TryGetInt32(out var value3)) ? value3 : 0);
			bool flag = (uint)(num - 4101) <= 1u;
			entry.Status = (flag ? "Retracted" : "NotFound");
			entry.Detail = DescribeModDbError(value2);
		}
		else
		{
			entry.Status = "Current";
			entry.Detail = "No compatible upgrade was recommended by the Mod DB.";
		}
	}

	private async Task DescribeKnownSuiteListingAsync(ModUpdateEntry entry)
	{
		entry.ModPageUrl = "https://mods.vintagestory.at/show/mod/67501";
		entry.AutoUpdateSupported = false;
		entry.AutoUpdateReason = "The Suite listing is missing from the Mod DB install index; use its listing page until the index is repaired.";
		try
		{
			using HttpResponseMessage response = await http.GetAsync("https://mods.vintagestory.at/api/mod/11556", HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(continueOnCapturedContext: false);
			response.EnsureSuccessStatusCode();
			using Stream input = await response.Content.ReadAsStreamAsync().ConfigureAwait(continueOnCapturedContext: false);
			using MemoryStream bounded = new MemoryStream();
			byte[] buffer = new byte[8192];
			while (true)
			{
				int num;
				int read = (num = await input.ReadAsync(buffer).ConfigureAwait(continueOnCapturedContext: false));
				if (num <= 0)
				{
					break;
				}
				if (bounded.Length + read > 1048576)
				{
					throw new InvalidDataException("Listing response exceeds its metadata limit.");
				}
				bounded.Write(buffer, 0, read);
			}
			bounded.Position = 0L;
			using JsonDocument document = JsonDocument.Parse(bounded);
			if (!document.RootElement.TryGetProperty("mod", out var mod) || !mod.TryGetProperty("releases", out var releases) || releases.ValueKind != JsonValueKind.Array)
			{
				return;
			}
			string latest = "";
			SemVer latestVersion = null;
			foreach (JsonElement item in releases.EnumerateArray())
			{
				if (item.TryGetProperty("modversion", out var value) && value.ValueKind == JsonValueKind.String && SemVer.TryParse(value.GetString() ?? "", out var version) && (latestVersion == null || version > latestVersion))
				{
					latest = value.GetString();
					latestVersion = version;
				}
				value = default;
				version = null;
			}
			if (latest.Length == 0)
			{
				return;
			}
			entry.Status = "Published";
			entry.Detail = "Public Suite listing: " + latest + "; installed " + entry.InstalledVersion + ". Automatic compatibility lookup is unavailable because its runtime-ID index is missing.";
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			entry.Detail = entry.Detail + " Public listing check failed: " + ex2.GetType().Name + ".";
		}
	}

	private static string DescribeModDbError(JsonElement errorCode)
	{
		int num = ((errorCode.ValueKind == JsonValueKind.Number && errorCode.TryGetInt32(out var value)) ? value : 0);
		if (1 == 0)
		{
		}
		string result = num switch
		{
			4001 => "Mod DB could not parse this installed mod identifier/version.", 
			4002 => "Mod DB requires a version or target game version for this lookup.", 
			4041 => "The Mod DB install index does not recognize this runtime mod ID. Check its listing page for public releases.", 
			4031 => "The Mod DB could not find an eligible published release for this mod identifier.", 
			4032 => "The Mod DB refused the release-retraction override.", 
			4101 => "The matching Mod DB release is retracted and is not eligible for automatic update.", 
			4102 => "The matching Mod DB release is force-retracted and cannot be downloaded automatically.", 
			_ => "Mod DB lookup error " + errorCode.ToString() + ".", 
		};
		if (1 == 0)
		{
		}
		return result;
	}

	private static ModUpdateEntry CloneEntry(ModUpdateEntry x)
	{
		return new ModUpdateEntry
		{
			ModId = x.ModId,
			InstalledVersion = x.InstalledVersion,
			RecommendedVersion = x.RecommendedVersion,
			Status = x.Status,
			Detail = x.Detail,
			ModPageUrl = x.ModPageUrl,
			AutoUpdateSupported = x.AutoUpdateSupported,
			AutoUpdateReason = x.AutoUpdateReason
		};
	}

	private async Task DownloadWithLimitAsync(string url, string destination, long maxBytes)
	{
		using HttpResponseMessage response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(continueOnCapturedContext: false);
		response.EnsureSuccessStatusCode();
		long? contentLength = response.Content.Headers.ContentLength;
		long length = default;
		int num;
		if (contentLength.HasValue)
		{
			length = contentLength.GetValueOrDefault();
			num = ((length > maxBytes) ? 1 : 0);
		}
		else
		{
			num = 0;
		}
		if (num != 0)
		{
			throw new InvalidDataException($"Download is {length / 1024 / 1024} MiB; the NSE safety limit is {maxBytes / 1024 / 1024} MiB.");
		}
		await using Stream input = await response.Content.ReadAsStreamAsync().ConfigureAwait(continueOnCapturedContext: false);
		await using FileStream output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
		byte[] buffer = new byte[81920];
		long total = 0L;
		while (true)
		{
			int read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length)).ConfigureAwait(continueOnCapturedContext: false);
			if (read <= 0)
			{
				break;
			}
			total += read;
			if (total > maxBytes)
			{
				throw new InvalidDataException("Downloaded package exceeded the NSE safety limit.");
			}
			await output.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(continueOnCapturedContext: false);
		}
		await output.FlushAsync().ConfigureAwait(continueOnCapturedContext: false);
	}

	private static (bool Success, string Error, Dictionary<string, string> Dependencies) ValidateDownloadedMod(string path, string expectedModId, string expectedVersion)
	{
		try
		{
			using ZipArchive zipArchive = ZipFile.OpenRead(path);
			if (zipArchive.Entries.Count > 100000)
			{
				return (Success: false, Error: "ZIP contains too many entries.", Dependencies: new Dictionary<string, string>());
			}
			HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			long num = 0L;
			foreach (ZipArchiveEntry entry in zipArchive.Entries)
			{
				string text = entry.FullName.Replace('\\', '/');
				if (text.StartsWith('/') || text.Contains(':') || text.Split('/').Any((string part) => part == ".." || part == ".") || !hashSet.Add(text))
				{
					return (Success: false, Error: "ZIP contains an unsafe or duplicate path.", Dependencies: new Dictionary<string, string>());
				}
				if (((entry.ExternalAttributes >> 16) & 0xF000) == 40960)
				{
					return (Success: false, Error: "ZIP symbolic links are not supported.", Dependencies: new Dictionary<string, string>());
				}
				if (entry.Length > 536870912 - num)
				{
					return (Success: false, Error: "ZIP expands beyond the 512 MiB installation limit.", Dependencies: new Dictionary<string, string>());
				}
				num += entry.Length;
			}
			ZipArchiveEntry zipArchiveEntry = zipArchive.Entries.SingleOrDefault((ZipArchiveEntry x) => string.Equals(x.FullName, "modinfo.json", StringComparison.OrdinalIgnoreCase));
			if (zipArchiveEntry == null)
			{
				return (Success: false, Error: "modinfo.json is missing from the ZIP root.", Dependencies: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
			}
			if (zipArchiveEntry.Length > 1048576)
			{
				return (Success: false, Error: "modinfo.json is unexpectedly large.", Dependencies: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
			}
			using Stream utf8Json = zipArchiveEntry.Open();
			using JsonDocument jsonDocument = JsonDocument.Parse(utf8Json);
			string text2 = (jsonDocument.RootElement.TryGetProperty("modid", out var value) ? (value.GetString() ?? "") : "");
			string text3 = (jsonDocument.RootElement.TryGetProperty("version", out var value2) ? (value2.GetString() ?? "") : "");
			if (!string.Equals(text2, expectedModId, StringComparison.OrdinalIgnoreCase))
			{
				return (Success: false, Error: $"expected mod ID {expectedModId}, package declares {text2}.", Dependencies: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
			}
			if (!string.Equals(text3, expectedVersion, StringComparison.OrdinalIgnoreCase))
			{
				return (Success: false, Error: $"expected version {expectedVersion}, package declares {text3}.", Dependencies: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
			}
			Dictionary<string, string> dictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			if (jsonDocument.RootElement.TryGetProperty("dependencies", out var value3))
			{
				if (value3.ValueKind != JsonValueKind.Object)
				{
					return (Success: false, Error: "dependencies must be an object.", Dependencies: dictionary);
				}
				foreach (JsonProperty item in value3.EnumerateObject())
				{
					if (!ModInfo.IsValidModID(item.Name) || item.Value.ValueKind != JsonValueKind.String)
					{
						return (Success: false, Error: "Invalid dependency ID/version.", Dependencies: dictionary);
					}
					string text4 = item.Value.GetString() ?? "";
					if (text4.Length > 0 && text4 != "*" && !SemVer.TryParse(text4, out var _))
					{
						return (Success: false, Error: "Invalid minimum version for " + item.Name + ".", Dependencies: dictionary);
					}
					if (!dictionary.TryAdd(item.Name, text4))
					{
						return (Success: false, Error: "Duplicate dependency " + item.Name + ".", Dependencies: dictionary);
					}
				}
			}
			return (Success: true, Error: "", Dependencies: dictionary);
		}
		catch (InvalidDataException ex)
		{
			return (Success: false, Error: "invalid ZIP: " + ex.Message, Dependencies: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
		}
		catch (JsonException ex2)
		{
			return (Success: false, Error: "invalid modinfo.json: " + ex2.Message, Dependencies: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
		}
		catch (Exception ex3)
		{
			return (Success: false, Error: ex3.GetType().Name + ": " + ex3.Message, Dependencies: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
		}
	}

	internal static List<string> ValidateDependencyVersions(IReadOnlyDictionary<string, string> dependencies, IReadOnlyDictionary<string, string> available)
	{
		List<string> list = new List<string>();
		foreach (KeyValuePair<string, string> dependency in dependencies)
		{
			if (!available.TryGetValue(dependency.Key, out string value))
			{
				list.Add(dependency.Key + " is missing or disabled");
				continue;
			}
			string value2 = dependency.Value;
			if (value2.Length != 0 && !(value2 == "*"))
			{
				if (!SemVer.TryParse(value2, out var result) || !SemVer.TryParse(value, out var result2))
				{
					list.Add(dependency.Key + " version could not be verified");
				}
				else if (result2 < result)
				{
					list.Add(dependency.Key + " requires " + value2 + " or newer; available " + value);
				}
			}
		}
		return list;
	}

	private static string SanitizeFilePart(string value)
	{
		char[] invalid = Path.GetInvalidFileNameChars();
		return new string((value ?? "mod").Select((char ch) => invalid.Contains(ch) ? '_' : ch).ToArray());
	}

	public void Dispose()
	{
		disposed = true;
		if (tickListenerId >= 0)
		{
			try
			{
				api.Event.UnregisterGameTickListener(tickListenerId);
			}
			catch
			{
			}
			tickListenerId = -1L;
		}
		try
		{
			http.Dispose();
		}
		catch
		{
		}
	}
}
