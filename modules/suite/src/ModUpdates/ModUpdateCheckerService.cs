using System;
using ProperVersion;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using BlockLogger.Services;
using System.Threading;
using System.Threading.Tasks;
using Vintagestory.API.Server;

namespace BlockLogger.ModUpdates;

public sealed class ModUpdateEntry
{
    public string ModId { get; set; } = "";
    public string InstalledVersion { get; set; } = "";
    public string RecommendedVersion { get; set; } = "";
    public string Status { get; set; } = "Unknown"; // Current | Update | NotFound | Error | Core
    public string Detail { get; set; } = "";
    public string ModPageUrl { get; set; } = "";
    public bool AutoUpdateSupported { get; set; }
    public string AutoUpdateReason { get; set; } = "";
    /// <summary>Staff chose to stop flagging this mod until the game version, installed version or offered release changes.</summary>
    public bool Ignored { get; set; }
    public string IgnoreNote { get; set; } = "";
    public long IgnoredUnixMilliseconds { get; set; }
    public string IgnoredBy { get; set; } = "";
    /// <summary>The mod's own "game" dependency, for example 1.21.0 (a minimum).</summary>
    public string GameRequirement { get; set; } = "";
    /// <summary>Compatible | RequiresNewerGame | NoCompatibleRelease | Unknown</summary>
    public string GameCompatibility { get; set; } = "Unknown";
    public List<ModDependencyInfo> Dependencies { get; set; } = new();
    public List<string> RequiredBy { get; set; } = new();
    public int DependencyIssues { get; set; }
}

public sealed class ModUpdateSnapshot
{
    public long CheckedUnixMilliseconds { get; set; }
    public string TargetGameVersion { get; set; } = "";
    public bool CheckInProgress { get; set; }
    public string LastError { get; set; } = "";
    public string LastWarning { get; set; } = "";
    public int LookupFailedCount { get; set; }
    public int UpdateCount { get; set; }
    public int CurrentCount { get; set; }
    public int UnknownCount { get; set; }
    public int IgnoredCount { get; set; }
    public int DependencyIssueCount { get; set; }
    public bool AllowSameMinorGameUpdates { get; set; }
    public bool RestartRequired { get; set; }
    public List<string> StagedUpdates { get; set; } = new();
    public List<ModUpdateEntry> Mods { get; set; } = new();
}


public sealed class ModUpdateApplyResult
{
    public bool Success { get; set; }
    public string ModId { get; set; } = "";
    public string InstalledVersion { get; set; } = "";
    public string NewVersion { get; set; } = "";
    public string Message { get; set; } = "";
    public string BackupPath { get; set; } = "";
    public string InstalledPath { get; set; } = "";
    public string ModPageUrl { get; set; } = "";
    public bool RestartRequired { get; set; }
}

internal sealed class InstalledModInfo
{
    public string Id { get; set; } = "";
    public string Version { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public string FileName { get; set; } = "";
}

public sealed class ModUpdateCheckerService : IDisposable
{
    private readonly ICoreServerAPI api;
    private readonly string targetGameVersion;
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object sync = new();
    private ModUpdateSnapshot snapshot = new();
    private long tickListenerId = -1;
    private bool disposed;
    public bool AllowSameMinorGameUpdates { get; set; }
    private List<InstalledModInfo> installedMods = new();
    private readonly Dictionary<string, string> stagedUpdates = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> loadedModVersions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> stagedVersions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, string>> loadedDependencies = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, Dictionary<string, string>> declaredDependencies = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, List<string>> requiredBy = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> installedVersionById = new(StringComparer.OrdinalIgnoreCase);
    private ModUpdateIgnoreStore ignores = new();
    public const string IgnoreFile = "NewfiesModUpdateIgnores.json";

    public ModUpdateCheckerService(ICoreServerAPI api, string targetGameVersion)
    {
        this.api = api;
        this.targetGameVersion = targetGameVersion;
        http.DefaultRequestHeaders.UserAgent.ParseAdd("NewfiesServerEssentialsSuite/" + SuiteRelease.Version);
    }

