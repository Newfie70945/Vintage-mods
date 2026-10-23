using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using BlockLogger.Services;
using BlockLogger.Models;
using BlockLogger.Hooks;
using BlockLogger.Config;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace BlockLogger.AnimalLimiter;

public sealed class AnimalPopulationScanState
{
    public long ScanId { get; init; }
    public bool Running { get; init; }
    public bool Completed { get; init; }
    public int ProgressPercent { get; init; }
    public int ProcessedEntities { get; init; }
    public int TotalEntities { get; init; }
    public int ProcessedRegions { get; init; }
    public int TotalRegions { get; init; }
    public long StartedUnixMilliseconds { get; init; }
    public long DurationMilliseconds { get; init; }
    public string Mode { get; init; } = "";
    public string Error { get; init; } = "";
    public List<string> Lines { get; init; } = new();
}

public sealed class AnimalLimiterRuleInfo
{
    public string Pattern { get; init; } = "";
    public int Limit { get; init; }
    public int RadiusChunks { get; init; }
    public bool UsesDefaultRadius { get; init; }
    public string GenerationMode { get; init; } = "Inherit";
    public int MinimumProtectedGeneration { get; init; } = 1;
    public int RegisteredMatches { get; init; }
}

public sealed class AnimalLimiterRuleSnapshot
{
    public bool Available { get; init; } = true;
    public bool Enabled { get; init; }
    public bool LimitAllEntities { get; init; }
    public int DefaultLimit { get; init; }
    public int DefaultRadiusChunks { get; init; }
    public List<string> Exclusions { get; init; } = new();
    public List<AnimalLimiterRuleInfo> Rules { get; init; } = new();
}

public sealed class AnimalLimiterRecommendationInfo
{
    public string Pattern { get; init; } = "";
    public int SuggestedLimit { get; init; }
    public int RadiusChunks { get; init; }
    public int RegionsObserved { get; init; }
    public int TotalEligible { get; init; }
    public int PeakEligible { get; init; }
    public double AverageEligible { get; init; }
    public int MedianEligible { get; init; }
    public int UpperQuartileEligible { get; init; }
    public int EstimatedWouldRemoveNow { get; init; }
    public bool HasEffectiveRule { get; init; }
    public string EffectiveRule { get; init; } = "";
    public int? EffectiveLimit { get; init; }
    public string Confidence { get; init; } = "Low";
    public string Reason { get; init; } = "";
}

public sealed class AnimalLimiterRecommendationSnapshot
{
    public long ScanId { get; init; }
    public bool Ready { get; init; }
    public string Error { get; init; } = "";
    public string Note { get; init; } = "";
    public List<AnimalLimiterRecommendationInfo> Recommendations { get; init; } = new();
}

public sealed class AnimalLimiterSystem : ModSystem
{
    public static AnimalLimiterSystem? Current { get; private set; }
    private const string ConfigFile = "NewfiesAnimalLimiter.json";
    private const string LegacyConfigFile = "animal_limiter.json";
    private const string HistoryFile = "NewfiesAnimalLimiter.removals.json";

    private ICoreServerAPI sapi = null!;
    private AnimalLimiterConfig config = new();
    private List<RemovalRecord> history = new();
    private int removalSequence;
    private bool historyDirty;
    private readonly object csvLock = new();
    private readonly Dictionary<BucketKey, EntityBucket> entityBuckets = new();
    private readonly Dictionary<long, BucketKey> entityBucketIndex = new();
    private readonly Dictionary<string, ResolvedRuleCacheEntry> resolvedRuleCache = new(StringComparer.OrdinalIgnoreCase);
    private Timer? reconciliationTimer;
    private int reconciliationQueued;
    private long populationScanSequence;
    private long populationScanListenerId = -1;
    private PopulationScanJob? populationScanJob;
    private bool usingSharedLifecycleBus;
    private const int PopulationScanMaxRegionalRows = 500;
    private const double PopulationScanTickBudgetMs = 4.0;
    private const int MaxResolvedRuleCacheEntries = 4096;

    public int HealthTrackedRegionCount => entityBuckets.Count;
    public int HealthTrackedEntityCount => entityBucketIndex.Count;
    public AnimalLimiterConfig Config => config;

    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Server;

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;
        var suiteFeatures = SuiteFeatureConfig.Load(api);
        if (!suiteFeatures.AnimalLimiter)
        {
            api.Logger.Notification("[Newfie's Animal Limiter] Entire subsystem disabled by {0}; no entity observer, timer, command, or history service registered.", SuiteFeatureConfig.ConfigFile);
            return;
        }

        Current = this;
        ValidateRuntimeApiShape();
        LoadConfig();
        LoadHistory();

        // Old registration API is intentionally used for compatibility with the v1.1 command style.
#pragma warning disable CS0618
        sapi.RegisterCommand(
            "animallimit",
            "Configure and inspect Newfie's Animal Limiter",
            "/animallimit help",
            HandleCommand,
            "controlserver"
        );
#pragma warning restore CS0618

        sapi.Event.PlayerJoin += OnPlayerJoin;
        usingSharedLifecycleBus = suiteFeatures.BlockLogger;
        if (usingSharedLifecycleBus)
        {
            EntityHooks.EntityObserved += ObserveEntity;
        }
        else
        {
            // BlockLogger can be master-disabled independently. In that case Animal Limiter
            // owns the minimum lifecycle hooks it needs instead of silently losing enforcement.
            sapi.Event.OnEntitySpawn += OnFallbackEntitySpawn;
            sapi.Event.OnEntityDeath += OnFallbackEntityDeath;
            sapi.Event.OnEntityDespawn += OnFallbackEntityDespawn;
        }

        // Reconciliation is only a safety net for pre-existing entities or missed lifecycle events.
        // Keep the timer dormant while the limiter is disabled so preview-only installs have no
        // recurring world task at idle. Enabling the limiter arms it through RestartReconciliationTimer().
        reconciliationTimer = new Timer(
            _ => QueueReconciliation(),
            null,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
        RestartReconciliationTimer(initialStart: true);

        sapi.Logger.Notification("[Newfie's Animal Limiter] incremental mode started. Enabled={0}, LimitAll={1}, default={2}, radius={3} chunks, reconciliation={4}s",
            config.Enabled == true, config.LimitAllEntities, config.DefaultPerEntityLimit, config.RadiusChunks, config.SweepIntervalSeconds);
    }

    public override void Dispose()
    {
        if (ReferenceEquals(Current, this)) Current = null;
        reconciliationTimer?.Dispose();
        reconciliationTimer = null;
        StopPopulationScanListener();
        populationScanJob = null;
        if (usingSharedLifecycleBus)
        {
            EntityHooks.EntityObserved -= ObserveEntity;
        }
        else if (sapi != null)
        {
            sapi.Event.OnEntitySpawn -= OnFallbackEntitySpawn;
            sapi.Event.OnEntityDeath -= OnFallbackEntityDeath;
            sapi.Event.OnEntityDespawn -= OnFallbackEntityDespawn;
        }
        if (sapi != null) sapi.Event.PlayerJoin -= OnPlayerJoin;
        entityBuckets.Clear();
        entityBucketIndex.Clear();
        resolvedRuleCache.Clear();
        if (historyDirty) SaveHistory();
        base.Dispose();
    }

    private readonly struct ResolvedRuleCacheEntry
    {
        public ResolvedRuleCacheEntry(bool hasLimit, int limit, string rule)
        {
            HasLimit = hasLimit;
            Limit = limit;
            Rule = rule;
        }

        public bool HasLimit { get; }
        public int Limit { get; }
        public string Rule { get; }
    }

    private readonly struct BucketKey : IEquatable<BucketKey>
    {
        public BucketKey(int x, int z, string code, int radiusChunks)
        {
            X = x;
            Z = z;
            Code = code;
            RadiusChunks = radiusChunks;
        }

        public int X { get; }
        public int Z { get; }
        public string Code { get; }
        public int RadiusChunks { get; }

        public bool Equals(BucketKey other) =>
            X == other.X && Z == other.Z && RadiusChunks == other.RadiusChunks &&
            string.Equals(Code, other.Code, StringComparison.OrdinalIgnoreCase);

        public override bool Equals(object? obj) => obj is BucketKey other && Equals(other);

        public override int GetHashCode() =>
            HashCode.Combine(X, Z, RadiusChunks, StringComparer.OrdinalIgnoreCase.GetHashCode(Code));
    }

    private sealed class EntityBucket
    {
        // Keep only ids here. A missed despawn callback must never root an EntityAgent
        // (or anything reachable from it) until the next reconciliation pass.
        public readonly HashSet<long> EntityIds = new();
    }

    private EntityAgent? ResolveLoadedAgent(long entityId)
    {
        try
        {
            return sapi.World.LoadedEntities.TryGetValue(entityId, out var entity)
                ? entity as EntityAgent
                : null;
        }
        catch
        {
            return null;
        }
    }

    private void OnFallbackEntitySpawn(Entity entity) => ObserveEntity(entity, LogActionType.EntitySpawn);
    private void OnFallbackEntityDeath(Entity entity, DamageSource damageSource) => ObserveEntity(entity, LogActionType.EntityDeath);
    private void OnFallbackEntityDespawn(Entity entity, EntityDespawnData despawnData) => ObserveEntity(entity, LogActionType.EntityDespawn);

    public void ObserveEntity(Entity entity, LogActionType action)
    {
        if (config.Enabled != true) return;
        if (entity is not EntityAgent agent || entity is EntityPlayer) return;

        switch (action)
        {
            case LogActionType.EntitySpawn:
                TrackSpawn(agent, evaluate: true);
                break;

            case LogActionType.EntityDeath:
            case LogActionType.EntityDespawn:
                UntrackEntity(agent.EntityId);
                break;
        }
    }

    private void TrackSpawn(EntityAgent entity, bool evaluate)
    {
        if (!entity.Alive) return;
        string code = CodeOf(entity);
        if (!ResolveLimitForCode(code, null, out var matchedRule).HasValue) return;
        int radiusChunks = ResolveRadiusForRule(matchedRule);
        if (!TryGetBucketKey(entity, code, radiusChunks, out var key)) return;

        // Defensive against a mod re-emitting spawn for the same entity or an entity moving
        // between buckets before reconciliation.
        if (entityBucketIndex.TryGetValue(entity.EntityId, out var previous) && !previous.Equals(key))
            RemoveFromBucket(previous, entity.EntityId);

        if (!entityBuckets.TryGetValue(key, out var bucket))
        {
            bucket = new EntityBucket();
            entityBuckets[key] = bucket;
        }

        bucket.EntityIds.Add(entity.EntityId);
        entityBucketIndex[entity.EntityId] = key;

        if (evaluate) EvaluateBucket(key, bucket);
    }

    private void UntrackEntity(long entityId)
    {
        if (!entityBucketIndex.Remove(entityId, out var key)) return;
        RemoveFromBucket(key, entityId);
    }

    private void RemoveFromBucket(BucketKey key, long entityId)
    {
        if (!entityBuckets.TryGetValue(key, out var bucket)) return;
        bucket.EntityIds.Remove(entityId);
        if (bucket.EntityIds.Count == 0) entityBuckets.Remove(key);
    }

    private bool TryGetBucketKey(Entity entity, string code, int radiusChunks, out BucketKey key)
    {
        key = default;
        if (!EntityPositionResolver.TryGetCoordinates(entity, out var x, out _, out var z, out _)) return false;

        radiusChunks = Math.Clamp(radiusChunks, 0, 32);
        int widthChunks = radiusChunks * 2 + 1;
        int regionBlocks = Math.Max(GlobalConstants.ChunkSize, widthChunks * GlobalConstants.ChunkSize);
        key = new BucketKey(
            FloorDiv((int)Math.Floor(x), regionBlocks),
            FloorDiv((int)Math.Floor(z), regionBlocks),
            code,
            radiusChunks);
        return true;
    }

    private void EvaluateBucket(BucketKey key, EntityBucket bucket)
    {
        int? limit = ResolveLimitForCode(key.Code, null, out string rule);
        if (!limit.HasValue || ResolveRadiusForRule(rule) != key.RadiusChunks)
        {
            DropBucket(key, bucket);
            return;
        }

        // Cheap gate first. Protected entities never consume the cull quota, but if the total
        // population is not above the limit then eligible population cannot be above it either.
        if (bucket.EntityIds.Count <= limit.Value) return;

        var stale = new List<long>();
        var eligible = new List<EntityAgent>();

        foreach (var entityId in bucket.EntityIds)
        {
            var entity = ResolveLoadedAgent(entityId);
            if (entity == null || !entity.Alive)
            {
                stale.Add(entityId);
                continue;
            }

            // Movement across a fixed region boundary is corrected by reconciliation. Avoid a
            // position read here unless we actually need to evaluate an over-limit bucket.
            if (IsProtected(entity, out _) || IsInsideHardPlayerProtection(entity)) continue;
            eligible.Add(entity);
        }

        foreach (var id in stale)
        {
            bucket.EntityIds.Remove(id);
            entityBucketIndex.Remove(id);
        }

        if (eligible.Count <= limit.Value)
        {
            if (bucket.EntityIds.Count == 0) entityBuckets.Remove(key);
            return;
        }

        int removeCount = eligible.Count - limit.Value;
        var ordered = config.KeepNearestToPlayers
            ? eligible.OrderByDescending(NearestPlayerDistanceSq).ToList()
            : eligible.OrderByDescending(e => e.EntityId).ToList();

        foreach (var entity in ordered.Take(removeCount))
        {
            // Remove from tracking first so synchronous death/despawn callbacks are idempotent.
            UntrackEntity(entity.EntityId);
            RemoveEntity(entity, rule, limit.Value, eligible.Count);
        }

        if (historyDirty) SaveHistory();
    }

    private void DropBucket(BucketKey key, EntityBucket bucket)
    {
        foreach (var id in bucket.EntityIds)
            entityBucketIndex.Remove(id);
        entityBuckets.Remove(key);
    }

    private void QueueReconciliation()
    {
        if (config.Enabled != true) return;
        if (Interlocked.Exchange(ref reconciliationQueued, 1) != 0) return;

        try
        {
            sapi.Event.EnqueueMainThreadTask(() =>
            {
                try { ReconcileBuckets(); }
                catch (Exception e) { sapi.Logger.Warning("[AnimalLimiter] Reconciliation failed: {0}: {1}", e.GetType().Name, e.Message); }
                finally { Interlocked.Exchange(ref reconciliationQueued, 0); }
            }, "animallimiter-reconcile");
        }
        catch
        {
            Interlocked.Exchange(ref reconciliationQueued, 0);
        }
    }

    private void ReconcileBuckets()
    {
        if (config.Enabled != true)
        {
            entityBuckets.Clear();
            entityBucketIndex.Clear();
            return;
        }

        entityBuckets.Clear();
        entityBucketIndex.Clear();

        var agents = GatherLoadedAgentsNearPlayers();
        foreach (var agent in agents)
            TrackSpawn(agent, evaluate: false);

        foreach (var pair in entityBuckets.ToList())
            EvaluateBucket(pair.Key, pair.Value);
    }


    private void RestartReconciliationTimer(bool initialStart = false)
    {
        if (reconciliationTimer == null) return;
        if (config.Enabled != true)
        {
            reconciliationTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            return;
        }

        var seconds = Math.Max(60, config.SweepIntervalSeconds);
        var due = initialStart ? TimeSpan.FromSeconds(30) : TimeSpan.FromSeconds(seconds);
        reconciliationTimer.Change(due, TimeSpan.FromSeconds(seconds));
    }

    private void LoadConfig()
    {
        var existing = ModConfigRecovery.LoadOrNull<AnimalLimiterConfig>(
            sapi,
            ConfigFile,
            "AnimalLimiter");
        if (existing != null)
        {
            // Preserve established servers: configs created before Enabled existed were
            // already actively limiting, so upgrading keeps them enabled.
            if (!existing.Enabled.HasValue) existing.Enabled = true;
            config = existing;
        }
        else
        {
            var migrated = MigrateLegacyConfig();
            if (migrated != null)
            {
                migrated.Enabled = true;
                config = migrated;
            }
            else
            {
                // Safe fresh install. Server owners must explicitly enable after reviewing settings.
                config = new AnimalLimiterConfig
                {
                    Enabled = false,
                    DefaultPerEntityLimit = 100
                };
            }
        }

        config.SchemaVersion = Math.Max(AnimalLimiterConfig.CurrentSchemaVersion, config.SchemaVersion);
        NormalizeConfig();
        SaveConfig();
    }

    private AnimalLimiterConfig? MigrateLegacyConfig()
    {
        var legacy = ModConfigRecovery.LoadOrNull<LegacyAnimalLimiterConfig>(
            sapi,
            LegacyConfigFile,
            "AnimalLimiter");
        if (legacy == null) return null;

        var migrated = new AnimalLimiterConfig
        {
            LimitAllEntities = legacy.IncludeCodeStartsWith?.Any(x => string.IsNullOrEmpty(x)) == true,
            DefaultPerEntityLimit = Math.Max(0, legacy.PerCodeLimitPerChunk),
            RadiusChunks = 0,
            ProtectPets = legacy.ProtectTamedOrOwned,
            KeepNearestToPlayers = legacy.KeepNearestToPlayers,
            LogActions = legacy.LogActions,
            LogToCsv = legacy.LogToCsv,
            CsvDirName = string.IsNullOrWhiteSpace(legacy.CsvDirName) ? "AnimalLimiterLogs" : legacy.CsvDirName,
            CsvFilePrefix = string.IsNullOrWhiteSpace(legacy.CsvFilePrefix) ? "animal-removals" : legacy.CsvFilePrefix
        };

        if (legacy.PerCodeOverrides != null)
        {
            foreach (var kv in legacy.PerCodeOverrides) migrated.Rules[NormalizeLegacyPrefix(kv.Key)] = Math.Max(0, kv.Value);
        }
        if (legacy.IncludeCodeStartsWith != null)
        {
            foreach (string prefix in legacy.IncludeCodeStartsWith.Where(x => !string.IsNullOrWhiteSpace(x)))
            {
                string pat = NormalizeLegacyPrefix(prefix);
                if (!migrated.Rules.ContainsKey(pat)) migrated.Rules[pat] = migrated.DefaultPerEntityLimit;
            }
        }
        if (legacy.ProtectedCodeStartsWith != null)
        {
            foreach (string prefix in legacy.ProtectedCodeStartsWith.Where(x => !string.IsNullOrWhiteSpace(x)))
                migrated.ExcludeRules.Add(NormalizeLegacyPrefix(prefix));
        }

        sapi.Logger.Notification("[AnimalLimiter] Migrated legacy {0} settings into {1}. Legacy radius semantics are preserved as radius 0 until changed.", LegacyConfigFile, ConfigFile);
        return migrated;
    }

    private static string NormalizeLegacyPrefix(string prefix)
    {
        string p = NormalizePattern(prefix);
        return p.Contains('*') ? p : p + "*";
    }