    public void Start()
    {
        installedMods = api.ModLoader.Mods
            .Where(x => x?.Info != null && !x.Info.CoreMod)
            .Select(x => new InstalledModInfo
            {
                Id = (x.Info.ModID ?? "").Trim(),
                Version = (x.Info.Version ?? "").Trim(),
                SourcePath = (x.SourcePath ?? "").Trim(),
                FileName = (x.FileName ?? "").Trim()
            })
            .Where(x => x.Id.Length > 0 && x.Version.Length > 0 && !string.Equals(x.Id, "game", StringComparison.OrdinalIgnoreCase))
            .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase).ToList();
        loadedModVersions = api.ModLoader.Mods
            .Where(x => x?.Info != null && !string.IsNullOrWhiteSpace(x.Info.ModID) && api.ModLoader.IsModEnabled(x.Info.ModID))
            .GroupBy(x => x.Info.ModID, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Info.Version ?? "", StringComparer.OrdinalIgnoreCase);
        loadedModVersions["game"] = targetGameVersion;
        foreach (var mod in api.ModLoader.Mods.Where(x => x?.Info != null && loadedModVersions.ContainsKey(x.Info.ModID ?? "")))
            loadedDependencies[mod.Info.ModID] = (mod.Info.Dependencies ?? Array.Empty<Vintagestory.API.Common.ModDependency>()).GroupBy(d => d.ModID, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Version ?? "", StringComparer.OrdinalIgnoreCase);
        // Declared dependencies for every installed mod (enabled or not) drive the dependency view.
        declaredDependencies = api.ModLoader.Mods
            .Where(x => x?.Info != null && !string.IsNullOrWhiteSpace(x.Info.ModID))
            .GroupBy(x => x.Info.ModID, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (g.First().Info.Dependencies ?? Array.Empty<Vintagestory.API.Common.ModDependency>())
                .Where(d => !string.IsNullOrWhiteSpace(d.ModID)).GroupBy(d => d.ModID, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(d => d.Key, d => d.First().Version ?? "", StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        requiredBy = ModUpdateAnalysis.ReverseDependencies(declaredDependencies.ToDictionary(
            x => x.Key, x => (IReadOnlyDictionary<string, string>)x.Value, StringComparer.OrdinalIgnoreCase));
        installedVersionById = api.ModLoader.Mods
            .Where(x => x?.Info != null && !string.IsNullOrWhiteSpace(x.Info.ModID))
            .GroupBy(x => x.Info.ModID, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Info.Version ?? "", StringComparer.OrdinalIgnoreCase);
        LoadIgnores();
        lock (sync) snapshot.TargetGameVersion = targetGameVersion;
        // Delayed startup check avoids competing with world/mod initialization.
        api.Event.RegisterCallback(dt => { _ = CheckAsync(false); }, 60_000);
        // Cached automatic check every 6h; manual checks are also available in the Mods page.
        tickListenerId = api.Event.RegisterGameTickListener(dt => { _ = CheckAsync(false); }, 6 * 60 * 60 * 1000);
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
                IgnoredCount = snapshot.IgnoredCount,
                DependencyIssueCount = snapshot.DependencyIssueCount,
                AllowSameMinorGameUpdates = AllowSameMinorGameUpdates,
                RestartRequired = snapshot.RestartRequired,
                StagedUpdates = snapshot.StagedUpdates.ToList(),
                Mods = snapshot.Mods.Select(CloneEntry).ToList()
            };
        }
    }

    public async Task<ModUpdateSnapshot> CheckAsync(bool force)
    {
        if (disposed) return GetSnapshot();
        if (!force)
        {
            var current = GetSnapshot();
            if (current.CheckedUnixMilliseconds > 0 && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - current.CheckedUnixMilliseconds < 5 * 60 * 1000)
                return current;
        }
        if (!await gate.WaitAsync(0).ConfigureAwait(false)) return GetSnapshot();
        try
        {
            lock (sync) { snapshot.CheckInProgress = true; snapshot.LastError = ""; snapshot.LastWarning = ""; snapshot.LookupFailedCount = 0; }
            var installed = installedMods.ToList();

            var results = installed.ToDictionary(x => x.Id, x => CreateEntry(x, "Unknown", "Not checked."), StringComparer.OrdinalIgnoreCase);
            int lookupFailures = 0;
            var prior = GetSnapshot().Mods.ToDictionary(x => x.ModId, x => x, StringComparer.OrdinalIgnoreCase);
            foreach (var batch in installed.Chunk(20))
            {
                var batchItems = batch.ToList();
                ModDbBatchResult lookup = await FetchCompatibleInformationAsync(batchItems, includeInstalledVersions: true).ConfigureAwait(false);
                if (!lookup.Success)
                    lookup = await FetchCompatibleInformationAsync(batchItems, includeInstalledVersions: false).ConfigureAwait(false);

                foreach (var item in batchItems)
                {
                    var entry = results[item.Id];
                    if (!TryGetInfo(lookup.Data, item.Id, out var info))
                    {
                        // A partial Mod DB batch response should not make the whole inventory unknown.
                        // Retry the missing item by itself, first with installed version context and then
                        // with the plain identifier for API compatibility across Mod DB revisions.
                        var single = await FetchCompatibleInformationAsync(new[] { item }, includeInstalledVersions: true).ConfigureAwait(false);
                        if (!TryGetInfo(single.Data, item.Id, out info))
                        {
                            single = await FetchCompatibleInformationAsync(new[] { item }, includeInstalledVersions: false).ConfigureAwait(false);
                        }

                        if (!TryGetInfo(single.Data, item.Id, out info))
                        {
                            lookupFailures++;
                            if (prior.TryGetValue(item.Id, out var cached) && cached.Status is "Current" or "Update" or "Staged")
                            {
                                entry = CloneEntry(cached);
                                entry.Detail = "Cached result retained; latest Mod DB lookup failed. " + (single.Error.Length > 0 ? single.Error : lookup.Error);
                                results[item.Id] = entry;
                            }
                            else
                            {
                                entry.Status = "Error";
                                entry.Detail = single.Error.Length > 0 ? single.Error : (lookup.Error.Length > 0 ? lookup.Error : "The Mod DB did not return this mod identifier.");
                            }
                            continue;
                        }
                    }

                    ApplyInstallInfo(item, entry, info);
                    if (item.Id.Equals("newfiesserveressentialssuite", StringComparison.OrdinalIgnoreCase) && info.TryGetProperty("errorCode", out var missingIndex) && missingIndex.TryGetInt32(out int code) && code == 4041)
                        await DescribeKnownSuiteListingAsync(entry).ConfigureAwait(false);
                    if (stagedUpdates.TryGetValue(item.Id, out var stagedDescription))
                    {
                        entry.Status = "Staged";
                        entry.Detail = stagedDescription + ". Server restart required.";
                    }
                }
            }

            foreach (var entry in results.Values) FinishEntry(entry);
            var list = results.Values.OrderByDescending(x => x.Status == "Update" && !x.Ignored).ThenBy(x => x.ModId, StringComparer.OrdinalIgnoreCase).ToList();
            lock (sync)
            {
                snapshot.Mods = list;
                snapshot.CheckedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                RefreshCounts();
                snapshot.RestartRequired = stagedUpdates.Count > 0;
                snapshot.StagedUpdates = stagedUpdates.Values.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
                snapshot.CheckInProgress = false;
                snapshot.LookupFailedCount = lookupFailures;
                snapshot.LastError = list.Count > 0 && list.All(x => x.Status == "Error") ? "All Mod DB requests failed after retries." : "";
                snapshot.LastWarning = lookupFailures > 0 && string.IsNullOrWhiteSpace(snapshot.LastError) ? $"{lookupFailures} mod lookup(s) could not be refreshed; cached/partial results were retained." : "";
            }
            return GetSnapshot();
        }
        catch (Exception ex)
        {
            lock (sync) { snapshot.CheckInProgress = false; snapshot.LastError = ex.GetType().Name + ": " + ex.Message; snapshot.CheckedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); }
            api.Logger.Warning("[ServerEssentials/ModUpdates] Update check failed: {0}: {1}", ex.GetType().Name, ex.Message);
            return GetSnapshot();
        }
        finally { gate.Release(); }
    }