    private void NormalizeConfig()
    {
        config.SchemaVersion = Math.Max(AnimalLimiterConfig.CurrentSchemaVersion, config.SchemaVersion);
        config.DefaultPerEntityLimit = Math.Max(0, config.DefaultPerEntityLimit);
        config.RadiusChunks = Math.Clamp(config.RadiusChunks, 0, 32);
        config.SweepIntervalSeconds = Math.Clamp(config.SweepIntervalSeconds, 60, 3600);
        config.PlayerProtectionRadiusBlocks = Math.Max(0, config.PlayerProtectionRadiusBlocks);
        config.MaxRemovalHistory = Math.Clamp(config.MaxRemovalHistory, 10, 100000);
        config.RemovalHistoryDays = Math.Clamp(config.RemovalHistoryDays, 1, 3650);
        config.Enabled ??= false;
        config.Rules ??= new Dictionary<string, int>();
        config.RuleRadiusChunks ??= new Dictionary<string, int>();
        config.RuleGenerationPolicies ??= new Dictionary<string, AnimalGenerationPolicy>();
        config.ExcludeRules ??= new List<string>();
        config.OwnerAttributeKeys ??= new List<string>();
        config.TamedAttributeKeys ??= new List<string>();
        config.NameAttributeKeys ??= new List<string>();
        config.GenerationAttributeKeys ??= new List<string>();

        config.GenerationFilter = (config.GenerationFilter ?? "ProtectBred").ToLowerInvariant() switch
        {
            "off" => "Off",
            "wildonly" => "WildOnly",
            "bredonly" => "BredOnly",
            "protectbred" => "ProtectBred",
            _ => "ProtectBred"
        };

        config.Rules = config.Rules
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Key))
            .GroupBy(kv => NormalizePattern(kv.Key), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => Math.Max(0, g.Last().Value), StringComparer.OrdinalIgnoreCase);

        config.RuleRadiusChunks = config.RuleRadiusChunks
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Key))
            .Select(kv => new KeyValuePair<string, int>(NormalizePattern(kv.Key), Math.Clamp(kv.Value, 0, 32)))
            .Where(kv => config.Rules.ContainsKey(kv.Key))
            .GroupBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.OrdinalIgnoreCase);

        config.RuleGenerationPolicies = config.RuleGenerationPolicies
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Key) && kv.Value != null)
            .Select(kv => new KeyValuePair<string, AnimalGenerationPolicy>(NormalizePattern(kv.Key), new AnimalGenerationPolicy {
                Mode = AnimalGenerationPolicy.Canonical(kv.Value.Mode) ?? "Inherit",
                MinimumProtectedGeneration = Math.Clamp(kv.Value.MinimumProtectedGeneration, 1, 1000000) }))
            .Where(kv => config.Rules.ContainsKey(kv.Key) && kv.Value.Mode != "Inherit")
            .GroupBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.OrdinalIgnoreCase);

        config.ExcludeRules = config.ExcludeRules
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(NormalizePattern)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        // Players are never valid limiter targets. Keep this safety exclusion present even
        // on older/custom configs that omitted the newer default entry.
        if (!config.ExcludeRules.Any(x => x.Equals("game:player", StringComparison.OrdinalIgnoreCase)))
            config.ExcludeRules.Add("game:player");
    }

    private void SaveConfig()
    {
        // Any rule/protection setting can affect the resolved result for an entity code.
        // Clearing this small cache on config writes keeps the hot path O(1) without stale policy.
        resolvedRuleCache.Clear();
        BlockLogger.Services.SuiteConfigStorage.Store(sapi, config, ConfigFile);
    }

    private void LoadHistory()
    {
        history = ModConfigRecovery.LoadOrNull<List<RemovalRecord>>(
            sapi,
            HistoryFile,
            "AnimalLimiterHistory") ?? new List<RemovalRecord>();
        removalSequence = history.Select(r => ParseRemovalSequence(r.RemovalId)).DefaultIfEmpty(0).Max();
        if (CleanupHistory()) SaveHistory();
    }

    private void SaveHistory()
    {
        BlockLogger.Services.SuiteConfigStorage.Store(sapi, history, HistoryFile);
        historyDirty = false;
    }

    private bool CleanupHistory()
    {
        int before = history.Count;
        long cutoff = DateTimeOffset.UtcNow.AddDays(-config.RemovalHistoryDays).ToUnixTimeSeconds();
        history.RemoveAll(r => r.RemovedUnixSeconds < cutoff);
        if (history.Count > config.MaxRemovalHistory)
        {
            history = history.OrderByDescending(r => r.RemovedUnixSeconds)
                .Take(config.MaxRemovalHistory).OrderBy(r => r.RemovedUnixSeconds).ToList();
        }
        bool changed = history.Count != before;
        if (changed) historyDirty = true;
        return changed;
    }

    private void OnPlayerJoin(IServerPlayer _)
    {
        if (CleanupHistory()) SaveHistory();
    }

    private void ValidateRuntimeApiShape()
    {
        if (EntityPositionResolver.RuntimeMemberName == "unavailable")
            throw new InvalidOperationException("Animal Limiter could not resolve a runtime entity position member.");
    }

    private void HandleCommand(IServerPlayer player, int groupId, CmdArgs args)
    {
        string sub = (args.PopWord() ?? "help").ToLowerInvariant();
        try
        {
            switch (sub)
            {
                case "help": SendHelp(player, groupId); break;
                case "status": SendStatus(player, groupId); break;
                case "all": CmdAll(player, groupId, args); break;
                case "default": CmdDefault(player, groupId, args); break;
                case "radius": CmdRadius(player, groupId, args); break;
                case "interval": CmdInterval(player, groupId, args); break;
                case "set": CmdSet(player, groupId, args); break;
                case "target":
                case "look": CmdTarget(player, groupId, args); break;
                case "remove": CmdRemove(player, groupId, args); break;
                case "exclude": CmdExclude(player, groupId, args); break;
                case "include": CmdInclude(player, groupId, args); break;
                case "protect": CmdProtect(player, groupId, args); break;
                case "generation": CmdGeneration(player, groupId, args); break;
                case "generationrule": CmdGenerationRule(player, groupId, args); break;
                case "list": CmdList(player, groupId); break;
                case "find": CmdFind(player, groupId, args); break;
                case "matches": CmdMatches(player, groupId, args); break;
                case "top": CmdTop(player, groupId, args); break;
                case "preview": CmdPreview(player, groupId, args); break;
                case "cull": CmdCull(player, groupId); break;
                case "history": CmdHistory(player, groupId, args); break;
                case "return": CmdReturn(player, groupId, args); break;
                case "reload": LoadConfig(); RestartReconciliationTimer(); InvalidateBucketsAndReconcile(); Msg(player, groupId, "Config reloaded."); break;
                default: Msg(player, groupId, "Unknown subcommand. Use /animallimit help"); break;
            }
        }
        catch (Exception e)
        {
            sapi.Logger.Error("[AnimalLimiter] Command error: {0}", e);
            Msg(player, groupId, "Command failed: " + e.Message);
        }
    }

    private void SendHelp(IServerPlayer p, int g)
    {
        Msg(p, g,
            "Animal Limiter commands:\n" +
            "/animallimit status\n" +
            "/animallimit all on|off\n" +
            "/animallimit default <limit>\n" +
            "/animallimit radius <chunks>\n" +
            "/animallimit interval <reconciliation-seconds>\n" +
            "/animallimit set <entityid|wildcard> <limit> [radius]\n" +
            "/animallimit target <limit> [radius]  (limit the entity you are looking at)\n" +
            "/animallimit remove <pattern>\n" +
            "/animallimit exclude <pattern> | include <pattern>\n" +
            "/animallimit protect pets|farm|named on|off\n" +
            "/animallimit generation off|wildonly|bredonly|protectbred\n" +
            "/animallimit generationrule <stored-rule> inherit|off|protectfrom [generation]\n" +
            "/animallimit find <text> | matches <pattern>\n" +
            "/animallimit top [pattern]\n" +
            "/animallimit preview [pattern] [limit]\n" +
            "/animallimit cull\n" +
            "/animallimit history [count]\n" +
            "/animallimit return last|<removal-id>");
    }

    private void SendStatus(IServerPlayer p, int g)
    {
        Msg(p, g, $"Limit all entity types: {config.LimitAllEntities}; default/type: {config.DefaultPerEntityLimit}; radius: {config.RadiusChunks} chunks; reconcile: {config.SweepIntervalSeconds}s; rules: {config.Rules.Count}; excludes: {config.ExcludeRules.Count}; protect pets={config.ProtectPets}, farm={config.ProtectFarmAnimals}, named={config.ProtectNamedEntities}; generation={config.GenerationFilter}");
    }

    private void CmdAll(IServerPlayer p, int g, CmdArgs a)
    {
        string v = (a.PopWord() ?? "").ToLowerInvariant();
        if (v != "on" && v != "off") { Msg(p, g, "Usage: /animallimit all on|off"); return; }
        config.LimitAllEntities = v == "on";
        SaveConfig(); Msg(p, g, "Universal per-entity limiting is now " + v.ToUpperInvariant() + ".");
    }

    private void CmdDefault(IServerPlayer p, int g, CmdArgs a)
    {
        if (!int.TryParse(a.PopWord(), out int n) || n < 0) { Msg(p, g, "Usage: /animallimit default <0+>"); return; }
        config.DefaultPerEntityLimit = n; SaveConfig(); Msg(p, g, $"Default per-entity limit set to {n}.");
    }

    private void CmdRadius(IServerPlayer p, int g, CmdArgs a)
    {
        if (!int.TryParse(a.PopWord(), out int n) || n < 0 || n > 32) { Msg(p, g, "Usage: /animallimit radius <0-32 chunks>"); return; }
        config.RadiusChunks = n;
        SaveConfig();
        InvalidateBucketsAndReconcile();
        Msg(p, g, $"Default radius set to {n} chunks ({n * 2 + 1}x{n * 2 + 1} chunk regions).");
    }

    private void CmdInterval(IServerPlayer p, int g, CmdArgs a)
    {
        if (!int.TryParse(a.PopWord(), out int n) || n < 60) { Msg(p, g, "Usage: /animallimit interval <reconciliation seconds, minimum 60>"); return; }
        config.SweepIntervalSeconds = Math.Min(3600, n); SaveConfig(); RestartReconciliationTimer(); Msg(p, g, $"Reconciliation interval set to {config.SweepIntervalSeconds}s. Normal limiting remains event-driven.");
    }

    private void CmdSet(IServerPlayer p, int g, CmdArgs a)
    {
        string pattern = NormalizePattern(a.PopWord());
        if (string.IsNullOrWhiteSpace(pattern) || !int.TryParse(a.PopWord(), out int n) || n < 0)
        { Msg(p, g, "Usage: /animallimit set <entityid|wildcard> <limit> [radius]"); return; }

        int radius = config.RadiusChunks;
        string? radiusWord = a.PopWord();
        if (!string.IsNullOrWhiteSpace(radiusWord) &&
            (!int.TryParse(radiusWord, out radius) || radius < 0 || radius > 32))
        {
            Msg(p, g, "Radius must be 0-32 chunks.");
            return;
        }

        Msg(p, g, SetRuleFromGui(pattern, n, radius));
    }

    private void CmdTarget(IServerPlayer p, int g, CmdArgs a)
    {
        if (!int.TryParse(a.PopWord(), out int limit) || limit < 0)
        {
            Msg(p, g, "Usage: /animallimit target <limit> [radius]");
            return;
        }

        int radius = config.RadiusChunks;
        string? radiusWord = a.PopWord();
        if (!string.IsNullOrWhiteSpace(radiusWord) &&
            (!int.TryParse(radiusWord, out radius) || radius < 0 || radius > 32))
        {
            Msg(p, g, "Radius must be 0-32 chunks.");
            return;
        }

        Msg(p, g, SetTargetRule(p, limit, radius));
    }

    public string SetTargetRule(IServerPlayer player, int limit, int radiusChunks)
    {
        limit = Math.Max(0, limit);
        radiusChunks = Math.Clamp(radiusChunks, 0, 32);
        var entity = player.CurrentEntitySelection?.Entity;
        if (entity == null) return "Look directly at an entity first.";
        if (entity is EntityPlayer) return "Players cannot be added to the animal limiter.";

        string code = CodeOf(entity);
        if (string.IsNullOrWhiteSpace(code) || code == "unknown:unknown") return "Could not resolve that entity's code.";

        config.Rules[code] = limit;
        config.RuleRadiusChunks[code] = radiusChunks;
        SaveConfig();
        InvalidateBucketsAndReconcile();
        return $"Rule set from target: {code} | limit {limit} | radius {radiusChunks} chunks.";
    }

    public string SetRuleFromGui(string? pattern, int limit, int radiusChunks) => SetRuleFromGui(pattern, limit, radiusChunks, null, 1);

    public string SetRuleFromGui(string? pattern, int limit, int radiusChunks, string? generationMode, int minimumProtectedGeneration)
    {
        pattern = NormalizePattern(pattern);
        if (string.IsNullOrWhiteSpace(pattern)) return "Enter an entity code or wildcard pattern.";
        limit = Math.Max(0, limit);
        radiusChunks = Math.Clamp(radiusChunks, 0, 32);
        string? mode = generationMode == null ? null : AnimalGenerationPolicy.Canonical(generationMode);
        if (generationMode != null && mode == null) return "Invalid generation policy. Use Inherit, Off or ProtectFromGeneration.";
        if (mode == "ProtectFromGeneration" && (minimumProtectedGeneration < 1 || minimumProtectedGeneration > 1000000)) return "Invalid protected generation; use 1-1000000.";
        config.Rules[pattern] = limit;
        config.RuleRadiusChunks[pattern] = radiusChunks;
        if (mode == "Inherit") config.RuleGenerationPolicies.Remove(pattern);
        else if (mode != null) config.RuleGenerationPolicies[pattern] = new AnimalGenerationPolicy { Mode = mode, MinimumProtectedGeneration = minimumProtectedGeneration };
        SaveConfig();
        InvalidateBucketsAndReconcile();
        return $"Rule set: {pattern} | limit {limit} | radius {radiusChunks} chunks. Matches {CountRegisteredMatches(pattern)} registered entity types.";
    }

    public string RemoveRuleFromGui(string? pattern)
    {
        pattern = NormalizePattern(pattern);
        if (string.IsNullOrWhiteSpace(pattern)) return "Enter the exact stored rule to remove.";
        bool removed = config.Rules.Remove(pattern);
        config.RuleRadiusChunks.Remove(pattern);
        config.RuleGenerationPolicies.Remove(pattern);
        if (removed)
        {
            SaveConfig();
            InvalidateBucketsAndReconcile();
        }
        return removed ? $"Removed rule {pattern}." : $"No exact stored rule named {pattern}.";
    }

    public string AddExclusionFromGui(string? pattern)
    {
        pattern = NormalizePattern(pattern);
        if (string.IsNullOrWhiteSpace(pattern)) return "Enter an entity code or wildcard pattern to exclude.";
        if (pattern.Equals("game:player", StringComparison.OrdinalIgnoreCase))
            return "game:player is already permanently protected by the default exclusion.";

        if (config.ExcludeRules.Any(x => x.Equals(pattern, StringComparison.OrdinalIgnoreCase)))
            return $"Exclusion {pattern} already exists.";

        config.ExcludeRules.Add(pattern);
        SaveConfig();
        InvalidateBucketsAndReconcile();
        return $"Excluded {pattern} from Animal Limiter enforcement. Matches {CountRegisteredMatches(pattern)} registered entity types.";
    }

    public string RemoveExclusionFromGui(string? pattern)
    {
        pattern = NormalizePattern(pattern);
        if (string.IsNullOrWhiteSpace(pattern)) return "Enter the exact stored exclusion to remove.";
        if (pattern.Equals("game:player", StringComparison.OrdinalIgnoreCase))
            return "The built-in game:player exclusion cannot be removed from the WebPanel.";

        int removed = config.ExcludeRules.RemoveAll(x => x.Equals(pattern, StringComparison.OrdinalIgnoreCase));
        if (removed > 0)
        {
            SaveConfig();
            InvalidateBucketsAndReconcile();
        }
        return removed > 0 ? $"Removed exclusion {pattern}." : $"No exact exclusion named {pattern}.";
    }

    public AnimalLimiterRuleSnapshot GetWebRuleSnapshot()
    {
        var rules = config.Rules
            .OrderByDescending(x => Specificity(x.Key))
            .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(x =>
            {
                bool explicitRadius = config.RuleRadiusChunks.TryGetValue(x.Key, out int storedRadius);
                int radius = explicitRadius ? Math.Clamp(storedRadius, 0, 32) : Math.Clamp(config.RadiusChunks, 0, 32);
                return new AnimalLimiterRuleInfo
                {
                    Pattern = x.Key,
                    Limit = x.Value,
                    RadiusChunks = radius,
                    UsesDefaultRadius = !explicitRadius,
                    GenerationMode = config.RuleGenerationPolicies.TryGetValue(x.Key, out var gp) ? gp.Mode : "Inherit",
                    MinimumProtectedGeneration = gp?.MinimumProtectedGeneration ?? 1,
                    RegisteredMatches = CountRegisteredMatches(x.Key)
                };
            })
            .ToList();

        return new AnimalLimiterRuleSnapshot
        {
            Enabled = config.Enabled == true,
            LimitAllEntities = config.LimitAllEntities,
            DefaultLimit = config.DefaultPerEntityLimit,
            DefaultRadiusChunks = config.RadiusChunks,
            Exclusions = config.ExcludeRules.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(),
            Rules = rules
        };
    }

    public string PreviewFromGui(string? pattern, int? limit, int radiusChunks)
    {
        pattern = NormalizePattern(pattern);
        PreviewOverride? preview = string.IsNullOrWhiteSpace(pattern)
            ? null
            : new PreviewOverride(pattern, limit, Math.Clamp(radiusChunks, 0, 32));
        var result = SweepAllPlayerRegions(true, preview);
        return $"Preview: scanned {result.Scanned}; regions {result.Regions}; over-limit groups {result.OverLimitGroups}; would remove {result.WouldRemove}; protected/skipped {result.Protected}. Nothing was removed.";
    }

    public string CullFromGui()
    {
        var result = SweepAllPlayerRegions(false, null);
        return $"Cull complete: scanned {result.Scanned}; removed {result.Removed}; protected/skipped {result.Protected}; regions {result.Regions}.";
    }

    public bool GuiEnabled => config.Enabled == true;
    public int GuiRadiusChunks => config.RadiusChunks;
    public int GuiDefaultPerEntityLimit => config.DefaultPerEntityLimit;
    public int GuiPlayerProtectionRadiusBlocks => config.PlayerProtectionRadiusBlocks;
    public int GuiReconciliationSeconds => config.SweepIntervalSeconds;
    public bool GuiLimitAllEntities => config.LimitAllEntities;
    public bool GuiProtectPets => config.ProtectPets;
    public bool GuiProtectFarmAnimals => config.ProtectFarmAnimals;
    public bool GuiProtectNamedEntities => config.ProtectNamedEntities;
    public bool GuiKeepNearestToPlayers => config.KeepNearestToPlayers;
    public bool GuiLogActions => config.LogActions;
    public bool GuiLogToCsv => config.LogToCsv;
    public string GuiGenerationFilter => config.GenerationFilter ?? "Off";

    private void InvalidateBucketsAndReconcile()
    {
        entityBuckets.Clear();
        entityBucketIndex.Clear();
        QueueReconciliation();
    }

    public string SetRadiusFromGui(int radiusChunks)
    {
        config.RadiusChunks = Math.Clamp(radiusChunks, 0, 32);
        SaveConfig();

        // This is now the fallback radius for rules without their own radius.
        InvalidateBucketsAndReconcile();

        return $"Default rule radius set to {config.RadiusChunks} chunks ({config.RadiusChunks * 2 + 1}x{config.RadiusChunks * 2 + 1}).";
    }

    public string SetIntegerSettingFromGui(string? key, int value)
    {
        switch ((key ?? "").Trim().ToLowerInvariant())
        {
            case "defaultlimit":
                config.DefaultPerEntityLimit = Math.Clamp(value, 0, 1000000);
                break;

            case "playerprotectionradius":
                config.PlayerProtectionRadiusBlocks = Math.Clamp(value, 0, 1024);
                break;

            case "reconciliationseconds":
                config.SweepIntervalSeconds = Math.Clamp(value, 60, 3600);
                RestartReconciliationTimer();
                break;

            default:
                return "Unknown Animal Limiter numeric setting.";
        }

        SaveConfig();
        QueueReconciliation();
        return "Animal Limiter setting saved.";
    }

    public string SetBooleanSettingFromGui(string? key, bool value)
    {
        switch ((key ?? "").Trim().ToLowerInvariant())
        {
            case "enabled":
                config.Enabled = value;
                RestartReconciliationTimer();
                if (!value)
                {
                    entityBuckets.Clear();
                    entityBucketIndex.Clear();
                }
                break;
            case "limitallentities": config.LimitAllEntities = value; break;
            case "protectpets": config.ProtectPets = value; break;
            case "protectfarmanimals": config.ProtectFarmAnimals = value; break;
            case "protectnamedentities": config.ProtectNamedEntities = value; break;
            case "keepnearesttoplayers": config.KeepNearestToPlayers = value; break;
            case "logactions": config.LogActions = value; break;
            case "logtocsv": config.LogToCsv = value; break;
            default: return "Unknown Animal Limiter toggle.";
        }

        SaveConfig();
        QueueReconciliation();
        return $"Animal Limiter setting {(value ? "enabled" : "disabled")}.";
    }

    public AnimalPopulationScanState BeginPopulationScan()
    {
        return BeginPopulationScanInternal(includeUnruledEntities: false);
    }

    public AnimalPopulationScanState BeginRecommendationScan()
    {
        return BeginPopulationScanInternal(includeUnruledEntities: true);
    }

    private AnimalPopulationScanState BeginPopulationScanInternal(bool includeUnruledEntities)
    {
        if (populationScanJob is { Completed: false } runningJob)
        {
            if (runningJob.RecommendationScan == includeUnruledEntities)
                return SnapshotPopulationScanState(runningJob);

            return new AnimalPopulationScanState
            {
                ScanId = runningJob.Id,
                Completed = true,
                Error = "Another Animal Limiter population scan is already running. Wait for it to finish before starting a different scan mode."
            };
        }

        StopPopulationScanListener();

        var job = new PopulationScanJob
        {
            Id = Interlocked.Increment(ref populationScanSequence),
            StartedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Mode = includeUnruledEntities
                ? "Recommendation scan (all loaded agents near players)"
                : config.Enabled == true ? "Live tracked buckets" : "Preview snapshot (limiter disabled)",
            RecommendationScan = includeUnruledEntities,
            PlayerPositions = CaptureOnlinePlayerPositions()
        };

        try
        {
            job.Items = includeUnruledEntities
                ? BuildPopulationScanWorkFromLoadedSnapshot(includeUnruledEntities: true)
                : config.Enabled == true
                    ? BuildPopulationScanWorkFromTrackedBuckets()
                    : BuildPopulationScanWorkFromLoadedSnapshot(includeUnruledEntities: false);
            job.TotalEntities = job.Items.Sum(x => x.Entities.Count);
            job.TotalRegions = job.Items.Count;
            populationScanJob = job;

            if (job.TotalRegions == 0 || job.TotalEntities == 0)
            {
                CompletePopulationScan(job);
            }
            else
            {
                populationScanListenerId = sapi.Event.RegisterGameTickListener(
                    _ => ProcessPopulationScanTick(),
                    ex => FailPopulationScan(ex),
                    20);
            }
        }
        catch (Exception ex)
        {
            populationScanJob = job;
            FailPopulationScan(ex);
        }

        return SnapshotPopulationScanState(job);
    }

    public AnimalPopulationScanState GetPopulationScanState(long scanId)
    {
        var job = populationScanJob;
        if (job == null || (scanId > 0 && job.Id != scanId))
        {
            return new AnimalPopulationScanState
            {
                ScanId = scanId,
                Completed = true,
                Error = "Population scan was not found or has expired. Start a new scan."
            };
        }

        return SnapshotPopulationScanState(job);
    }

    public AnimalLimiterRecommendationSnapshot GetWebRecommendations(long scanId)
    {
        var job = populationScanJob;
        if (job == null || (scanId > 0 && job.Id != scanId))
        {
            return new AnimalLimiterRecommendationSnapshot
            {
                ScanId = scanId,
                Error = "Recommendation scan was not found or has expired. Run Analyze & Recommend again."
            };
        }

        if (!job.RecommendationScan)
        {
            return new AnimalLimiterRecommendationSnapshot
            {
                ScanId = job.Id,
                Error = "The most recent scan was a normal limiter population scan. Run Analyze & Recommend to inspect unruled entities too."
            };
        }

        if (!job.Completed)
        {
            return new AnimalLimiterRecommendationSnapshot
            {
                ScanId = job.Id,
                Error = "Recommendation scan is still running."
            };
        }

        if (!string.IsNullOrWhiteSpace(job.Error))
        {
            return new AnimalLimiterRecommendationSnapshot
            {
                ScanId = job.Id,
                Error = job.Error
            };
        }

        var recommendations = new List<AnimalLimiterRecommendationInfo>();
        foreach (var group in job.Rows.GroupBy(x => x.Code, StringComparer.OrdinalIgnoreCase))
        {
            var values = group.Select(x => Math.Max(0, x.Eligible)).OrderBy(x => x).ToList();
            if (values.Count == 0) continue;

            int total = values.Sum();
            int peak = values[^1];
            if (peak < 8 || total < 12) continue;

            double average = values.Average();
            int median = PercentileNearestRank(values, 0.50);
            int upperQuartile = PercentileNearestRank(values, 0.75);
            int suggested;
            string confidence;

            if (values.Count >= 3)
            {
                suggested = Math.Max(4, (int)Math.Ceiling(Math.Max(median * 1.75d, upperQuartile * 1.25d)));
                confidence = values.Count >= 5 ? "High" : "Medium";
            }
            else if (values.Count == 2)
            {
                suggested = Math.Max(4, (int)Math.Ceiling(average * 1.15d));
                confidence = "Medium";
            }
            else
            {
                suggested = Math.Max(4, (int)Math.Ceiling(peak * 0.80d));
                confidence = "Low";
            }

            // Never recommend a cap above the observed hotspot: that would have no immediate effect.
            suggested = Math.Min(suggested, Math.Max(0, peak - 1));
            if (suggested < 0) continue;

            int estimatedRemoval = values.Sum(x => Math.Max(0, x - suggested));
            if (estimatedRemoval <= 0) continue;

            string code = group.Key;
            int? effectiveLimit = ResolveLimitForCode(code, null, out string effectiveRule);
            bool hasEffectiveRule = effectiveLimit.HasValue;

            // If an existing effective rule is already at least as strict, there is no new rule to recommend.
            if (effectiveLimit.HasValue && effectiveLimit.Value <= suggested) continue;

            int radius = group.Select(x => x.RadiusChunks).DefaultIfEmpty(config.RadiusChunks).First();
            string reason = values.Count switch
            {
                >= 5 => $"Observed {values.Count} active regions. Typical eligible population was about {median}-{upperQuartile}, while the hotspot reached {peak}. The suggested cap keeps headroom above the typical range while trimming the hotspot.",
                >= 3 => $"Observed {values.Count} active regions. Typical eligible population was about {median}-{upperQuartile}, with a peak of {peak}. This is a conservative cap with headroom above the normal range.",
                2 => $"Only two active regions were observed (average {average:0.0}, peak {peak}), so this is a cautious recommendation and should be reviewed before applying.",
                _ => $"Only one active region was observed with {peak} eligible entities. This low-confidence suggestion trims roughly 20% of that hotspot; review it carefully before applying."
            };

            recommendations.Add(new AnimalLimiterRecommendationInfo
            {
                Pattern = code,
                SuggestedLimit = suggested,
                RadiusChunks = Math.Clamp(radius, 0, 32),
                RegionsObserved = values.Count,
                TotalEligible = total,
                PeakEligible = peak,
                AverageEligible = average,
                MedianEligible = median,
                UpperQuartileEligible = upperQuartile,
                EstimatedWouldRemoveNow = estimatedRemoval,
                HasEffectiveRule = hasEffectiveRule,
                EffectiveRule = hasEffectiveRule ? effectiveRule : "",
                EffectiveLimit = effectiveLimit,
                Confidence = confidence,
                Reason = reason
            });
        }

        recommendations = recommendations
            .OrderByDescending(x => x.EstimatedWouldRemoveNow)
            .ThenByDescending(x => x.PeakEligible)
            .ThenBy(x => x.Pattern, StringComparer.OrdinalIgnoreCase)
            .Take(100)
            .ToList();

        return new AnimalLimiterRecommendationSnapshot
        {
            ScanId = job.Id,
            Ready = true,
            Note = recommendations.Count == 0
                ? "No strong rule candidates were found in the currently loaded player areas. Recommendations are intentionally conservative."
                : "Recommendations are based only on currently loaded non-player agents near online players. Review and preview the estimated impact before applying; NSE never auto-applies these suggestions.",
            Recommendations = recommendations
        };
    }

    private static int PercentileNearestRank(List<int> sortedValues, double percentile)
    {
        if (sortedValues.Count == 0) return 0;
        int index = Math.Clamp((int)Math.Ceiling(sortedValues.Count * percentile) - 1, 0, sortedValues.Count - 1);
        return sortedValues[index];
    }

    public List<string> GetGuiPopulationLines()
    {
        // The in-game Investigator request/response protocol is synchronous, so retain an
        // immediate bounded result there. The web dashboard uses BeginPopulationScan() instead
        // so large servers do not hold an HTTP request or one long main-thread task open.
        bool live = config.Enabled == true;
        var rows = live
            ? BuildPopulationRowsFromTrackedBucketsImmediate()
            : BuildPopulationRowsFromLoadedSnapshotImmediate();
        return BuildPopulationLines(
            live ? "Live tracked buckets" : "Preview snapshot (limiter disabled)",
            rows,
            PopulationScanMaxRegionalRows);
    }

    private List<PopulationScanWorkItem> BuildPopulationScanWorkFromTrackedBuckets()
    {
        var items = new List<PopulationScanWorkItem>();
        foreach (var pair in entityBuckets.ToList())
        {
            var key = pair.Key;
            int? limit = ResolveLimitForCode(key.Code, null, out string rule);
            if (!limit.HasValue || ResolveRadiusForRule(rule) != key.RadiusChunks) continue;

            var entities = pair.Value.EntityIds
                .Select(ResolveLoadedAgent)
                .Where(x => x != null && x.Alive)
                .Select(x => x!)
                .ToList();
            if (entities.Count == 0) continue;

            items.Add(new PopulationScanWorkItem(
                key.Code, key.X, key.Z, rule, limit.Value, key.RadiusChunks, entities));
        }
        return items;
    }

    private List<PopulationScanWorkItem> BuildPopulationScanWorkFromLoadedSnapshot(bool includeUnruledEntities)
    {
        var items = new List<PopulationScanWorkItem>();
        int chunkSize = GlobalConstants.ChunkSize;
        var agents = GatherLoadedAgentsNearPlayers();

        foreach (var byCode in agents.GroupBy(CodeOf, StringComparer.OrdinalIgnoreCase))
        {
            string code = byCode.Key;
            int? limit = ResolveLimitForCode(code, null, out string rule);
            if (!limit.HasValue)
            {
                if (!includeUnruledEntities || config.ExcludeRules.Any(pattern => GlobMatch(code, pattern))) continue;
                rule = "<unruled>";
                limit = Math.Max(0, config.DefaultPerEntityLimit);
            }

            int radiusChunks = rule == "<unruled>"
                ? Math.Clamp(config.RadiusChunks, 0, 32)
                : ResolveRadiusForRule(rule);
            int widthChunks = radiusChunks * 2 + 1;
            int regionBlocks = Math.Max(chunkSize, widthChunks * chunkSize);

            foreach (var region in byCode
                         .Select(entity =>
                         {
                             if (!EntityPositionResolver.TryGetCoordinates(entity, out var x, out _, out var z, out _))
                                 return null;
                             return new PositionedAgent(entity, x, z);
                         })
                         .Where(x => x != null)
                         .Select(x => x!)
                         .GroupBy(x => (
                             X: FloorDiv((int)Math.Floor(x.X), regionBlocks),
                             Z: FloorDiv((int)Math.Floor(x.Z), regionBlocks))))
            {
                var entities = region.Select(x => x.Entity).ToList();
                if (entities.Count == 0) continue;
                items.Add(new PopulationScanWorkItem(
                    code, region.Key.X, region.Key.Z, rule, limit.Value, radiusChunks, entities));
            }
        }

        return items;
    }

    private void ProcessPopulationScanTick()
    {
        var job = populationScanJob;
        if (job == null || job.Completed)
        {
            StopPopulationScanListener();
            return;
        }

        var sw = Stopwatch.StartNew();
        try
        {
            while (job.ItemIndex < job.Items.Count && sw.Elapsed.TotalMilliseconds < PopulationScanTickBudgetMs)
            {
                var item = job.Items[job.ItemIndex];

                while (job.EntityIndex < item.Entities.Count && sw.Elapsed.TotalMilliseconds < PopulationScanTickBudgetMs)
                {
                    var entity = item.Entities[job.EntityIndex++];
                    job.ProcessedEntities++;
                    if (entity == null || !entity.Alive) continue;

                    job.CurrentTracked++;
                    if (IsProtected(entity, out _) || IsInsideHardPlayerProtection(entity, job.PlayerPositions))
                        job.CurrentProtected++;
                }

                if (job.EntityIndex < item.Entities.Count) break;

                if (job.CurrentTracked > 0)
                {
                    job.Rows.Add(new PopulationStatusRow(
                        item.Code,
                        item.RegionX,
                        item.RegionZ,
                        item.Rule,
                        job.CurrentTracked,
                        item.Limit,
                        item.RadiusChunks,
                        job.CurrentProtected,
                        Math.Max(0, job.CurrentTracked - job.CurrentProtected)));
                }

                job.ProcessedRegions++;
                job.ItemIndex++;
                job.EntityIndex = 0;
                job.CurrentTracked = 0;
                job.CurrentProtected = 0;
            }

            if (job.ItemIndex >= job.Items.Count)
                CompletePopulationScan(job);
        }
        catch (Exception ex)
        {
            FailPopulationScan(ex);
        }
    }

    private void CompletePopulationScan(PopulationScanJob job)
    {
        job.Completed = true;
        job.CompletedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        job.Lines = BuildPopulationLines(job.Mode, job.Rows, PopulationScanMaxRegionalRows);
        ReleasePopulationScanWorkingSet(job);
        StopPopulationScanListener();
    }

    private void FailPopulationScan(Exception ex)
    {
        var job = populationScanJob;
        if (job != null)
        {
            job.Completed = true;
            job.CompletedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            job.Error = $"{ex.GetType().Name}: {ex.Message}";
            ReleasePopulationScanWorkingSet(job);
        }
        StopPopulationScanListener();
        sapi.Logger.Warning("[AnimalLimiter] Population scan failed safely: {0}: {1}", ex.GetType().Name, ex.Message);
    }

    private static void ReleasePopulationScanWorkingSet(PopulationScanJob job)
    {
        // PopulationScanWorkItem contains live EntityAgent references. Keeping the completed job
        // for the web UI used to keep those entities (and potentially their chunk/world graph)
        // rooted until another scan replaced it. Keep only the lightweight summary after a scan.
        job.Items = new List<PopulationScanWorkItem>();
        job.PlayerPositions = new List<(double X, double Y, double Z)>();
        job.ItemIndex = 0;
        job.EntityIndex = 0;
        job.CurrentTracked = 0;
        job.CurrentProtected = 0;
    }

    private void StopPopulationScanListener()
    {
        if (populationScanListenerId < 0) return;
        try { sapi.Event.UnregisterGameTickListener(populationScanListenerId); } catch { }
        populationScanListenerId = -1;
    }

    private AnimalPopulationScanState SnapshotPopulationScanState(PopulationScanJob job)
    {
        int progress = job.TotalEntities <= 0
            ? (job.Completed ? 100 : 0)
            : Math.Clamp((int)Math.Round(job.ProcessedEntities * 100d / job.TotalEntities), 0, 100);
        long end = job.CompletedUnixMilliseconds > 0
            ? job.CompletedUnixMilliseconds
            : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        return new AnimalPopulationScanState
        {
            ScanId = job.Id,
            Running = !job.Completed,
            Completed = job.Completed,
            ProgressPercent = progress,
            ProcessedEntities = job.ProcessedEntities,
            TotalEntities = job.TotalEntities,
            ProcessedRegions = job.ProcessedRegions,
            TotalRegions = job.TotalRegions,
            StartedUnixMilliseconds = job.StartedUnixMilliseconds,
            DurationMilliseconds = Math.Max(0, end - job.StartedUnixMilliseconds),
            Mode = job.Mode,
            Error = job.Error,
            Lines = job.Completed && string.IsNullOrWhiteSpace(job.Error)
                ? new List<string>(job.Lines)
                : new List<string>()
        };
    }

    private List<(double X, double Y, double Z)> CaptureOnlinePlayerPositions()
    {
        var positions = new List<(double X, double Y, double Z)>();
        foreach (var p in sapi.World.AllOnlinePlayers)
        {
            if (p?.Entity == null) continue;
            if (EntityPositionResolver.TryGetCoordinates(p.Entity, out var x, out var y, out var z, out _))
                positions.Add((x, y, z));
        }
        return positions;
    }

    private bool IsInsideHardPlayerProtection(Entity entity, List<(double X, double Y, double Z)> playerPositions)
    {
        if (config.PlayerProtectionRadiusBlocks <= 0 || playerPositions.Count == 0) return false;
        if (!EntityPositionResolver.TryGetCoordinates(entity, out var ex, out var ey, out var ez, out _)) return false;

        double maxSq = (double)config.PlayerProtectionRadiusBlocks * config.PlayerProtectionRadiusBlocks;
        foreach (var p in playerPositions)
        {
            double dx = p.X - ex;
            double dy = p.Y - ey;
            double dz = p.Z - ez;
            if (dx * dx + dy * dy + dz * dz <= maxSq) return true;
        }
        return false;
    }

    private static List<string> BuildPopulationLines(string mode, List<PopulationStatusRow> rows, int maxRegionalRows)
    {
        var lines = new List<string> { "MODE\t" + mode };

        foreach (var total in rows
                     .GroupBy(x => x.Code, StringComparer.OrdinalIgnoreCase)
                     .Select(group => new
                     {
                         Code = group.Key,
                         Tracked = group.Sum(x => x.Tracked),
                         Protected = group.Sum(x => x.Protected),
                         Eligible = group.Sum(x => x.Eligible),
                         Regions = group.Count(),
                         OverRegions = group.Count(x => x.Eligible > x.Limit)
                     })
                     .OrderByDescending(x => x.Tracked)
                     .ThenBy(x => x.Code, StringComparer.OrdinalIgnoreCase))
        {
            lines.Add(
                $"TOTAL\t{total.Code}\t{total.Tracked}\t{total.Protected}\t" +
                $"{total.Eligible}\t{total.Regions}\t{total.OverRegions}");
        }

        var orderedRows = rows
            .OrderByDescending(x => x.Eligible > x.Limit)
            .ThenByDescending(x => Math.Max(0, x.Eligible - x.Limit))
            .ThenByDescending(x => x.Tracked)
            .ThenBy(x => x.Code, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.RegionX)
            .ThenBy(x => x.RegionZ)
            .ToList();

        foreach (var row in orderedRows.Take(Math.Max(1, maxRegionalRows)))
        {
            lines.Add(
                $"ROW\t{row.Code}\t{row.RegionX},{row.RegionZ}\t{row.Rule}\t" +
                $"{row.Tracked}\t{row.Limit}\t{row.RadiusChunks}\t{row.Protected}\t{row.Eligible}");
        }

        if (orderedRows.Count > maxRegionalRows)
            lines.Add($"TRUNCATED\t{orderedRows.Count - maxRegionalRows}\t{orderedRows.Count}");

        return lines;
    }

    private List<PopulationStatusRow> BuildPopulationRowsFromTrackedBucketsImmediate()
    {
        var rows = new List<PopulationStatusRow>();
        var playerPositions = CaptureOnlinePlayerPositions();

        foreach (var pair in entityBuckets.ToList())
        {
            var key = pair.Key;
            var bucket = pair.Value;

            int? limit = ResolveLimitForCode(key.Code, null, out string rule);
            if (!limit.HasValue || ResolveRadiusForRule(rule) != key.RadiusChunks) continue;

            int tracked = 0;
            int protectedCount = 0;
            foreach (var entityId in bucket.EntityIds)
            {
                var entity = ResolveLoadedAgent(entityId);
                if (entity == null || !entity.Alive) continue;
                tracked++;
                if (IsProtected(entity, out _) || IsInsideHardPlayerProtection(entity, playerPositions))
                    protectedCount++;
            }

            if (tracked == 0) continue;
            rows.Add(new PopulationStatusRow(
                key.Code, key.X, key.Z, rule, tracked, limit.Value, key.RadiusChunks,
                protectedCount, Math.Max(0, tracked - protectedCount)));
        }

        return rows;
    }

    private List<PopulationStatusRow> BuildPopulationRowsFromLoadedSnapshotImmediate()
    {
        var rows = new List<PopulationStatusRow>();
        int chunkSize = GlobalConstants.ChunkSize;
        var agents = GatherLoadedAgentsNearPlayers();
        var playerPositions = CaptureOnlinePlayerPositions();

        foreach (var byCode in agents.GroupBy(CodeOf, StringComparer.OrdinalIgnoreCase))
        {
            string code = byCode.Key;
            int? limit = ResolveLimitForCode(code, null, out string rule);
            if (!limit.HasValue) continue;

            int radiusChunks = ResolveRadiusForRule(rule);
            int widthChunks = radiusChunks * 2 + 1;
            int regionBlocks = Math.Max(chunkSize, widthChunks * chunkSize);

            foreach (var region in byCode
                         .Select(entity =>
                         {
                             if (!EntityPositionResolver.TryGetCoordinates(entity, out var x, out _, out var z, out _))
                                 return null;
                             return new PositionedAgent(entity, x, z);
                         })
                         .Where(x => x != null)
                         .Select(x => x!)
                         .GroupBy(x => (
                             X: FloorDiv((int)Math.Floor(x.X), regionBlocks),
                             Z: FloorDiv((int)Math.Floor(x.Z), regionBlocks))))
            {
                int tracked = 0;
                int protectedCount = 0;
                foreach (var positionedAgent in region)
                {
                    var entity = positionedAgent.Entity;
                    if (entity == null || !entity.Alive) continue;
                    tracked++;
                    if (IsProtected(entity, out _) || IsInsideHardPlayerProtection(entity, playerPositions))
                        protectedCount++;
                }

                if (tracked == 0) continue;
                rows.Add(new PopulationStatusRow(
                    code, region.Key.X, region.Key.Z, rule, tracked, limit.Value, radiusChunks,
                    protectedCount, Math.Max(0, tracked - protectedCount)));
            }
        }

        return rows;
    }

    public List<string> GetGuiRuleLines()
    {
        var lines = new List<string>
        {
            $"Rules: {config.Rules.Count}",
            $"Exclusions: {config.ExcludeRules.Count}",
            "",
            "LIMIT RULES"
        };

        foreach (var kv in config.Rules
                     .OrderByDescending(x => Specificity(x.Key))
                     .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            bool explicitRadius = config.RuleRadiusChunks.TryGetValue(kv.Key, out var storedRadius);
            int radius = explicitRadius ? Math.Clamp(storedRadius, 0, 32) : Math.Clamp(config.RadiusChunks, 0, 32);
            lines.Add($"{kv.Key}    |    {kv.Value}    |    {radius} chunks{(explicitRadius ? "" : " (default)")}    |    {(config.RuleGenerationPolicies.TryGetValue(kv.Key, out var gp) ? gp.Describe() : "Inherit global protection")}");
        }

        if (config.Rules.Count == 0) lines.Add("(none)");

        lines.Add("");
        lines.Add("EXCLUSIONS");

        foreach (var rule in config.ExcludeRules.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            lines.Add(rule);

        if (config.ExcludeRules.Count == 0) lines.Add("(none)");
        return lines;
    }

    public List<string> GetGuiSettingsLines()
    {
        return new List<string>
        {
            $"Animal Limiter: {(config.Enabled == true ? "ENABLED" : "DISABLED")}",
            $"Default region radius: {config.RadiusChunks} chunks",
            $"Default per-entity limit: {config.DefaultPerEntityLimit}",
            $"Player protection radius: {config.PlayerProtectionRadiusBlocks} blocks",
            $"Reconciliation: {config.SweepIntervalSeconds}s",
            $"Generation filter: {config.GenerationFilter}",
            "",
            "Switches save immediately."
        };
    }

    public List<string> GetGuiRemovalLogLines(int maxCount = 50)
    {
        CleanupHistory();

        var rows = history
            .OrderByDescending(x => x.RemovedUnixSeconds)
            .Take(Math.Clamp(maxCount, 1, 100))
            .ToList();

        if (rows.Count == 0)
            return new List<string> { "No Animal Limiter removals have been recorded." };

        var lines = new List<string>(rows.Count);
        foreach (var row in rows)
        {
            var when = DateTimeOffset.FromUnixTimeSeconds(row.RemovedUnixSeconds)
                .ToLocalTime()
                .ToString("yyyy-MM-dd HH:mm:ss");

            var returned = row.Returned ? " [RETURNED]" : "";
            var nearest = string.IsNullOrWhiteSpace(row.NearestPlayerName)
                ? ""
                : $" | nearest {row.NearestPlayerName} {row.NearestPlayerDistance:0.0}b";

            lines.Add(
                $"{when} | {row.RemovalId}{returned} | {row.Code} | " +
                $"{row.X:0.0}, {row.Y:0.0}, {row.Z:0.0} | " +
                $"rule {row.Rule} | limit {row.Limit} | before {row.CountBeforeCull}{nearest}");
        }

        return lines;
    }

    public List<string> GetGuiStatusLines()
    {
        var lines = new List<string>
        {
            $"Animal Limiter: {(config.Enabled == true ? "ENABLED" : "DISABLED")}",
            $"Universal limiting: {(config.LimitAllEntities ? "ON" : "OFF")}",
            $"Default per-entity limit: {config.DefaultPerEntityLimit}",
            $"Default region radius: {config.RadiusChunks} chunks ({config.RadiusChunks * 2 + 1}x{config.RadiusChunks * 2 + 1})",
            $"Reconciliation interval: {config.SweepIntervalSeconds}s",
            $"Generation filter: {config.GenerationFilter}",
            $"Protections: pets={config.ProtectPets}, farm={config.ProtectFarmAnimals}, named={config.ProtectNamedEntities}",
            $"Stored rules: {config.Rules.Count}; exclusions: {config.ExcludeRules.Count}",
            "",
            "Rules:"
        };

        foreach (var kv in config.Rules.OrderByDescending(x => Specificity(x.Key)).ThenBy(x => x.Key).Take(40))
            lines.Add($"  {kv.Key} = {kv.Value}");
        if (config.Rules.Count == 0) lines.Add("  (none)");
        if (config.Rules.Count > 40) lines.Add($"  ... {config.Rules.Count - 40} more");
        return lines;
    }

    private void CmdRemove(IServerPlayer p, int g, CmdArgs a)
    {
        string pattern = NormalizePattern(a.PopWord());
        if (string.IsNullOrWhiteSpace(pattern)) { Msg(p, g, "Usage: /animallimit remove <pattern>"); return; }
        bool ok = config.Rules.Remove(pattern);
        config.RuleRadiusChunks.Remove(pattern);
        config.RuleGenerationPolicies.Remove(pattern);
        SaveConfig();
        if (ok) InvalidateBucketsAndReconcile();
        Msg(p, g, ok ? $"Removed rule {pattern}." : $"No exact stored rule named {pattern}.");
    }

    private void CmdExclude(IServerPlayer p, int g, CmdArgs a)
    {
        string pattern = NormalizePattern(a.PopWord());
        if (string.IsNullOrWhiteSpace(pattern)) { Msg(p, g, "Usage: /animallimit exclude <pattern>"); return; }
        if (!config.ExcludeRules.Any(x => x.Equals(pattern, StringComparison.OrdinalIgnoreCase))) config.ExcludeRules.Add(pattern);
        SaveConfig(); Msg(p, g, $"Excluded {pattern} from limiting.");
    }

    private void CmdInclude(IServerPlayer p, int g, CmdArgs a)
    {
        string pattern = NormalizePattern(a.PopWord());
        int n = config.ExcludeRules.RemoveAll(x => x.Equals(pattern, StringComparison.OrdinalIgnoreCase));
        SaveConfig(); Msg(p, g, n > 0 ? $"Removed exclusion {pattern}." : $"No exclusion named {pattern}.");
    }

    private void CmdProtect(IServerPlayer p, int g, CmdArgs a)
    {
        string what = (a.PopWord() ?? "").ToLowerInvariant();
        string val = (a.PopWord() ?? "").ToLowerInvariant();
        if ((what != "pets" && what != "farm" && what != "named") || (val != "on" && val != "off"))
        { Msg(p, g, "Usage: /animallimit protect pets|farm|named on|off"); return; }
        bool on = val == "on";
        if (what == "pets") config.ProtectPets = on;
        if (what == "farm") config.ProtectFarmAnimals = on;
        if (what == "named") config.ProtectNamedEntities = on;
        SaveConfig(); Msg(p, g, $"Protection {what} = {on}.");
    }

    private void CmdGenerationRule(IServerPlayer p, int g, CmdArgs args)
    {
        string pattern = NormalizePattern(args.PopWord());
        string? mode = AnimalGenerationPolicy.Canonical(args.PopWord());
        int threshold = 1;
        if (mode == null || (mode == "ProtectFromGeneration" && (!int.TryParse(args.PopWord(), out threshold) || threshold < 1 || threshold > 1000000)))
        { Msg(p, g, "Usage: /animallimit generationrule <stored-rule> inherit|off|protectfrom [generation]"); return; }
        if (!config.Rules.TryGetValue(pattern, out int limit)) { Msg(p, g, "Create that population rule first with /animallimit set or the webpanel."); return; }
        Msg(p, g, SetRuleFromGui(pattern, limit, ResolveRadiusForRule(pattern), mode, threshold));
        Msg(p, g, "Generation policy: " + (config.RuleGenerationPolicies.TryGetValue(pattern, out var policy) ? policy.Describe() : "Inherit global protection") + ". Owned pets, names, exclusions and nearby-player protection remain.");
    }

    private void CmdGeneration(IServerPlayer p, int g, CmdArgs a)
    {
        string v = (a.PopWord() ?? "").ToLowerInvariant();
        string canonical = v switch { "off" => "Off", "wildonly" => "WildOnly", "bredonly" => "BredOnly", "protectbred" => "ProtectBred", _ => "" };
        if (canonical.Length == 0) { Msg(p, g, "Usage: /animallimit generation off|wildonly|bredonly|protectbred"); return; }
        config.GenerationFilter = canonical; SaveConfig();
        string warning = canonical == "BredOnly" && config.ProtectFarmAnimals ? " Note: farm protection is ON, so bred/farm entities will still be protected until /animallimit protect farm off." : "";
        Msg(p, g, "Generation filter = " + canonical + "." + warning);
    }

    private void CmdList(IServerPlayer p, int g)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Rules (most specific matching rule wins):");
        foreach (var kv in config.Rules.OrderByDescending(x => Specificity(x.Key)).ThenBy(x => x.Key)) sb.AppendLine($"  {kv.Key} = {kv.Value} | {(config.RuleGenerationPolicies.TryGetValue(kv.Key, out var gp) ? gp.Describe() : "Inherit global protection")}");
        sb.AppendLine("Excludes: " + (config.ExcludeRules.Count == 0 ? "(none)" : string.Join(", ", config.ExcludeRules)));
        Msg(p, g, sb.ToString().TrimEnd());
    }

    private void CmdFind(IServerPlayer p, int g, CmdArgs a)
    {
        string q = (a.PopWord() ?? "").ToLowerInvariant();
        if (q.Length == 0) { Msg(p, g, "Usage: /animallimit find <text>"); return; }
        var found = RegisteredEntityCodes().Where(c => c.Contains(q, StringComparison.OrdinalIgnoreCase)).Take(50).ToList();
        Msg(p, g, found.Count == 0 ? "No registered entity codes matched." : string.Join("\n", found));
    }

    private void CmdMatches(IServerPlayer p, int g, CmdArgs a)
    {
        string pattern = NormalizePattern(a.PopWord());
        if (pattern.Length == 0) { Msg(p, g, "Usage: /animallimit matches <pattern>"); return; }
        var found = RegisteredEntityCodes().Where(c => GlobMatch(c, pattern)).Take(100).ToList();
        Msg(p, g, found.Count == 0 ? "No registered entity codes matched." : $"Matches ({found.Count}{(found.Count == 100 ? "+" : "")}):\n" + string.Join("\n", found));
    }

    private void CmdTop(IServerPlayer p, int g, CmdArgs a)
    {
        string pattern = NormalizePattern(a.PopWord());
        var ents = GatherLoadedAgentsNearPlayers();
        if (pattern.Length > 0) ents = ents.Where(e => GlobMatch(CodeOf(e), pattern)).ToList();
        var rows = ents.GroupBy(CodeOf, StringComparer.OrdinalIgnoreCase).Select(x => (Code: x.Key, Count: x.Count())).OrderByDescending(x => x.Count).Take(30).ToList();
        Msg(p, g, rows.Count == 0 ? "No matching loaded entities." : "Top loaded entities:\n" + string.Join("\n", rows.Select(x => $"{x.Count,5}  {x.Code}")));
    }

    private void CmdPreview(IServerPlayer p, int g, CmdArgs a)
    {
        string pattern = NormalizePattern(a.PopWord());
        int? tempLimit = null;
        string lim = a.PopWord();
        if (!string.IsNullOrWhiteSpace(lim))
        {
            if (!int.TryParse(lim, out int parsed) || parsed < 0) { Msg(p, g, "Usage: /animallimit preview [pattern] [limit]"); return; }
            tempLimit = parsed;
        }
        var result = SweepAllPlayerRegions(true, pattern.Length == 0 ? null : new PreviewOverride(pattern, tempLimit, config.RadiusChunks));
        Msg(p, g, $"Preview only: scanned {result.Scanned} eligible entities across {result.Regions} active regions; {result.OverLimitGroups} entity-type/region groups over limit; {result.WouldRemove} would be removed; {result.Protected} protected/skipped. Nothing was removed.");
    }

    private void CmdCull(IServerPlayer p, int g)
    {
        var result = SweepAllPlayerRegions(false, null);
        Msg(p, g, $"Cull complete: scanned {result.Scanned}; removed {result.Removed}; protected/skipped {result.Protected}; regions {result.Regions}.");
    }

    private void CmdHistory(IServerPlayer p, int g, CmdArgs a)
    {
        int n = 10;
        int.TryParse(a.PopWord(), out n); n = Math.Clamp(n, 1, 50);
        var rows = history.OrderByDescending(x => x.RemovedUnixSeconds).Take(n).ToList();
        if (rows.Count == 0) { Msg(p, g, "Removal history is empty."); return; }
        Msg(p, g, string.Join("\n", rows.Select(r => $"{r.RemovalId} {(r.Returned ? "[RETURNED]" : "")} {r.Code} @ {r.X:0.0},{r.Y:0.0},{r.Z:0.0} rule={r.Rule} limit={r.Limit}")));
    }

    private void CmdReturn(IServerPlayer p, int g, CmdArgs a)
    {
        string id = a.PopWord() ?? "";
        RemovalRecord? rec = id.Equals("last", StringComparison.OrdinalIgnoreCase)
            ? history.Where(r => !r.Returned).OrderByDescending(r => r.RemovedUnixSeconds).ThenByDescending(r => r.RemovalId).FirstOrDefault()
            : history.LastOrDefault(r => r.RemovalId.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (rec == null) { Msg(p, g, "Removal record not found (or no unreturned record for 'last')."); return; }
        if (rec.Returned) { Msg(p, g, "That record was already returned."); return; }
        if (Restore(rec, out string error))
        {
            rec.Returned = true; SaveHistory();
            sapi.Logger.Notification("[AnimalLimiter] Returned {0} ({1}) to {2:0.0},{3:0.0},{4:0.0}", rec.RemovalId, rec.Code, rec.X, rec.Y, rec.Z);
            Msg(p, g, $"Returned {rec.Code} from {rec.RemovalId} at {rec.X:0.0},{rec.Y:0.0},{rec.Z:0.0}.");
        }
        else Msg(p, g, "Return failed: " + error);
    }

    private SweepResult SweepAllPlayerRegions(bool preview, PreviewOverride? previewOverride)
    {
        var result = new SweepResult();
        if (!config.LimitAllEntities && config.Rules.Count == 0 && previewOverride == null) return result;

        int chunkSize = GlobalConstants.ChunkSize;
        var agents = GatherLoadedAgentsNearPlayers();
        result.Scanned = agents.Count;

        var positionedAgents = agents
            .Select(e => EntityPositionResolver.TryGetCoordinates(e, out var x, out _, out var z, out _)
                ? new PositionedAgent(e, x, z)
                : null)
            .Where(x => x != null)
            .Select(x => x!)
            .ToList();

        // Radius belongs to the matched rule. Group by entity code first, resolve that code's
        // winning rule/radius, then build region buckets using that rule's geometry.
        foreach (var byCode in positionedAgents.GroupBy(x => CodeOf(x.Entity), StringComparer.OrdinalIgnoreCase))
        {
            string code = byCode.Key;
            int? limit = ResolveLimitForCode(code, previewOverride, out string rule);
            if (!limit.HasValue) continue;

            int radiusChunks = previewOverride != null && !string.IsNullOrWhiteSpace(previewOverride.Pattern)
                ? previewOverride.RadiusChunks
                : ResolveRadiusForRule(rule);
            int widthChunks = radiusChunks * 2 + 1;
            int regionBlocks = Math.Max(chunkSize, widthChunks * chunkSize);

            foreach (var region in byCode.GroupBy(x => (
                         X: FloorDiv((int)Math.Floor(x.X), regionBlocks),
                         Z: FloorDiv((int)Math.Floor(x.Z), regionBlocks))))
            {
                result.Regions++;
                var allForCode = region.Select(x => x.Entity).Where(e => e.Alive).ToList();

                var eligible = new List<EntityAgent>();
                foreach (var ent in allForCode)
                {
                    if (IsProtected(ent, out _) || IsInsideHardPlayerProtection(ent))
                    {
                        result.Protected++;
                        continue;
                    }
                    eligible.Add(ent);
                }

                if (eligible.Count <= limit.Value) continue;

                result.OverLimitGroups++;
                int removeCount = eligible.Count - limit.Value;
                var ordered = config.KeepNearestToPlayers
                    ? eligible.OrderByDescending(NearestPlayerDistanceSq).ToList()
                    : eligible.OrderByDescending(e => e.EntityId).ToList();

                foreach (var ent in ordered.Take(removeCount))
                {
                    if (preview)
                    {
                        result.WouldRemove++;
                        continue;
                    }

                    if (RemoveEntity(ent, rule, limit.Value, eligible.Count))
                    {
                        result.Removed++;
                        UntrackEntity(ent.EntityId);
                    }
                }
            }
        }

        if (!preview && historyDirty) SaveHistory();
        return result;
    }

    private List<EntityAgent> GatherLoadedAgentsNearPlayers()
    {
        // Gather from player-centered boxes, de-duplicate by EntityId. This avoids iterating every
        // entity in every loaded chunk on huge worlds and follows the server's active player areas.
        var map = new Dictionary<long, EntityAgent>();
        int maxRadiusChunks = config.RuleRadiusChunks.Count == 0
            ? config.RadiusChunks
            : Math.Max(config.RadiusChunks, config.RuleRadiusChunks.Values.Max());
        float range = Math.Max(
            GlobalConstants.ChunkSize,
            (Math.Clamp(maxRadiusChunks, 0, 32) * 2 + 1) * GlobalConstants.ChunkSize * 1.5f);
        float verticalRange = Math.Max(range, sapi.World.BlockAccessor.MapSizeY);
        foreach (var player in sapi.World.AllOnlinePlayers)
        {
            if (player?.Entity == null) continue;
            if (!EntityPositionResolver.TryGetCoordinates(player.Entity, out var px, out var py, out var pz, out _)) continue;

            var center = new Vec3d(px, py, pz);
            foreach (var ent in sapi.World.GetEntitiesAround(center, range, verticalRange, e => e is EntityAgent && e is not EntityPlayer))
            {
                if (ent is EntityAgent agent && agent.Alive) map[agent.EntityId] = agent;
            }
        }
        return map.Values.ToList();
    }

    private int ResolveRadiusForRule(string? rule)
    {
        if (!string.IsNullOrWhiteSpace(rule) &&
            !rule.Equals("<default>", StringComparison.OrdinalIgnoreCase) &&
            !rule.StartsWith("preview:", StringComparison.OrdinalIgnoreCase) &&
            config.RuleRadiusChunks.TryGetValue(rule, out var radius))
        {
            return Math.Clamp(radius, 0, 32);
        }

        return Math.Clamp(config.RadiusChunks, 0, 32);
    }

    private int? ResolveLimitForCode(string code, PreviewOverride? preview, out string rule)
    {
        rule = "";

        // Normal live enforcement resolves the same entity codes repeatedly. Cache the final
        // answer so glob matching and rule sorting are paid once per code/config revision.
        // Preview requests intentionally bypass the cache because they temporarily overlay policy.
        if (preview == null && resolvedRuleCache.TryGetValue(code, out var cached))
        {
            rule = cached.Rule;
            return cached.HasLimit ? cached.Limit : null;
        }

        int? resolved = null;
        string resolvedRule = "";

        if (!config.ExcludeRules.Any(p => GlobMatch(code, p)))
        {
            if (preview != null)
            {
                if (!GlobMatch(code, preview.Pattern))
                    return null;
                if (preview.Limit.HasValue)
                {
                    rule = "preview:" + preview.Pattern;
                    return preview.Limit.Value;
                }
            }

            KeyValuePair<string, int>? best = null;
            foreach (var kv in config.Rules)
            {
                if (!GlobMatch(code, kv.Key)) continue;
                if (best == null || CompareRulePriority(kv.Key, best.Value.Key) < 0)
                    best = kv;
            }

            if (best.HasValue)
            {
                resolvedRule = best.Value.Key;
                resolved = Math.Max(0, best.Value.Value);
            }
            else if (config.LimitAllEntities)
            {
                resolvedRule = "<default>";
                resolved = config.DefaultPerEntityLimit;
            }
        }

        rule = resolvedRule;
        if (preview == null)
        {
            if (resolvedRuleCache.Count >= MaxResolvedRuleCacheEntries && !resolvedRuleCache.ContainsKey(code))
                resolvedRuleCache.Clear();
            resolvedRuleCache[code] = new ResolvedRuleCacheEntry(resolved.HasValue, resolved.GetValueOrDefault(), resolvedRule);
        }
        return resolved;
    }

    private static int CompareRulePriority(string left, string right)
    {
        bool leftExact = !left.Contains('*');
        bool rightExact = !right.Contains('*');
        if (leftExact != rightExact) return leftExact ? -1 : 1;

        int specificity = Specificity(right).CompareTo(Specificity(left));
        if (specificity != 0) return specificity;

        int length = right.Length.CompareTo(left.Length);
        if (length != 0) return length;

        return StringComparer.OrdinalIgnoreCase.Compare(left, right);
    }

    private bool IsProtected(EntityAgent entity, out string why)
    {
        why = "";
        if (entity is EntityPlayer) { why = "player"; return true; }
        string code = CodeOf(entity);
        if (config.ExcludeRules.Any(p => GlobMatch(code, p))) { why = "excluded"; return true; }

        if (config.ProtectPets && LooksTamedOrOwned(entity)) { why = "pet/owned"; return true; }

        AnimalGenerationPolicy? generationPolicy = null;
        if (config.RuleGenerationPolicies.Count > 0)
        {
            ResolveLimitForCode(code, null, out string matchedRule);
            config.RuleGenerationPolicies.TryGetValue(matchedRule, out generationPolicy);
        }
        bool inherit = generationPolicy == null || generationPolicy.Mode == "Inherit";
        string gf = (config.GenerationFilter ?? "Off").ToLowerInvariant();
        bool needBred = inherit && (config.ProtectFarmAnimals || gf == "wildonly" || gf == "protectbred" || gf == "bredonly");
        bool bred = needBred && LooksBred(entity);

        if (inherit && config.ProtectFarmAnimals && bred) { why = "farm/bred"; return true; }
        if (config.ProtectNamedEntities && LooksNamed(entity)) { why = "named"; return true; }
        if (!inherit)
        {
            if (generationPolicy!.Mode == "ProtectFromGeneration" && GenerationAtLeast(entity, generationPolicy.MinimumProtectedGeneration))
            { why = "rule generation threshold protected"; return true; }
            return false; // Off bypasses both generic generation and farm/bred protection.
        }
        if ((gf == "wildonly" || gf == "protectbred") && bred) { why = "generation protected"; return true; }
        if (gf == "bredonly" && !bred) { why = "generation wild skipped"; return true; }
        return false;
    }

    private bool GenerationAtLeast(Entity entity, int threshold)
    {
        foreach (string key in config.GenerationAttributeKeys)
        {
            double number = entity.WatchedAttributes.GetDecimal(key, 0);
            if (double.IsFinite(number) && number >= threshold) return true;
            if (threshold == 1 && entity.WatchedAttributes[key] is BoolAttribute or StringAttribute && entity.WatchedAttributes.GetAsBool(key, false)) return true;
        }
        if (threshold == 1)
            foreach (string key in config.TamedAttributeKeys)
                if (key.Contains("domestic", StringComparison.OrdinalIgnoreCase) && entity.WatchedAttributes.GetAsBool(key, false)) return true;
        return false;
    }

    private bool LooksTamedOrOwned(Entity entity)
    {
        foreach (string key in config.OwnerAttributeKeys)
        {
            if (!entity.WatchedAttributes.HasAttribute(key)) continue;

            // Owner ids are commonly strings, but some entity mods store ownership/taming
            // flags as IntAttribute, LongAttribute, FloatAttribute, DoubleAttribute or BoolAttribute.
            // GetAsBool() is deliberately type-tolerant in the VS API, unlike TryGetLong(),
            // which directly casts to LongAttribute and throws when the actual type is IntAttribute.
            string s = entity.WatchedAttributes.GetString(key, "");
            if (!string.IsNullOrWhiteSpace(s)) return true;
            if (entity.WatchedAttributes.GetAsBool(key, false)) return true;
        }

        foreach (string key in config.TamedAttributeKeys)
        {
            if (entity.WatchedAttributes.GetAsBool(key, false)) return true;
        }
        return false;
    }

    private bool LooksNamed(Entity entity)
    {
        foreach (string key in config.NameAttributeKeys)
        {
            string s = entity.WatchedAttributes.GetString(key, "");
            if (!string.IsNullOrWhiteSpace(s)) return true;
        }
        return false;
    }

    private bool LooksBred(Entity entity)
    {
        foreach (string key in config.GenerationAttributeKeys)
        {
            // GetAsBool() accepts Int/Long/Float/Double/Bool (and true/1 strings),
            // avoiding unsafe typed attribute casts when different mods use different
            // primitive attribute types for generation counters.
            if (entity.WatchedAttributes.GetAsBool(key, false)) return true;
        }

        // Many mods expose domestication as a number/bool rather than a generation field.
        foreach (string key in config.TamedAttributeKeys)
        {
            if (key.Contains("domestic", StringComparison.OrdinalIgnoreCase) &&
                entity.WatchedAttributes.GetAsBool(key, false))
            {
                return true;
            }
        }
        return false;
    }

    private bool IsInsideHardPlayerProtection(Entity entity)
    {
        if (config.PlayerProtectionRadiusBlocks <= 0) return false;
        double maxSq = (double)config.PlayerProtectionRadiusBlocks * config.PlayerProtectionRadiusBlocks;
        return NearestPlayerDistanceSq(entity) <= maxSq;
    }

    private double NearestPlayerDistanceSq(Entity entity)
    {
        if (!EntityPositionResolver.TryGetCoordinates(entity, out var ex, out var ey, out var ez, out _))
            return double.MaxValue;

        double best = double.MaxValue;
        foreach (var p in sapi.World.AllOnlinePlayers)
        {
            if (p?.Entity == null) continue;
            if (!EntityPositionResolver.TryGetCoordinates(p.Entity, out var px, out var py, out var pz, out _)) continue;

            double dx = px - ex;
            double dy = py - ey;
            double dz = pz - ez;
            double d = dx * dx + dy * dy + dz * dz;
            if (d < best) best = d;
        }
        return best;
    }

    private (string Name, double Distance) NearestPlayer(Entity entity)
    {
        if (!EntityPositionResolver.TryGetCoordinates(entity, out var ex, out var ey, out var ez, out _))
            return ("", -1);

        string name = "";
        double bestSq = double.MaxValue;
        foreach (var p in sapi.World.AllOnlinePlayers)
        {
            if (p?.Entity == null) continue;
            if (!EntityPositionResolver.TryGetCoordinates(p.Entity, out var px, out var py, out var pz, out _)) continue;

            double dx = px - ex;
            double dy = py - ey;
            double dz = pz - ez;
            double d = dx * dx + dy * dy + dz * dz;
            if (d < bestSq) { bestSq = d; name = p.PlayerName; }
        }
        return (name, bestSq == double.MaxValue ? -1 : Math.Sqrt(bestSq));
    }

    private bool RemoveEntity(EntityAgent entity, string rule, int limit, int countBefore)
    {
        try
        {
            string code = CodeOf(entity);
            if (!EntityPositionResolver.TryGetPose(entity, out var ex, out var ey, out var ez, out _, out var yaw, out var pitch))
            {
                sapi.Logger.Warning("[AnimalLimiter] Skipped removal for entity {0}: runtime position unavailable.", entity.EntityId);
                return false;
            }

            var nearest = NearestPlayer(entity);
            var rec = new RemovalRecord
            {
                RemovalId = MakeRemovalId(),
                OriginalEntityId = entity.EntityId,
                Code = code,
                DisplayName = code,
                X = ex, Y = ey, Z = ez,
                Yaw = yaw, Pitch = pitch,
                ChunkX = (int)Math.Floor(ex / GlobalConstants.ChunkSize),
                ChunkZ = (int)Math.Floor(ez / GlobalConstants.ChunkSize),
                Rule = rule,
                Limit = limit,
                CountBeforeCull = countBefore,
                RadiusChunks = config.RadiusChunks,
                NearestPlayerName = nearest.Name,
                NearestPlayerDistance = nearest.Distance,
                LookedTamedOrOwned = LooksTamedOrOwned(entity),
                LookedFarmBred = LooksBred(entity),
                LookedNamed = LooksNamed(entity),
                WatchedAttributesBase64 = SerializeTree(entity.WatchedAttributes),
                RemovedUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };

            entity.Die(EnumDespawnReason.Removed);
            history.Add(rec);
            historyDirty = true;
            CleanupHistory();
            LogRemoval(rec);
            return true;
        }
        catch (Exception e)
        {
            sapi.Logger.Error("[AnimalLimiter] Failed to remove entity {0}: {1}", entity.EntityId, e);
            return false;
        }
    }

    private bool Restore(RemovalRecord rec, out string error)
    {
        error = "";
        try
        {
            var props = sapi.World.GetEntityType(new AssetLocation(rec.Code));
            if (props == null) { error = "entity type is not registered: " + rec.Code; return false; }

            Entity entity = sapi.World.ClassRegistry.CreateEntity(props);
            if (!EntityPositionResolver.TrySetPose(entity, rec.X, rec.Y, rec.Z, rec.Yaw, rec.Pitch))
            {
                error = "runtime entity position is unavailable";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(rec.WatchedAttributesBase64))
            {
                RestoreTree(entity.WatchedAttributes, rec.WatchedAttributesBase64);
            }

            // SpawnEntity performs the normal engine-side initialization/spawn path. Do not call
            // Initialize manually with a fabricated chunk index; official game code creates the
            // entity, sets position/attributes, then calls SpawnEntity.
            sapi.World.SpawnEntity(entity);
            return true;
        }
        catch (Exception e)
        {
            error = e.GetType().Name + ": " + e.Message;
            sapi.Logger.Error("[AnimalLimiter] Return failed for {0}: {1}", rec.RemovalId, e);
            return false;
        }
    }

    private string SerializeTree(ITreeAttribute tree)
    {
        using var ms = new MemoryStream();
        using (var bw = new BinaryWriter(ms, Encoding.UTF8, true)) tree.ToBytes(bw);
        return Convert.ToBase64String(ms.ToArray());
    }

    private void RestoreTree(SyncedTreeAttribute target, string base64)
    {
        byte[] data = Convert.FromBase64String(base64);
        using var ms = new MemoryStream(data);
        using var br = new BinaryReader(ms, Encoding.UTF8, true);
        target.Clear();
        target.FromBytes(br);
    }

    private void LogRemoval(RemovalRecord r)
    {
        if (config.LogActions)
        {
            sapi.Logger.Notification("[AnimalLimiter] REMOVED {0} id={1} code={2} pos={3:0.0},{4:0.0},{5:0.0} chunk={6},{7} rule={8} limit={9} before={10} nearest={11}({12:0.0}m) pet={13} bred={14} named={15}",
                r.RemovalId, r.OriginalEntityId, r.Code, r.X, r.Y, r.Z, r.ChunkX, r.ChunkZ, r.Rule, r.Limit, r.CountBeforeCull,
                r.NearestPlayerName, r.NearestPlayerDistance, r.LookedTamedOrOwned, r.LookedFarmBred, r.LookedNamed);
        }

        if (!config.LogToCsv) return;
        lock (csvLock)
        {
            try
            {
                string dir = sapi.GetOrCreateDataPath(config.CsvDirName);
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, $"{config.CsvFilePrefix}-{DateTime.UtcNow:yyyy-MM-dd}.csv");
                if (!File.Exists(path))
                    File.WriteAllText(path, "utc,removalId,entityId,code,x,y,z,chunkX,chunkZ,rule,limit,countBefore,radiusChunks,nearestPlayer,nearestDistance,petOrOwned,bred,named\n", Encoding.UTF8);
                string row = string.Join(",", Csv(DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)), Csv(r.RemovalId), r.OriginalEntityId,
                    Csv(r.Code), F(r.X), F(r.Y), F(r.Z), r.ChunkX, r.ChunkZ, Csv(r.Rule), r.Limit, r.CountBeforeCull, r.RadiusChunks,
                    Csv(r.NearestPlayerName), F(r.NearestPlayerDistance), r.LookedTamedOrOwned, r.LookedFarmBred, r.LookedNamed) + "\n";
                File.AppendAllText(path, row, Encoding.UTF8);
            }
            catch (Exception e) { sapi.Logger.Error("[AnimalLimiter] CSV logging failed: {0}", e.Message); }
        }
    }

    private string MakeRemovalId() => $"AL-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{(++removalSequence % 10000):D4}";
    private static int ParseRemovalSequence(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return 0;
        int dash = id.LastIndexOf('-');
        return dash >= 0 && int.TryParse(id[(dash + 1)..], out int n) ? n : 0;
    }
    private static string F(double d) => d.ToString("0.###", CultureInfo.InvariantCulture);
    private static string Csv(string? s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
    private static string FullCode(AssetLocation? code) => code == null ? "unknown:unknown" : $"{code.Domain}:{code.Path}".ToLowerInvariant();
    private static string CodeOf(Entity e) => FullCode(e.Code ?? e.Properties?.Code);
    private static string NormalizePattern(string? s) => (s ?? "").Trim().ToLowerInvariant();
    private IEnumerable<string> RegisteredEntityCodes() => sapi.World.EntityTypes.Select(p => FullCode(p.Code)).Distinct(StringComparer.OrdinalIgnoreCase);
    private int CountRegisteredMatches(string p) => RegisteredEntityCodes().Count(c => GlobMatch(c, p));

    private static int Specificity(string pattern) => pattern.Count(c => c != '*');

    private static bool GlobMatch(string value, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return false;
        value = value.ToLowerInvariant(); pattern = pattern.ToLowerInvariant();
        if (!pattern.Contains('*')) return value.Equals(pattern, StringComparison.OrdinalIgnoreCase);
        string[] parts = pattern.Split('*');
        int pos = 0;
        if (!pattern.StartsWith('*') && !value.StartsWith(parts[0], StringComparison.OrdinalIgnoreCase)) return false;
        foreach (string part in parts)
        {
            if (part.Length == 0) continue;
            int idx = value.IndexOf(part, pos, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return false;
            pos = idx + part.Length;
        }
        if (!pattern.EndsWith('*') && parts[^1].Length > 0 && !value.EndsWith(parts[^1], StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private static int FloorDiv(int a, int b)
    {
        int q = a / b, r = a % b;
        if (r != 0 && ((r < 0) != (b < 0))) q--;
        return q;
    }

    private void Msg(IServerPlayer p, int groupId, string text) => sapi.SendMessage(p, groupId, text, EnumChatType.CommandSuccess);

    private sealed class LegacyAnimalLimiterConfig
    {
        public int PerCodeLimitPerChunk { get; set; } = 40;
        public Dictionary<string, int>? PerCodeOverrides { get; set; }
        public List<string>? IncludeCodeStartsWith { get; set; }
        public List<string>? ProtectedCodeStartsWith { get; set; }
        public bool ProtectTamedOrOwned { get; set; } = true;
        public bool KeepNearestToPlayers { get; set; } = true;
        public bool LogActions { get; set; } = true;
        public bool LogToCsv { get; set; } = true;
        public string CsvDirName { get; set; } = "AnimalLimiter";
        public string CsvFilePrefix { get; set; } = "removals";
    }

    private sealed record PopulationStatusRow(
        string Code,
        int RegionX,
        int RegionZ,
        string Rule,
        int Tracked,
        int Limit,
        int RadiusChunks,
        int Protected,
        int Eligible);

    private sealed class PopulationScanJob
    {
        public long Id;
        public long StartedUnixMilliseconds;
        public long CompletedUnixMilliseconds;
        public string Mode = "";
        public string Error = "";
        public bool Completed;
        public bool RecommendationScan;
        public int ItemIndex;
        public int EntityIndex;
        public int ProcessedEntities;
        public int ProcessedRegions;
        public int TotalEntities;
        public int TotalRegions;
        public int CurrentTracked;
        public int CurrentProtected;
        public List<PopulationScanWorkItem> Items = new();
        public List<PopulationStatusRow> Rows = new();
        public List<string> Lines = new();
        public List<(double X, double Y, double Z)> PlayerPositions = new();
    }

    private sealed record PopulationScanWorkItem(
        string Code,
        int RegionX,
        int RegionZ,
        string Rule,
        int Limit,
        int RadiusChunks,
        List<EntityAgent> Entities);

    private sealed record PreviewOverride(string Pattern, int? Limit, int RadiusChunks);
    private sealed class PositionedAgent
    {
        public PositionedAgent(EntityAgent entity, double x, double z)
        {
            Entity = entity;
            X = x;
            Z = z;
        }

        public EntityAgent Entity { get; }
        public double X { get; }
        public double Z { get; }
    }

    private sealed class SweepResult
    {
        public int Scanned, Regions, OverLimitGroups, Protected, WouldRemove, Removed;
    }
}