    public async Task<ModUpdateApplyResult> ApplyUpdateAsync(string modId)
    {
        modId = (modId ?? "").Trim();
        var result = new ModUpdateApplyResult
        {
            ModId = modId,
            ModPageUrl = BuildModPageUrl(modId)
        };
        if (disposed) { result.Message = "Mod update checker is shutting down."; return result; }
        if (modId.Length == 0) { result.Message = "Mod ID is required."; return result; }

        var installed = installedMods.FirstOrDefault(x => string.Equals(x.Id, modId, StringComparison.OrdinalIgnoreCase));
        if (installed == null) { result.Message = "That mod is not a loaded third-party mod."; return result; }
        result.InstalledVersion = installed.Version;
        if (stagedUpdates.TryGetValue(installed.Id, out var alreadyStaged))
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

        if (!await gate.WaitAsync(0).ConfigureAwait(false))
        {
            result.Message = "Another Mod DB check or update operation is already running.";
            return result;
        }

        string stagingRoot = Path.Combine(SuiteDataPaths.GetSuiteRoot(api), "mod-update-staging");
        string backupRoot = Path.Combine(SuiteDataPaths.GetSuiteRoot(api), "mod-update-backups");
        Directory.CreateDirectory(stagingRoot);
        Directory.CreateDirectory(backupRoot);
        string? downloadedPath = null;
        string? sameDirTemp = null;
        try
        {
            var selected = await GetRecommendedDownloadAsync(installed.Id, installed.Version).ConfigureAwait(false);
            if (!selected.Success)
            {
                result.Message = selected.Error;
                return result;
            }
            if (!SemVer.TryParse(selected.Version, out var selectedSemver) || !SemVer.TryParse(installed.Version, out var installedSemver))
            {
                result.Message = "The installed or recommended version could not be compared safely. Use the listing page for a manual update.";
                return result;
            }
            if (selectedSemver <= installedSemver)
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
            await DownloadWithLimitAsync(selected.FileUrl, downloadedPath, 256L * 1024 * 1024).ConfigureAwait(false);

            var validation = ValidateDownloadedMod(downloadedPath, installed.Id, selected.Version);
            if (!validation.Success)
            {
                result.Message = "Downloaded package validation failed: " + validation.Error;
                return result;
            }

            if (disposed) { result.Message = "Server is shutting down; no update was staged."; return result; }
            var availableVersions = new Dictionary<string, string>(loadedModVersions, StringComparer.OrdinalIgnoreCase);
            foreach (var staged in stagedVersions) availableVersions[staged.Key] = staged.Value;
            availableVersions[installed.Id] = selected.Version;
            var dependencyProblems = ValidateDependencyVersions(validation.Dependencies, availableVersions);
            foreach (var dependent in loadedDependencies.Where(d => !d.Key.Equals(installed.Id, StringComparison.OrdinalIgnoreCase) && d.Value.ContainsKey(installed.Id)))
                dependencyProblems.AddRange(ValidateDependencyVersions(new Dictionary<string, string> { [installed.Id] = dependent.Value[installed.Id] }, availableVersions).Select(problem => dependent.Key + ": " + problem));
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
                result.Message = $"A file named {safeDownloadName} already exists in the Mods folder. Resolve that duplicate manually before updating.";
                return result;
            }

            string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            string backupDir = Path.Combine(backupRoot, stamp + "-" + SanitizeFilePart(installed.Id));
            Directory.CreateDirectory(backupDir);
            string backupPath = Path.Combine(backupDir, Path.GetFileName(oldPath));
            File.Copy(oldPath, backupPath, false);
            result.BackupPath = backupPath;

            sameDirTemp = Path.Combine(modsDirectory, ".nse-update-" + Guid.NewGuid().ToString("N") + ".tmp");
            File.Copy(downloadedPath, sameDirTemp, true);

            try
            {
                if (string.Equals(targetPath, oldPath, StringComparison.OrdinalIgnoreCase))
                {
                    File.Move(sameDirTemp, oldPath, true);
                    sameDirTemp = null;
                }
                else
                {
                    File.Move(sameDirTemp, targetPath, false);
                    sameDirTemp = null;
                    try
                    {
                        File.Delete(oldPath);
                    }
                    catch
                    {
                        try { File.Delete(targetPath); } catch { }
                        throw;
                    }
                }
            }
            catch
            {
                // Best-effort rollback. The backup is outside Mods so it cannot create a duplicate on next boot.
                if (!File.Exists(oldPath) && File.Exists(backupPath))
                {
                    try { File.Copy(backupPath, oldPath, false); } catch { }
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
                snapshot.StagedUpdates = stagedUpdates.Values.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
                var existing = snapshot.Mods.FirstOrDefault(x => string.Equals(x.ModId, installed.Id, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    existing.Status = "Staged";
                    existing.Detail = $"Update {installed.Version} → {selected.Version} staged on disk. Server restart required.";
                    existing.RecommendedVersion = selected.Version;
                }
                RefreshCounts();
            }
            api.Logger.Notification("[ServerEssentials/ModUpdates] Staged mod update {0} {1} -> {2}. Backup: {3}", installed.Id, installed.Version, selected.Version, backupPath);
            return result;
        }
        catch (Exception ex)
        {
            result.Message = ex.GetType().Name + ": " + ex.Message;
            api.Logger.Warning("[ServerEssentials/ModUpdates] Failed to stage update for {0}: {1}: {2}", modId, ex.GetType().Name, ex.Message);
            return result;
        }
        finally
        {
            if (sameDirTemp != null) { try { File.Delete(sameDirTemp); } catch { } }
            if (downloadedPath != null) { try { File.Delete(downloadedPath); } catch { } }
            gate.Release();
        }
    }

    private ModUpdateEntry CreateEntry(InstalledModInfo mod, string status, string detail)
    {
        bool supported = CanAutoUpdate(mod, out string reason);
        var entry = new ModUpdateEntry
        {
            ModId = mod.Id,
            InstalledVersion = mod.Version,
            Status = status,
            Detail = detail,
            ModPageUrl = BuildModPageUrl(mod.Id),
            AutoUpdateSupported = supported,
            AutoUpdateReason = supported ? (string.Equals(mod.Id, "newfiesserveressentialssuite", StringComparison.OrdinalIgnoreCase) ? "Suite self-update is supported when the installed Suite is a ZIP and the Mod DB has a published compatible release; restart loads the new build." : "ZIP package can be safely staged with backup and restart.") : reason
        };
        FillDependencyInfo(entry);
        return entry;
    }

    private void FillDependencyInfo(ModUpdateEntry entry)
    {
        declaredDependencies.TryGetValue(entry.ModId, out var declared);
        declared ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        entry.Dependencies = ModUpdateAnalysis.ResolveDependencies(declared, installedVersionById, loadedModVersions);
        entry.DependencyIssues = entry.Dependencies.Count(x => x.Status is "Missing" or "Disabled" or "TooOld");
        entry.RequiredBy = requiredBy.TryGetValue(entry.ModId, out var users) ? users.ToList() : new List<string>();
        declared.TryGetValue("game", out var gameRequired);
        (entry.GameRequirement, entry.GameCompatibility) = ModUpdateAnalysis.DescribeGameRequirement(gameRequired ?? "", targetGameVersion);
    }

    /// <summary>Runs after the Mod DB result is known: game compatibility from the index, then the ignore decision.</summary>
    private void FinishEntry(ModUpdateEntry entry)
    {
        if (entry.Status == "NotFound") entry.GameCompatibility = "NoCompatibleRelease";
        else if (entry.Status is "Current" or "Update" && entry.GameCompatibility == "Unknown") entry.GameCompatibility = "Compatible";
        ApplyIgnoreState(entry);
    }

    private void ApplyIgnoreState(ModUpdateEntry entry)
    {
        entry.Ignored = false; entry.IgnoreNote = ""; entry.IgnoredBy = ""; entry.IgnoredUnixMilliseconds = 0;
        ModUpdateIgnoreRecord? record;
        lock (sync) record = ignores.Ignores.FirstOrDefault(x => string.Equals(x.ModId, entry.ModId, StringComparison.OrdinalIgnoreCase));
        if (record == null) return;
        if (ModUpdateAnalysis.IsIgnoreActive(record, targetGameVersion, entry.InstalledVersion, entry.RecommendedVersion, entry.Status))
        {
            entry.Ignored = true;
            entry.IgnoredBy = record.IgnoredBy;
            entry.IgnoredUnixMilliseconds = record.IgnoredUnixMilliseconds;
            entry.IgnoreNote = "Ignored until the game version, this mod's installed version or the offered release changes.";
        }
        else
        {
            string why = ModUpdateAnalysis.IgnoreExpiryReason(record, targetGameVersion, entry.InstalledVersion, entry.RecommendedVersion, entry.Status);
            if (why.Length > 0) entry.IgnoreNote = "Earlier ignore expired: " + why + ".";
        }
    }

    // Caller holds sync.
    private void RefreshCounts()
    {
        var list = snapshot.Mods;
        snapshot.UpdateCount = list.Count(x => x.Status == "Update" && !x.Ignored);
        snapshot.CurrentCount = list.Count(x => x.Status == "Current");
        snapshot.IgnoredCount = list.Count(x => x.Ignored);
        snapshot.UnknownCount = list.Count(x => x.Status != "Update" && x.Status != "Current" && x.Status != "Staged" && !x.Ignored);
        snapshot.DependencyIssueCount = list.Count(x => x.DependencyIssues > 0);
    }

    private void LoadIgnores()
    {
        try
        {
            var loaded = ModConfigRecovery.LoadOrNull<ModUpdateIgnoreStore>(api, IgnoreFile, "ModUpdateIgnores");
            lock (sync) ignores = loaded ?? new ModUpdateIgnoreStore();
            lock (sync) ignores.Ignores ??= new List<ModUpdateIgnoreRecord>();
        }
        catch (Exception ex)
        {
            api.Logger.Warning("[ServerEssentials/ModUpdates] Could not read the ignore list ({0}); starting with none.", ex.GetType().Name);
            lock (sync) ignores = new ModUpdateIgnoreStore();
        }
    }

    /// <summary>Ignores or restores one mod. The decision is recorded against what is installed and offered right now.</summary>
    public (bool Success, string Message) SetIgnored(string modId, bool ignore, string actor)
    {
        modId = (modId ?? "").Trim();
        if (modId.Length == 0) return (false, "A mod id is required.");
        lock (sync)
        {
            var entry = snapshot.Mods.FirstOrDefault(x => string.Equals(x.ModId, modId, StringComparison.OrdinalIgnoreCase));
            if (entry == null) return (false, "Run Check Updates first; that mod has no result yet.");
            if (ignore && !ModUpdateAnalysis.IsIgnorable(entry.Status)) return (false, $"A mod with status '{entry.Status}' has nothing to ignore.");
            ignores.Ignores.RemoveAll(x => string.Equals(x.ModId, modId, StringComparison.OrdinalIgnoreCase));
            if (ignore)
                ignores.Ignores.Add(new ModUpdateIgnoreRecord
                {
                    ModId = entry.ModId, Status = entry.Status, GameVersion = targetGameVersion,
                    InstalledVersion = entry.InstalledVersion, RecommendedVersion = entry.RecommendedVersion,
                    IgnoredBy = (actor ?? "").Trim(), IgnoredUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                });
            try { SuiteConfigStorage.Store(api, ignores, IgnoreFile); }
            catch (Exception ex) { return (false, "Could not save the ignore list: " + ex.GetType().Name); }
            ApplyIgnoreState(entry);
            RefreshCounts();
            return (true, ignore ? "Ignored until the game version, installed version or offered release changes." : "Notifications restored.");
        }
    }

    private static string BuildModPageUrl(string modId) =>
        "https://mods.vintagestory.at/" + Uri.EscapeDataString((modId ?? "").Trim());

    private static bool CanAutoUpdate(InstalledModInfo mod, out string reason)
    {
        if (string.IsNullOrWhiteSpace(mod.SourcePath)) { reason = "Vintage Story did not expose a source package path for this mod."; return false; }
        if (!File.Exists(mod.SourcePath)) { reason = "The loaded mod source is not a regular file (folder/dev mods are manual-update only)."; return false; }
        if (!mod.SourcePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) { reason = "Only loaded ZIP packages are eligible for automatic replacement."; return false; }
        reason = "";
        return true;
    }

    private async Task<(bool Success, string Version, string FileName, string FileUrl, string Error)> GetRecommendedDownloadAsync(string modId, string installedVersion)
    {
        var probe = new InstalledModInfo { Id = modId, Version = installedVersion };

        // For staging we intentionally query the plain identifier with gv. Per the Mod DB
        // contract, identifier@installedVersion returns attachment data for the installed
        // release while recommendedUpgrade only names the newer release. A plain identifier
        // selects the compatible release itself, so fileName/fileUrl and recommendedUpgrade
        // all describe the package we actually need to download and validate.
        ModDbBatchResult lookup = await FetchCompatibleInformationAsync(new[] { probe }, includeInstalledVersions: false).ConfigureAwait(false);
        if (!TryGetInfo(lookup.Data, modId, out var info))
        {
            // Versioned retry is diagnostic/fallback only; do not pair its attachment with a
            // newer recommendedUpgrade because that attachment may be the currently installed ZIP.
            var versioned = await FetchCompatibleInformationAsync(new[] { probe }, includeInstalledVersions: true).ConfigureAwait(false);
            string detail = versioned.Error.Length > 0 ? versioned.Error : lookup.Error;
            if (TryGetInfo(versioned.Data, modId, out var currentInfo) &&
                currentInfo.TryGetProperty("recommendedUpgrade", out var upgrade) && upgrade.ValueKind == JsonValueKind.String)
            {
                string recommended = upgrade.GetString() ?? "";
                if (recommended.Length > 0)
                    detail = $"Mod DB recommends {recommended}, but the downloadable compatible release could not be resolved. Retry the check or open the Mod DB page.";
            }
            return (false, "", "", "", detail.Length > 0 ? detail : "The Mod DB did not return this mod identifier or a published compatible release.");
        }
        if (info.TryGetProperty("errorCode", out var ec)) return (false, "", "", "", DescribeModDbError(ec));
        string version = info.TryGetProperty("recommendedUpgrade", out var rec) && rec.ValueKind == JsonValueKind.String ? rec.GetString() ?? "" : "";
        string fileName = info.TryGetProperty("fileName", out var fn) && fn.ValueKind == JsonValueKind.String ? fn.GetString() ?? "" : "";
        string fileUrl = info.TryGetProperty("fileUrl", out var fu) && fu.ValueKind == JsonValueKind.String ? fu.GetString() ?? "" : "";
        if (version.Length == 0 || fileName.Length == 0 || fileUrl.Length == 0)
            return (false, "", "", "", "The Mod DB did not provide a downloadable compatible release. Draft mods and mods with no published files cannot self-update.");
        var absoluteUri = new Uri(new Uri("https://mods.vintagestory.at/"), fileUrl);
        if (!string.Equals(absoluteUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !(string.Equals(absoluteUri.Host, "vintagestory.at", StringComparison.OrdinalIgnoreCase) ||
              absoluteUri.Host.EndsWith(".vintagestory.at", StringComparison.OrdinalIgnoreCase)))
            return (false, "", "", "", "Mod DB returned a download URL outside the Vintage Story HTTPS domain.");
        return (true, version, fileName, absoluteUri.ToString(), "");
    }

    private async Task<ModDbBatchResult> FetchCompatibleInformationAsync(IEnumerable<InstalledModInfo> mods, bool includeInstalledVersions)
    {
        var list = mods.ToList();
        var result = await FetchInstallInformationAsync(list, includeInstalledVersions, targetGameVersion).ConfigureAwait(false);
        // Explicit owner opt-in only: some authors tag 1.22.0/1.22.2 but not each later patch.
        // Ask the official install index for each earlier stable patch. It still filters
        // retracted releases; ZIP identity, version and dependencies are validated before staging.
        if (!result.Success || !AllowSameMinorGameUpdates || !Version.TryParse(targetGameVersion, out var game) || game.Build <= 0 || game.Build > 32)
            return result;
        for (int patch = game.Build - 1; patch >= 0; patch--)
        {
            if (disposed) break;
            string taggedGame = $"{game.Major}.{game.Minor}.{patch}";
            var older = await FetchInstallInformationAsync(list, false, taggedGame).ConfigureAwait(false);
            if (!older.Success) continue;
            foreach (var mod in list)
            {
                if (!TryGetInfo(older.Data, mod.Id, out var candidate) || !TryReleaseVersion(candidate, out var version)) continue;
                if (TryGetInfo(result.Data, mod.Id, out var existing) && TryReleaseVersion(existing, out var current) && current >= version) continue;
                // Preserve the game tag in diagnostics, without changing the attachment/version pair.
                var fields = candidate.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.Clone());
                fields["suiteCompatibleGameTag"] = JsonSerializer.SerializeToElement(taggedGame);
                result.Data[mod.Id] = JsonSerializer.SerializeToElement(fields);
                result.Success = true;
            }
        }
        return result;
    }

    private static bool TryReleaseVersion(JsonElement info, out SemVer version)
    {
        version = null!;
        return info.ValueKind == JsonValueKind.Object && !info.TryGetProperty("errorCode", out _) &&
            info.TryGetProperty("recommendedUpgrade", out var value) && value.ValueKind == JsonValueKind.String &&
            SemVer.TryParse(value.GetString() ?? "", out version);
    }

    private async Task<ModDbBatchResult> FetchInstallInformationAsync(IEnumerable<InstalledModInfo> mods, bool includeInstalledVersions, string gameVersion)
    {
        var list = mods.Where(x => !string.IsNullOrWhiteSpace(x.Id)).ToList();
        if (list.Count == 0) return new ModDbBatchResult { Success = true };
        string ids = string.Join(',', list.Select(x => Uri.EscapeDataString(includeInstalledVersions && !string.IsNullOrWhiteSpace(x.Version) ? x.Id + "@" + x.Version : x.Id)));
        string url = $"https://mods.vintagestory.at/api/v2/mods/install-information?ids={ids}&gv={Uri.EscapeDataString(gameVersion)}";
        string lastError = "";
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var response = await http.GetAsync(url).ConfigureAwait(false);
                string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                int status = (int)response.StatusCode;
                if (!response.IsSuccessStatusCode)
                {
                    lastError = $"Mod DB returned HTTP {status}.";
                    if (status == 408 || status == 429 || status >= 500)
                    {
                        await Task.Delay(250 * attempt * attempt).ConfigureAwait(false);
                        continue;
                    }
                    return new ModDbBatchResult { Error = lastError };
                }
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                {
                    lastError = "Mod DB response did not contain a data object.";
                    await Task.Delay(200 * attempt).ConfigureAwait(false);
                    continue;
                }
                var copied = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
                foreach (var prop in data.EnumerateObject()) copied[prop.Name] = prop.Value.Clone();
                return new ModDbBatchResult { Success = true, Data = copied };
            }
            catch (TaskCanceledException ex)
            {
                lastError = "Mod DB request timed out: " + ex.Message;
            }
            catch (HttpRequestException ex)
            {
                lastError = "Mod DB network request failed: " + ex.Message;
            }
            catch (JsonException ex)
            {
                lastError = "Mod DB returned invalid JSON: " + ex.Message;
                break;
            }
            if (attempt < 3) await Task.Delay(250 * attempt * attempt).ConfigureAwait(false);
        }
        return new ModDbBatchResult { Error = lastError.Length > 0 ? lastError : "Mod DB request failed." };
    }

    private static bool TryGetInfo(Dictionary<string, JsonElement> data, string modId, out JsonElement info)
    {
        if (data.TryGetValue(modId, out info)) return true;
        foreach (var pair in data)
        {
            string key = pair.Key;
            int at = key.IndexOf('@');
            if (at > 0) key = key[..at];
            if (string.Equals(key, modId, StringComparison.OrdinalIgnoreCase)) { info = pair.Value; return true; }
        }
        info = default;
        return false;
    }

    private static void ApplyInstallInfo(InstalledModInfo item, ModUpdateEntry entry, JsonElement info)
    {
        if (info.TryGetProperty("recommendedUpgrade", out var rec) && rec.ValueKind == JsonValueKind.String)
        {
            string version = rec.GetString() ?? "";
            entry.RecommendedVersion = version;
            if (version.Length > 0 && (!SemVer.TryParse(version, out _) || !SemVer.TryParse(item.Version, out _)))
            {
                entry.Status = "Unknown";
                entry.Detail = "The installed or recommended version could not be compared safely; automatic replacement is blocked.";
                return;
            }
            if (SemVer.TryParse(version, out var recommended) && SemVer.TryParse(item.Version, out var installed) && recommended > installed)
            {
                entry.Status = "Update";
                entry.Detail = info.TryGetProperty("suiteCompatibleGameTag", out var tag)
                    ? "Update tagged for Vintage Story " + tag.GetString() + "; allowed by your same-minor patch compatibility setting. Dependencies will be checked before staging."
                    : "Compatible update available from the official Vintage Story Mod DB.";
            }
            else { entry.Status = "Current"; entry.Detail = "Installed version is current for the target game version."; }
        }
        else if (info.TryGetProperty("errorCode", out var ec))
        {
            int code = ec.ValueKind == JsonValueKind.Number && ec.TryGetInt32(out int parsed) ? parsed : 0;
            entry.Status = code is 4101 or 4102 ? "Retracted" : "NotFound";
            entry.Detail = DescribeModDbError(ec);
        }
        else
        {
            entry.Status = "Current";
            entry.Detail = "No compatible upgrade was recommended by the Mod DB.";
        }
    }


    private async Task DescribeKnownSuiteListingAsync(ModUpdateEntry entry)
    {
        // A public listing exists, but its runtime-ID install index is currently empty.
        // Listing metadata cannot establish game compatibility, so it never enables installation.
        entry.ModPageUrl = "https://mods.vintagestory.at/show/mod/67501";
        entry.AutoUpdateSupported = false;
        entry.AutoUpdateReason = "The Suite listing is missing from the Mod DB install index; use its listing page until the index is repaired.";
        try
        {
            using var response = await http.GetAsync("https://mods.vintagestory.at/api/mod/11556", HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var bounded = new MemoryStream();
            byte[] buffer = new byte[8192];
            int read;
            while ((read = await input.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                if (bounded.Length + read > 1024 * 1024) throw new InvalidDataException("Listing response exceeds its metadata limit.");
                bounded.Write(buffer, 0, read);
            }
            bounded.Position = 0;
            using var document = JsonDocument.Parse(bounded);
            if (!document.RootElement.TryGetProperty("mod", out var mod) || !mod.TryGetProperty("releases", out var releases) || releases.ValueKind != JsonValueKind.Array) return;
            string latest = "";
            SemVer? latestVersion = null;
            foreach (var release in releases.EnumerateArray())
                if (release.TryGetProperty("modversion", out var value) && value.ValueKind == JsonValueKind.String && SemVer.TryParse(value.GetString() ?? "", out var version) && (latestVersion == null || version > latestVersion))
                { latest = value.GetString()!; latestVersion = version; }
            if (latest.Length == 0) return;
            entry.Status = "Published";
            entry.Detail = "Public Suite listing: " + latest + "; installed " + entry.InstalledVersion + ". Automatic compatibility lookup is unavailable because its runtime-ID index is missing.";
        }
        catch (Exception ex) { entry.Detail += " Public listing check failed: " + ex.GetType().Name + "."; }
    }

    private static string DescribeModDbError(JsonElement errorCode)
    {
        int code = errorCode.ValueKind == JsonValueKind.Number && errorCode.TryGetInt32(out int parsed) ? parsed : 0;
        return code switch
        {
            4001 => "Mod DB could not parse this installed mod identifier/version.",
            4002 => "Mod DB requires a version or target game version for this lookup.",
            4041 => "No indexed release matches this runtime ID and game version. The listing may use earlier game-patch tags. Check its listing page or explicitly allow earlier patches of the same game version.",
            4031 => "The Mod DB could not find an eligible published release for this mod identifier.",
            4032 => "The Mod DB refused the release-retraction override.",
            4101 => "The matching Mod DB release is retracted and is not eligible for automatic update.",
            4102 => "The matching Mod DB release is force-retracted and cannot be downloaded automatically.",
            _ => "Mod DB lookup error " + errorCode.ToString() + "."
        };
    }

    private static ModUpdateEntry CloneEntry(ModUpdateEntry x) => new()
    {
        ModId = x.ModId, InstalledVersion = x.InstalledVersion, RecommendedVersion = x.RecommendedVersion,
        Status = x.Status, Detail = x.Detail, ModPageUrl = x.ModPageUrl,
        AutoUpdateSupported = x.AutoUpdateSupported, AutoUpdateReason = x.AutoUpdateReason,
        Ignored = x.Ignored, IgnoreNote = x.IgnoreNote, IgnoredUnixMilliseconds = x.IgnoredUnixMilliseconds, IgnoredBy = x.IgnoredBy,
        GameRequirement = x.GameRequirement, GameCompatibility = x.GameCompatibility,
        Dependencies = x.Dependencies.Select(d => new ModDependencyInfo { ModId = d.ModId, RequiredVersion = d.RequiredVersion, InstalledVersion = d.InstalledVersion, Status = d.Status, Detail = d.Detail }).ToList(),
        RequiredBy = x.RequiredBy.ToList(), DependencyIssues = x.DependencyIssues
    };

    private sealed class ModDbBatchResult
    {
        public bool Success { get; set; }
        public string Error { get; set; } = "";
        public Dictionary<string, JsonElement> Data { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private async Task DownloadWithLimitAsync(string url, string destination, long maxBytes)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long length && length > maxBytes)
            throw new InvalidDataException($"Download is {length / 1024 / 1024} MiB; the NSE safety limit is {maxBytes / 1024 / 1024} MiB.");
        await using var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        byte[] buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            int read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length)).ConfigureAwait(false);
            if (read <= 0) break;
            total += read;
            if (total > maxBytes) throw new InvalidDataException("Downloaded package exceeded the NSE safety limit.");
            await output.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
        }
        await output.FlushAsync().ConfigureAwait(false);
    }

    private static (bool Success, string Error, Dictionary<string, string> Dependencies) ValidateDownloadedMod(string path, string expectedModId, string expectedVersion)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            if (zip.Entries.Count > 100_000) return (false, "ZIP contains too many entries.", new Dictionary<string, string>());
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long unpacked = 0;
            foreach (var file in zip.Entries)
            {
                string name = file.FullName.Replace('\\', '/');
                if (name.StartsWith('/') || name.Contains(':') || name.Split('/').Any(part => part == ".." || part == ".") || !paths.Add(name))
                    return (false, "ZIP contains an unsafe or duplicate path.", new Dictionary<string, string>());
                if (((file.ExternalAttributes >> 16) & 0xf000) == 0xa000) return (false, "ZIP symbolic links are not supported.", new Dictionary<string, string>());
                if (file.Length > 512L * 1024 * 1024 - unpacked) return (false, "ZIP expands beyond the 512 MiB installation limit.", new Dictionary<string, string>());
                unpacked += file.Length;
            }
            var entry = zip.Entries.SingleOrDefault(x => string.Equals(x.FullName, "modinfo.json", StringComparison.OrdinalIgnoreCase));
            if (entry == null) return (false, "modinfo.json is missing from the ZIP root.", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
            if (entry.Length > 1024 * 1024) return (false, "modinfo.json is unexpectedly large.", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
            using var stream = entry.Open();
            using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            // The game's JSON loader accepts PascalCase manifests (including Floral Zones).
            // Match its casing tolerance, but reject ambiguous duplicate keys before staging.
            var manifest = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in doc.RootElement.EnumerateObject())
                if (!manifest.TryAdd(property.Name, property.Value))
                    return (false, "Duplicate manifest field: " + property.Name, new Dictionary<string, string>());
            string actualId = manifest.TryGetValue("modid", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() ?? "" : "";
            string actualVersion = manifest.TryGetValue("version", out var ver) && ver.ValueKind == JsonValueKind.String ? ver.GetString() ?? "" : "";
            if (!string.Equals(actualId, expectedModId, StringComparison.OrdinalIgnoreCase))
                return (false, $"expected mod ID {expectedModId}, package declares {actualId}.", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
            if (!string.Equals(actualVersion, expectedVersion, StringComparison.OrdinalIgnoreCase))
                return (false, $"expected version {expectedVersion}, package declares {actualVersion}.", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
            var dependencies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (manifest.TryGetValue("dependencies", out var deps))
            {
                if (deps.ValueKind != JsonValueKind.Object) return (false, "dependencies must be an object.", dependencies);
                foreach (var dependency in deps.EnumerateObject())
                {
                    if (!Vintagestory.API.Common.ModInfo.IsValidModID(dependency.Name) || dependency.Value.ValueKind != JsonValueKind.String)
                        return (false, "Invalid dependency ID/version.", dependencies);
                    string minimum = dependency.Value.GetString() ?? "";
                    if (minimum.Length > 0 && minimum != "*" && !SemVer.TryParse(minimum, out _)) return (false, "Invalid minimum version for " + dependency.Name + ".", dependencies);
                    if (!dependencies.TryAdd(dependency.Name, minimum)) return (false, "Duplicate dependency " + dependency.Name + ".", dependencies);
                }
            }
            return (true, "", dependencies);
        }
        catch (InvalidDataException ex) { return (false, "invalid ZIP: " + ex.Message, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)); }
        catch (JsonException ex) { return (false, "invalid modinfo.json: " + ex.Message, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)); }
        catch (Exception ex) { return (false, ex.GetType().Name + ": " + ex.Message, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)); }
    }

    internal static List<string> ValidateDependencyVersions(IReadOnlyDictionary<string, string> dependencies, IReadOnlyDictionary<string, string> available)
    {
        var problems = new List<string>();
        foreach (var dependency in dependencies)
        {
            if (!available.TryGetValue(dependency.Key, out var version)) { problems.Add(dependency.Key + " is missing or disabled"); continue; }
            string minimum = dependency.Value;
            if (minimum.Length == 0 || minimum == "*") continue;
            if (!SemVer.TryParse(minimum, out var required) || !SemVer.TryParse(version, out var installed))
                problems.Add(dependency.Key + " version could not be verified");
            else if (installed < required) problems.Add(dependency.Key + " requires " + minimum + " or newer; available " + version);
        }
        return problems;
    }

    private static string SanitizeFilePart(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string((value ?? "mod").Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
    }

    public void Dispose()
    {
        disposed = true;
        if (tickListenerId >= 0) { try { api.Event.UnregisterGameTickListener(tickListenerId); } catch { } tickListenerId = -1; }
        try { http.Dispose(); } catch { }
        // Pending async checks still release this semaphore after HTTP cancellation.
        // It has no allocated WaitHandle and is reclaimed with the service once those tasks finish.
    }
}
