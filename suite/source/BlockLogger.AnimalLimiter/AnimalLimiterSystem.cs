using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using BlockLogger.Config;
using BlockLogger.Hooks;
using BlockLogger.Models;
using BlockLogger.Services;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace BlockLogger.AnimalLimiter;

public sealed class AnimalLimiterSystem : ModSystem
{
	private readonly struct ResolvedRuleCacheEntry(bool hasLimit, int limit, string rule)
	{
		public bool HasLimit { get; } = hasLimit;

		public int Limit { get; } = limit;

		public string Rule { get; } = rule;
	}

	private readonly struct BucketKey(int x, int z, string code, int radiusChunks) : IEquatable<BucketKey>
	{
		public int X { get; } = x;

		public int Z { get; } = z;

		public string Code { get; } = code;

		public int RadiusChunks { get; } = radiusChunks;

		public bool Equals(BucketKey other)
		{
			return X == other.X && Z == other.Z && RadiusChunks == other.RadiusChunks && string.Equals(Code, other.Code, StringComparison.OrdinalIgnoreCase);
		}

		public override bool Equals(object? obj)
		{
			return obj is BucketKey other && Equals(other);
		}

		public override int GetHashCode()
		{
			return HashCode.Combine(X, Z, RadiusChunks, StringComparer.OrdinalIgnoreCase.GetHashCode(Code));
		}
	}

	private sealed class EntityBucket
	{
		public readonly HashSet<long> EntityIds = new HashSet<long>();
	}

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

	private sealed record PopulationStatusRow(string Code, int RegionX, int RegionZ, string Rule, int Tracked, int Limit, int RadiusChunks, int Protected, int Eligible);

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

		public List<PopulationScanWorkItem> Items = new List<PopulationScanWorkItem>();

		public List<PopulationStatusRow> Rows = new List<PopulationStatusRow>();

		public List<string> Lines = new List<string>();

		public List<(double X, double Y, double Z)> PlayerPositions = new List<(double, double, double)>();
	}

	private sealed record PopulationScanWorkItem(string Code, int RegionX, int RegionZ, string Rule, int Limit, int RadiusChunks, List<EntityAgent> Entities);

	private sealed record PreviewOverride(string Pattern, int? Limit, int RadiusChunks);

	private sealed class PositionedAgent
	{
		public EntityAgent Entity { get; }

		public double X { get; }

		public double Z { get; }

		public PositionedAgent(EntityAgent entity, double x, double z)
		{
			Entity = entity;
			X = x;
			Z = z;
		}
	}

	private sealed class SweepResult
	{
		public int Scanned;

		public int Regions;

		public int OverLimitGroups;

		public int Protected;

		public int WouldRemove;

		public int Removed;
	}

	private const string ConfigFile = "NewfiesAnimalLimiter.json";

	private const string LegacyConfigFile = "animal_limiter.json";

	private const string HistoryFile = "NewfiesAnimalLimiter.removals.json";

	private ICoreServerAPI sapi = null;

	private AnimalLimiterConfig config = new AnimalLimiterConfig();

	private List<RemovalRecord> history = new List<RemovalRecord>();

	private int removalSequence;

	private bool historyDirty;

	private readonly object csvLock = new object();

	private readonly Dictionary<BucketKey, EntityBucket> entityBuckets = new Dictionary<BucketKey, EntityBucket>();

	private readonly Dictionary<long, BucketKey> entityBucketIndex = new Dictionary<long, BucketKey>();

	private readonly Dictionary<string, ResolvedRuleCacheEntry> resolvedRuleCache = new Dictionary<string, ResolvedRuleCacheEntry>(StringComparer.OrdinalIgnoreCase);

	private Timer? reconciliationTimer;

	private int reconciliationQueued;

	private long populationScanSequence;

	private long populationScanListenerId = -1L;

	private PopulationScanJob? populationScanJob;

	private bool usingSharedLifecycleBus;

	private const int PopulationScanMaxRegionalRows = 500;

	private const double PopulationScanTickBudgetMs = 4.0;

	private const int MaxResolvedRuleCacheEntries = 4096;

	public static AnimalLimiterSystem? Current { get; private set; }

	public int HealthTrackedRegionCount => entityBuckets.Count;

	public int HealthTrackedEntityCount => entityBucketIndex.Count;

	public AnimalLimiterConfig Config => config;

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

	public override bool ShouldLoad(EnumAppSide side)
	{
		return side == EnumAppSide.Server;
	}

	public override void StartServerSide(ICoreServerAPI api)
	{
		sapi = api;
		SuiteFeatureConfig suiteFeatureConfig = SuiteFeatureConfig.Load(api);
		if (!suiteFeatureConfig.AnimalLimiter)
		{
			api.Logger.Notification("[Newfie's Animal Limiter] Entire subsystem disabled by {0}; no entity observer, timer, command, or history service registered.", "NewfiesServerEssentialsFeatures.json");
			return;
		}
		Current = this;
		ValidateRuntimeApiShape();
		LoadConfig();
		LoadHistory();
		sapi.RegisterCommand("animallimit", "Configure and inspect Newfie's Animal Limiter", "/animallimit help", HandleCommand, "controlserver");
		sapi.Event.PlayerJoin += OnPlayerJoin;
		usingSharedLifecycleBus = suiteFeatureConfig.BlockLogger;
		if (usingSharedLifecycleBus)
		{
			EntityHooks.EntityObserved += ObserveEntity;
		}
		else
		{
			sapi.Event.OnEntitySpawn += OnFallbackEntitySpawn;
			sapi.Event.OnEntityDeath += OnFallbackEntityDeath;
			sapi.Event.OnEntityDespawn += OnFallbackEntityDespawn;
		}
		reconciliationTimer = new Timer((object? _) =>
		{
			QueueReconciliation();
		}, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
		RestartReconciliationTimer(initialStart: true);
		sapi.Logger.Notification("[Newfie's Animal Limiter] incremental mode started. Enabled={0}, LimitAll={1}, default={2}, radius={3} chunks, reconciliation={4}s", config.Enabled == true, config.LimitAllEntities, config.DefaultPerEntityLimit, config.RadiusChunks, config.SweepIntervalSeconds);
	}

	public override void Dispose()
	{
		if (Current == this)
		{
			Current = null;
		}
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
		if (sapi != null)
		{
			sapi.Event.PlayerJoin -= OnPlayerJoin;
		}
		entityBuckets.Clear();
		entityBucketIndex.Clear();
		resolvedRuleCache.Clear();
		if (historyDirty)
		{
			SaveHistory();
		}
		base.Dispose();
	}

	private EntityAgent? ResolveLoadedAgent(long entityId)
	{
		try
		{
			Entity value;
			return sapi.World.LoadedEntities.TryGetValue(entityId, out value) ? (value as EntityAgent) : null;
		}
		catch
		{
			return null;
		}
	}

	private void OnFallbackEntitySpawn(Entity entity)
	{
		ObserveEntity(entity, LogActionType.EntitySpawn);
	}

	private void OnFallbackEntityDeath(Entity entity, DamageSource damageSource)
	{
		ObserveEntity(entity, LogActionType.EntityDeath);
	}

	private void OnFallbackEntityDespawn(Entity entity, EntityDespawnData despawnData)
	{
		ObserveEntity(entity, LogActionType.EntityDespawn);
	}

	public void ObserveEntity(Entity entity, LogActionType action)
	{
		if (config.Enabled == true && entity is EntityAgent entityAgent && !(entity is EntityPlayer))
		{
			switch (action)
			{
			case LogActionType.EntitySpawn:
				TrackSpawn(entityAgent, evaluate: true);
				break;
			case LogActionType.EntityDeath:
			case LogActionType.EntityDespawn:
				UntrackEntity(entityAgent.EntityId);
				break;
			}
		}
	}

	private void TrackSpawn(EntityAgent entity, bool evaluate)
	{
		if (!entity.Alive)
		{
			return;
		}
		string code = CodeOf(entity);
		if (!ResolveLimitForCode(code, null, out string rule).HasValue)
		{
			return;
		}
		int radiusChunks = ResolveRadiusForRule(rule);
		if (TryGetBucketKey(entity, code, radiusChunks, out var key))
		{
			if (entityBucketIndex.TryGetValue(entity.EntityId, out var value) && !value.Equals(key))
			{
				RemoveFromBucket(value, entity.EntityId);
			}
			if (!entityBuckets.TryGetValue(key, out EntityBucket value2))
			{
				value2 = new EntityBucket();
				entityBuckets[key] = value2;
			}
			value2.EntityIds.Add(entity.EntityId);
			entityBucketIndex[entity.EntityId] = key;
			if (evaluate)
			{
				EvaluateBucket(key, value2);
			}
		}
	}

	private void UntrackEntity(long entityId)
	{
		if (entityBucketIndex.Remove(entityId, out var value))
		{
			RemoveFromBucket(value, entityId);
		}
	}

	private void RemoveFromBucket(BucketKey key, long entityId)
	{
		if (entityBuckets.TryGetValue(key, out EntityBucket value))
		{
			value.EntityIds.Remove(entityId);
			if (value.EntityIds.Count == 0)
			{
				entityBuckets.Remove(key);
			}
		}
	}

	private bool TryGetBucketKey(Entity entity, string code, int radiusChunks, out BucketKey key)
	{
		key = default;
		if (!EntityPositionResolver.TryGetCoordinates(entity, out var x, out var _, out var z, out var _))
		{
			return false;
		}
		radiusChunks = Math.Clamp(radiusChunks, 0, 32);
		int num = radiusChunks * 2 + 1;
		int b = Math.Max(32, num * 32);
		key = new BucketKey(FloorDiv((int)Math.Floor(x), b), FloorDiv((int)Math.Floor(z), b), code, radiusChunks);
		return true;
	}

	private void EvaluateBucket(BucketKey key, EntityBucket bucket)
	{
		int? num = ResolveLimitForCode(key.Code, null, out string rule);
		if (!num.HasValue || ResolveRadiusForRule(rule) != key.RadiusChunks)
		{
			DropBucket(key, bucket);
		}
		else
		{
			if (bucket.EntityIds.Count <= num.Value)
			{
				return;
			}
			List<long> list = new List<long>();
			List<EntityAgent> list2 = new List<EntityAgent>();
			foreach (long entityId in bucket.EntityIds)
			{
				EntityAgent entityAgent = ResolveLoadedAgent(entityId);
				string why;
				if (entityAgent == null || !entityAgent.Alive)
				{
					list.Add(entityId);
				}
				else if (!IsProtected(entityAgent, out why) && !IsInsideHardPlayerProtection(entityAgent))
				{
					list2.Add(entityAgent);
				}
			}
			foreach (long item in list)
			{
				bucket.EntityIds.Remove(item);
				entityBucketIndex.Remove(item);
			}
			if (list2.Count <= num.Value)
			{
				if (bucket.EntityIds.Count == 0)
				{
					entityBuckets.Remove(key);
				}
				return;
			}
			int count = list2.Count - num.Value;
			List<EntityAgent> source = (config.KeepNearestToPlayers ? list2.OrderByDescending(NearestPlayerDistanceSq).ToList() : list2.OrderByDescending((EntityAgent e) => e.EntityId).ToList());
			foreach (EntityAgent item2 in source.Take(count))
			{
				UntrackEntity(item2.EntityId);
				RemoveEntity(item2, rule, num.Value, list2.Count);
			}
			if (historyDirty)
			{
				SaveHistory();
			}
		}
	}

	private void DropBucket(BucketKey key, EntityBucket bucket)
	{
		foreach (long entityId in bucket.EntityIds)
		{
			entityBucketIndex.Remove(entityId);
		}
		entityBuckets.Remove(key);
	}

	private void QueueReconciliation()
	{
		if (config.Enabled != true || Interlocked.Exchange(ref reconciliationQueued, 1) != 0)
		{
			return;
		}
		try
		{
			sapi.Event.EnqueueMainThreadTask(() =>
			{
				try
				{
					ReconcileBuckets();
				}
				catch (Exception ex)
				{
					sapi.Logger.Warning("[AnimalLimiter] Reconciliation failed: {0}: {1}", ex.GetType().Name, ex.Message);
				}
				finally
				{
					Interlocked.Exchange(ref reconciliationQueued, 0);
				}
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
		List<EntityAgent> list = GatherLoadedAgentsNearPlayers();
		foreach (EntityAgent item in list)
		{
			TrackSpawn(item, evaluate: false);
		}
		foreach (KeyValuePair<BucketKey, EntityBucket> item2 in entityBuckets.ToList())
		{
			EvaluateBucket(item2.Key, item2.Value);
		}
	}

	private void RestartReconciliationTimer(bool initialStart = false)
	{
		if (reconciliationTimer != null)
		{
			if (config.Enabled != true)
			{
				reconciliationTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
				return;
			}
			int num = Math.Max(60, config.SweepIntervalSeconds);
			TimeSpan dueTime = (initialStart ? TimeSpan.FromSeconds(30L) : TimeSpan.FromSeconds(num));
			reconciliationTimer.Change(dueTime, TimeSpan.FromSeconds(num));
		}
	}

	private void LoadConfig()
	{
		AnimalLimiterConfig animalLimiterConfig = ModConfigRecovery.LoadOrNull<AnimalLimiterConfig>(sapi, "NewfiesAnimalLimiter.json", "AnimalLimiter");
		if (animalLimiterConfig != null)
		{
			if (!animalLimiterConfig.Enabled.HasValue)
			{
				animalLimiterConfig.Enabled = true;
			}
			config = animalLimiterConfig;
		}
		else
		{
			AnimalLimiterConfig animalLimiterConfig2 = MigrateLegacyConfig();
			if (animalLimiterConfig2 != null)
			{
				animalLimiterConfig2.Enabled = true;
				config = animalLimiterConfig2;
			}
			else
			{
				config = new AnimalLimiterConfig
				{
					Enabled = false,
					DefaultPerEntityLimit = 100
				};
			}
		}
		config.SchemaVersion = Math.Max(2, config.SchemaVersion);
		NormalizeConfig();
		SaveConfig();
	}

	private AnimalLimiterConfig? MigrateLegacyConfig()
	{
		LegacyAnimalLimiterConfig legacyAnimalLimiterConfig = ModConfigRecovery.LoadOrNull<LegacyAnimalLimiterConfig>(sapi, "animal_limiter.json", "AnimalLimiter");
		if (legacyAnimalLimiterConfig == null)
		{
			return null;
		}
		AnimalLimiterConfig animalLimiterConfig = new AnimalLimiterConfig
		{
			LimitAllEntities = (legacyAnimalLimiterConfig.IncludeCodeStartsWith?.Any((string x) => string.IsNullOrEmpty(x)) ?? false),
			DefaultPerEntityLimit = Math.Max(0, legacyAnimalLimiterConfig.PerCodeLimitPerChunk),
			RadiusChunks = 0,
			ProtectPets = legacyAnimalLimiterConfig.ProtectTamedOrOwned,
			KeepNearestToPlayers = legacyAnimalLimiterConfig.KeepNearestToPlayers,
			LogActions = legacyAnimalLimiterConfig.LogActions,
			LogToCsv = legacyAnimalLimiterConfig.LogToCsv,
			CsvDirName = (string.IsNullOrWhiteSpace(legacyAnimalLimiterConfig.CsvDirName) ? "AnimalLimiterLogs" : legacyAnimalLimiterConfig.CsvDirName),
			CsvFilePrefix = (string.IsNullOrWhiteSpace(legacyAnimalLimiterConfig.CsvFilePrefix) ? "animal-removals" : legacyAnimalLimiterConfig.CsvFilePrefix)
		};
		if (legacyAnimalLimiterConfig.PerCodeOverrides != null)
		{
			foreach (KeyValuePair<string, int> perCodeOverride in legacyAnimalLimiterConfig.PerCodeOverrides)
			{
				animalLimiterConfig.Rules[NormalizeLegacyPrefix(perCodeOverride.Key)] = Math.Max(0, perCodeOverride.Value);
			}
		}
		if (legacyAnimalLimiterConfig.IncludeCodeStartsWith != null)
		{
			foreach (string item in legacyAnimalLimiterConfig.IncludeCodeStartsWith.Where((string x) => !string.IsNullOrWhiteSpace(x)))
			{
				string key = NormalizeLegacyPrefix(item);
				if (!animalLimiterConfig.Rules.ContainsKey(key))
				{
					animalLimiterConfig.Rules[key] = animalLimiterConfig.DefaultPerEntityLimit;
				}
			}
		}
		if (legacyAnimalLimiterConfig.ProtectedCodeStartsWith != null)
		{
			foreach (string item2 in legacyAnimalLimiterConfig.ProtectedCodeStartsWith.Where((string x) => !string.IsNullOrWhiteSpace(x)))
			{
				animalLimiterConfig.ExcludeRules.Add(NormalizeLegacyPrefix(item2));
			}
		}
		sapi.Logger.Notification("[AnimalLimiter] Migrated legacy {0} settings into {1}. Legacy radius semantics are preserved as radius 0 until changed.", "animal_limiter.json", "NewfiesAnimalLimiter.json");
		return animalLimiterConfig;
	}

	private static string NormalizeLegacyPrefix(string prefix)
	{
		string text = NormalizePattern(prefix);
		return text.Contains('*') ? text : (text + "*");
	}

	private void NormalizeConfig()
	{
		config.SchemaVersion = Math.Max(2, config.SchemaVersion);
		config.DefaultPerEntityLimit = Math.Max(0, config.DefaultPerEntityLimit);
		config.RadiusChunks = Math.Clamp(config.RadiusChunks, 0, 32);
		config.SweepIntervalSeconds = Math.Clamp(config.SweepIntervalSeconds, 60, 3600);
		config.PlayerProtectionRadiusBlocks = Math.Max(0, config.PlayerProtectionRadiusBlocks);
		config.MaxRemovalHistory = Math.Clamp(config.MaxRemovalHistory, 10, 100000);
		config.RemovalHistoryDays = Math.Clamp(config.RemovalHistoryDays, 1, 3650);
		AnimalLimiterConfig animalLimiterConfig = config;
		bool? enabled = animalLimiterConfig.Enabled;
		bool valueOrDefault = enabled == true;
		if (!enabled.HasValue)
		{
			valueOrDefault = false;
			AnimalLimiterConfig animalLimiterConfig2 = animalLimiterConfig;
			bool? enabled2 = valueOrDefault;
			animalLimiterConfig2.Enabled = enabled2;
		}
		animalLimiterConfig = config;
		if (animalLimiterConfig.Rules == null)
		{
			Dictionary<string, int> dictionary = (animalLimiterConfig.Rules = new Dictionary<string, int>());
		}
		animalLimiterConfig = config;
		if (animalLimiterConfig.RuleRadiusChunks == null)
		{
			Dictionary<string, int> dictionary = (animalLimiterConfig.RuleRadiusChunks = new Dictionary<string, int>());
		}
		animalLimiterConfig = config;
		if (animalLimiterConfig.ExcludeRules == null)
		{
			List<string> list = (animalLimiterConfig.ExcludeRules = new List<string>());
		}
		animalLimiterConfig = config;
		if (animalLimiterConfig.OwnerAttributeKeys == null)
		{
			List<string> list = (animalLimiterConfig.OwnerAttributeKeys = new List<string>());
		}
		animalLimiterConfig = config;
		if (animalLimiterConfig.TamedAttributeKeys == null)
		{
			List<string> list = (animalLimiterConfig.TamedAttributeKeys = new List<string>());
		}
		animalLimiterConfig = config;
		if (animalLimiterConfig.NameAttributeKeys == null)
		{
			List<string> list = (animalLimiterConfig.NameAttributeKeys = new List<string>());
		}
		animalLimiterConfig = config;
		if (animalLimiterConfig.GenerationAttributeKeys == null)
		{
			List<string> list = (animalLimiterConfig.GenerationAttributeKeys = new List<string>());
		}
		AnimalLimiterConfig animalLimiterConfig3 = config;
		string text = (config.GenerationFilter ?? "ProtectBred").ToLowerInvariant();
		if (1 == 0)
		{
		}
		string generationFilter = text switch
		{
			"off" => "Off", 
			"wildonly" => "WildOnly", 
			"bredonly" => "BredOnly", 
			"protectbred" => "ProtectBred", 
			_ => "ProtectBred", 
		};
		if (1 == 0)
		{
		}
		animalLimiterConfig3.GenerationFilter = generationFilter;
		config.Rules = config.Rules.Where((KeyValuePair<string, int> kv) => !string.IsNullOrWhiteSpace(kv.Key)).GroupBy((KeyValuePair<string, int> kv) => NormalizePattern(kv.Key), StringComparer.OrdinalIgnoreCase).ToDictionary((IGrouping<string, KeyValuePair<string, int>> g) => g.Key, (IGrouping<string, KeyValuePair<string, int>> g) => Math.Max(0, g.Last().Value), StringComparer.OrdinalIgnoreCase);
		config.RuleRadiusChunks = (from kv in config.RuleRadiusChunks
			where !string.IsNullOrWhiteSpace(kv.Key)
			select new KeyValuePair<string, int>(NormalizePattern(kv.Key), Math.Clamp(kv.Value, 0, 32)) into kv
			where config.Rules.ContainsKey(kv.Key)
			select kv).GroupBy((KeyValuePair<string, int> kv) => kv.Key, StringComparer.OrdinalIgnoreCase).ToDictionary((IGrouping<string, KeyValuePair<string, int>> g) => g.Key, (IGrouping<string, KeyValuePair<string, int>> g) => g.Last().Value, StringComparer.OrdinalIgnoreCase);
		config.ExcludeRules = config.ExcludeRules.Where((string x) => !string.IsNullOrWhiteSpace(x)).Select(NormalizePattern).Distinct(StringComparer.OrdinalIgnoreCase)
			.ToList();
		if (!config.ExcludeRules.Any((string x) => x.Equals("game:player", StringComparison.OrdinalIgnoreCase)))
		{
			config.ExcludeRules.Add("game:player");
		}
	}

	private void SaveConfig()
	{
		resolvedRuleCache.Clear();
		SuiteConfigStorage.Store(sapi, config, "NewfiesAnimalLimiter.json");
	}

	private void LoadHistory()
	{
		history = ModConfigRecovery.LoadOrNull<List<RemovalRecord>>(sapi, "NewfiesAnimalLimiter.removals.json", "AnimalLimiterHistory") ?? new List<RemovalRecord>();
		removalSequence = history.Select((RemovalRecord r) => ParseRemovalSequence(r.RemovalId)).DefaultIfEmpty(0).Max();
		if (CleanupHistory())
		{
			SaveHistory();
		}
	}

	private void SaveHistory()
	{
		SuiteConfigStorage.Store(sapi, history, "NewfiesAnimalLimiter.removals.json");
		historyDirty = false;
	}

	private bool CleanupHistory()
	{
		int count = history.Count;
		long cutoff = DateTimeOffset.UtcNow.AddDays(-config.RemovalHistoryDays).ToUnixTimeSeconds();
		history.RemoveAll((RemovalRecord r) => r.RemovedUnixSeconds < cutoff);
		if (history.Count > config.MaxRemovalHistory)
		{
			history = (from r in history.OrderByDescending((RemovalRecord r) => r.RemovedUnixSeconds).Take(config.MaxRemovalHistory)
				orderby r.RemovedUnixSeconds
				select r).ToList();
		}
		bool flag = history.Count != count;
		if (flag)
		{
			historyDirty = true;
		}
		return flag;
	}

	private void OnPlayerJoin(IServerPlayer _)
	{
		if (CleanupHistory())
		{
			SaveHistory();
		}
	}

	private void ValidateRuntimeApiShape()
	{
		if (EntityPositionResolver.RuntimeMemberName == "unavailable")
		{
			throw new InvalidOperationException("Animal Limiter could not resolve a runtime entity position member.");
		}
	}

	private void HandleCommand(IServerPlayer player, int groupId, CmdArgs args)
	{
		string text = (args.PopWord() ?? "help").ToLowerInvariant();
		try
		{
			switch (text)
			{
			case "help":
				SendHelp(player, groupId);
				break;
			case "status":
				SendStatus(player, groupId);
				break;
			case "all":
				CmdAll(player, groupId, args);
				break;
			case "default":
				CmdDefault(player, groupId, args);
				break;
			case "radius":
				CmdRadius(player, groupId, args);
				break;
			case "interval":
				CmdInterval(player, groupId, args);
				break;
			case "set":
				CmdSet(player, groupId, args);
				break;
			case "target":
			case "look":
				CmdTarget(player, groupId, args);
				break;
			case "remove":
				CmdRemove(player, groupId, args);
				break;
			case "exclude":
				CmdExclude(player, groupId, args);
				break;
			case "include":
				CmdInclude(player, groupId, args);
				break;
			case "protect":
				CmdProtect(player, groupId, args);
				break;
			case "generation":
				CmdGeneration(player, groupId, args);
				break;
			case "list":
				CmdList(player, groupId);
				break;
			case "find":
				CmdFind(player, groupId, args);
				break;
			case "matches":
				CmdMatches(player, groupId, args);
				break;
			case "top":
				CmdTop(player, groupId, args);
				break;
			case "preview":
				CmdPreview(player, groupId, args);
				break;
			case "cull":
				CmdCull(player, groupId);
				break;
			case "history":
				CmdHistory(player, groupId, args);
				break;
			case "return":
				CmdReturn(player, groupId, args);
				break;
			case "reload":
				LoadConfig();
				RestartReconciliationTimer();
				InvalidateBucketsAndReconcile();
				Msg(player, groupId, "Config reloaded.");
				break;
			default:
				Msg(player, groupId, "Unknown subcommand. Use /animallimit help");
				break;
			}
		}
		catch (Exception ex)
		{
			sapi.Logger.Error("[AnimalLimiter] Command error: {0}", ex);
			Msg(player, groupId, "Command failed: " + ex.Message);
		}
	}

	private void SendHelp(IServerPlayer p, int g)
	{
		Msg(p, g, "Animal Limiter commands:\n/animallimit status\n/animallimit all on|off\n/animallimit default <limit>\n/animallimit radius <chunks>\n/animallimit interval <reconciliation-seconds>\n/animallimit set <entityid|wildcard> <limit> [radius]\n/animallimit target <limit> [radius]  (limit the entity you are looking at)\n/animallimit remove <pattern>\n/animallimit exclude <pattern> | include <pattern>\n/animallimit protect pets|farm|named on|off\n/animallimit generation off|wildonly|bredonly|protectbred\n/animallimit find <text> | matches <pattern>\n/animallimit top [pattern]\n/animallimit preview [pattern] [limit]\n/animallimit cull\n/animallimit history [count]\n/animallimit return last|<removal-id>");
	}

	private void SendStatus(IServerPlayer p, int g)
	{
		Msg(p, g, $"Limit all entity types: {config.LimitAllEntities}; default/type: {config.DefaultPerEntityLimit}; radius: {config.RadiusChunks} chunks; reconcile: {config.SweepIntervalSeconds}s; rules: {config.Rules.Count}; excludes: {config.ExcludeRules.Count}; protect pets={config.ProtectPets}, farm={config.ProtectFarmAnimals}, named={config.ProtectNamedEntities}; generation={config.GenerationFilter}");
	}

	private void CmdAll(IServerPlayer p, int g, CmdArgs a)
	{
		string text = (a.PopWord() ?? "").ToLowerInvariant();
		if (text != "on" && text != "off")
		{
			Msg(p, g, "Usage: /animallimit all on|off");
			return;
		}
		config.LimitAllEntities = text == "on";
		SaveConfig();
		Msg(p, g, "Universal per-entity limiting is now " + text.ToUpperInvariant() + ".");
	}

	private void CmdDefault(IServerPlayer p, int g, CmdArgs a)
	{
		if (!int.TryParse(a.PopWord(), out var result) || result < 0)
		{
			Msg(p, g, "Usage: /animallimit default <0+>");
			return;
		}
		config.DefaultPerEntityLimit = result;
		SaveConfig();
		Msg(p, g, $"Default per-entity limit set to {result}.");
	}

	private void CmdRadius(IServerPlayer p, int g, CmdArgs a)
	{
		if (!int.TryParse(a.PopWord(), out var result) || result < 0 || result > 32)
		{
			Msg(p, g, "Usage: /animallimit radius <0-32 chunks>");
			return;
		}
		config.RadiusChunks = result;
		SaveConfig();
		InvalidateBucketsAndReconcile();
		Msg(p, g, $"Default radius set to {result} chunks ({result * 2 + 1}x{result * 2 + 1} chunk regions).");
	}

	private void CmdInterval(IServerPlayer p, int g, CmdArgs a)
	{
		if (!int.TryParse(a.PopWord(), out var result) || result < 60)
		{
			Msg(p, g, "Usage: /animallimit interval <reconciliation seconds, minimum 60>");
			return;
		}
		config.SweepIntervalSeconds = Math.Min(3600, result);
		SaveConfig();
		RestartReconciliationTimer();
		Msg(p, g, $"Reconciliation interval set to {config.SweepIntervalSeconds}s. Normal limiting remains event-driven.");
	}

	private void CmdSet(IServerPlayer p, int g, CmdArgs a)
	{
		string text = NormalizePattern(a.PopWord());
		if (string.IsNullOrWhiteSpace(text) || !int.TryParse(a.PopWord(), out var result) || result < 0)
		{
			Msg(p, g, "Usage: /animallimit set <entityid|wildcard> <limit> [radius]");
			return;
		}
		int result2 = config.RadiusChunks;
		string text2 = a.PopWord();
		if (!string.IsNullOrWhiteSpace(text2) && (!int.TryParse(text2, out result2) || result2 < 0 || result2 > 32))
		{
			Msg(p, g, "Radius must be 0-32 chunks.");
		}
		else
		{
			Msg(p, g, SetRuleFromGui(text, result, result2));
		}
	}

	private void CmdTarget(IServerPlayer p, int g, CmdArgs a)
	{
		if (!int.TryParse(a.PopWord(), out var result) || result < 0)
		{
			Msg(p, g, "Usage: /animallimit target <limit> [radius]");
			return;
		}
		int result2 = config.RadiusChunks;
		string text = a.PopWord();
		if (!string.IsNullOrWhiteSpace(text) && (!int.TryParse(text, out result2) || result2 < 0 || result2 > 32))
		{
			Msg(p, g, "Radius must be 0-32 chunks.");
		}
		else
		{
			Msg(p, g, SetTargetRule(p, result, result2));
		}
	}

	public string SetTargetRule(IServerPlayer player, int limit, int radiusChunks)
	{
		limit = Math.Max(0, limit);
		radiusChunks = Math.Clamp(radiusChunks, 0, 32);
		Entity entity = player.CurrentEntitySelection?.Entity;
		if (entity == null)
		{
			return "Look directly at an entity first.";
		}
		if (entity is EntityPlayer)
		{
			return "Players cannot be added to the animal limiter.";
		}
		string text = CodeOf(entity);
		if (string.IsNullOrWhiteSpace(text) || text == "unknown:unknown")
		{
			return "Could not resolve that entity's code.";
		}
		config.Rules[text] = limit;
		config.RuleRadiusChunks[text] = radiusChunks;
		SaveConfig();
		InvalidateBucketsAndReconcile();
		return $"Rule set from target: {text} | limit {limit} | radius {radiusChunks} chunks.";
	}

	public string SetRuleFromGui(string? pattern, int limit, int radiusChunks)
	{
		pattern = NormalizePattern(pattern);
		if (string.IsNullOrWhiteSpace(pattern))
		{
			return "Enter an entity code or wildcard pattern.";
		}
		limit = Math.Max(0, limit);
		radiusChunks = Math.Clamp(radiusChunks, 0, 32);
		config.Rules[pattern] = limit;
		config.RuleRadiusChunks[pattern] = radiusChunks;
		SaveConfig();
		InvalidateBucketsAndReconcile();
		return $"Rule set: {pattern} | limit {limit} | radius {radiusChunks} chunks. Matches {CountRegisteredMatches(pattern)} registered entity types.";
	}

	public string RemoveRuleFromGui(string? pattern)
	{
		pattern = NormalizePattern(pattern);
		if (string.IsNullOrWhiteSpace(pattern))
		{
			return "Enter the exact stored rule to remove.";
		}
		bool flag = config.Rules.Remove(pattern);
		config.RuleRadiusChunks.Remove(pattern);
		if (flag)
		{
			SaveConfig();
			InvalidateBucketsAndReconcile();
		}
		return flag ? ("Removed rule " + pattern + ".") : ("No exact stored rule named " + pattern + ".");
	}

	public string AddExclusionFromGui(string? pattern)
	{
		pattern = NormalizePattern(pattern);
		if (string.IsNullOrWhiteSpace(pattern))
		{
			return "Enter an entity code or wildcard pattern to exclude.";
		}
		if (pattern.Equals("game:player", StringComparison.OrdinalIgnoreCase))
		{
			return "game:player is already permanently protected by the default exclusion.";
		}
		if (config.ExcludeRules.Any((string x) => x.Equals(pattern, StringComparison.OrdinalIgnoreCase)))
		{
			return "Exclusion " + pattern + " already exists.";
		}
		config.ExcludeRules.Add(pattern);
		SaveConfig();
		InvalidateBucketsAndReconcile();
		return $"Excluded {pattern} from Animal Limiter enforcement. Matches {CountRegisteredMatches(pattern)} registered entity types.";
	}

	public string RemoveExclusionFromGui(string? pattern)
	{
		pattern = NormalizePattern(pattern);
		if (string.IsNullOrWhiteSpace(pattern))
		{
			return "Enter the exact stored exclusion to remove.";
		}
		if (pattern.Equals("game:player", StringComparison.OrdinalIgnoreCase))
		{
			return "The built-in game:player exclusion cannot be removed from the WebPanel.";
		}
		int num = config.ExcludeRules.RemoveAll((string x) => x.Equals(pattern, StringComparison.OrdinalIgnoreCase));
		if (num > 0)
		{
			SaveConfig();
			InvalidateBucketsAndReconcile();
		}
		return (num > 0) ? ("Removed exclusion " + pattern + ".") : ("No exact exclusion named " + pattern + ".");
	}

	public AnimalLimiterRuleSnapshot GetWebRuleSnapshot()
	{
		List<AnimalLimiterRuleInfo> rules = config.Rules.OrderByDescending((KeyValuePair<string, int> x) => Specificity(x.Key)).ThenBy((KeyValuePair<string, int> x) => x.Key, StringComparer.OrdinalIgnoreCase).Select((KeyValuePair<string, int> x) =>
		{
			bool flag = config.RuleRadiusChunks.TryGetValue(x.Key, out var value);
			int radiusChunks = (flag ? Math.Clamp(value, 0, 32) : Math.Clamp(config.RadiusChunks, 0, 32));
			return new AnimalLimiterRuleInfo
			{
				Pattern = x.Key,
				Limit = x.Value,
				RadiusChunks = radiusChunks,
				UsesDefaultRadius = !flag,
				RegisteredMatches = CountRegisteredMatches(x.Key)
			};
		})
			.ToList();
		return new AnimalLimiterRuleSnapshot
		{
			Enabled = (config.Enabled == true),
			LimitAllEntities = config.LimitAllEntities,
			DefaultLimit = config.DefaultPerEntityLimit,
			DefaultRadiusChunks = config.RadiusChunks,
			Exclusions = config.ExcludeRules.OrderBy((string x) => x, StringComparer.OrdinalIgnoreCase).ToList(),
			Rules = rules
		};
	}

	public string PreviewFromGui(string? pattern, int? limit, int radiusChunks)
	{
		pattern = NormalizePattern(pattern);
		PreviewOverride previewOverride = (string.IsNullOrWhiteSpace(pattern) ? null : new PreviewOverride(pattern, limit, Math.Clamp(radiusChunks, 0, 32)));
		SweepResult sweepResult = SweepAllPlayerRegions(preview: true, previewOverride);
		return $"Preview: scanned {sweepResult.Scanned}; regions {sweepResult.Regions}; over-limit groups {sweepResult.OverLimitGroups}; would remove {sweepResult.WouldRemove}; protected/skipped {sweepResult.Protected}. Nothing was removed.";
	}

	public string CullFromGui()
	{
		SweepResult sweepResult = SweepAllPlayerRegions(preview: false, null);
		return $"Cull complete: scanned {sweepResult.Scanned}; removed {sweepResult.Removed}; protected/skipped {sweepResult.Protected}; regions {sweepResult.Regions}.";
	}

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
		case "limitallentities":
			config.LimitAllEntities = value;
			break;
		case "protectpets":
			config.ProtectPets = value;
			break;
		case "protectfarmanimals":
			config.ProtectFarmAnimals = value;
			break;
		case "protectnamedentities":
			config.ProtectNamedEntities = value;
			break;
		case "keepnearesttoplayers":
			config.KeepNearestToPlayers = value;
			break;
		case "logactions":
			config.LogActions = value;
			break;
		case "logtocsv":
			config.LogToCsv = value;
			break;
		default:
			return "Unknown Animal Limiter toggle.";
		}
		SaveConfig();
		QueueReconciliation();
		return "Animal Limiter setting " + (value ? "enabled" : "disabled") + ".";
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
		PopulationScanJob populationScanJob = this.populationScanJob;
		if (populationScanJob != null && !populationScanJob.Completed)
		{
			if (populationScanJob.RecommendationScan == includeUnruledEntities)
			{
				return SnapshotPopulationScanState(populationScanJob);
			}
			return new AnimalPopulationScanState
			{
				ScanId = populationScanJob.Id,
				Completed = true,
				Error = "Another Animal Limiter population scan is already running. Wait for it to finish before starting a different scan mode."
			};
		}
		StopPopulationScanListener();
		PopulationScanJob populationScanJob2 = new PopulationScanJob
		{
			Id = Interlocked.Increment(ref populationScanSequence),
			StartedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
			Mode = (includeUnruledEntities ? "Recommendation scan (all loaded agents near players)" : ((config.Enabled == true) ? "Live tracked buckets" : "Preview snapshot (limiter disabled)")),
			RecommendationScan = includeUnruledEntities,
			PlayerPositions = CaptureOnlinePlayerPositions()
		};
		try
		{
			ref List<PopulationScanWorkItem> items = ref populationScanJob2.Items;
			List<PopulationScanWorkItem> list;
			if (includeUnruledEntities)
			{
				list = BuildPopulationScanWorkFromLoadedSnapshot(includeUnruledEntities: true);
			}
			else
			{
				list = ((config.Enabled == true) ? BuildPopulationScanWorkFromTrackedBuckets() : BuildPopulationScanWorkFromLoadedSnapshot(includeUnruledEntities: false));
			}
			items = list;
			populationScanJob2.TotalEntities = populationScanJob2.Items.Sum((PopulationScanWorkItem x) => x.Entities.Count);
			populationScanJob2.TotalRegions = populationScanJob2.Items.Count;
			this.populationScanJob = populationScanJob2;
			if (populationScanJob2.TotalRegions == 0 || populationScanJob2.TotalEntities == 0)
			{
				CompletePopulationScan(populationScanJob2);
			}
			else
			{
				populationScanListenerId = sapi.Event.RegisterGameTickListener((float _) =>
				{
					ProcessPopulationScanTick();
				}, (Exception ex2) =>
				{
					FailPopulationScan(ex2);
				}, 20);
			}
		}
		catch (Exception ex)
		{
			this.populationScanJob = populationScanJob2;
			FailPopulationScan(ex);
		}
		return SnapshotPopulationScanState(populationScanJob2);
	}

	public AnimalPopulationScanState GetPopulationScanState(long scanId)
	{
		PopulationScanJob populationScanJob = this.populationScanJob;
		if (populationScanJob == null || (scanId > 0 && populationScanJob.Id != scanId))
		{
			return new AnimalPopulationScanState
			{
				ScanId = scanId,
				Completed = true,
				Error = "Population scan was not found or has expired. Start a new scan."
			};
		}
		return SnapshotPopulationScanState(populationScanJob);
	}

	public AnimalLimiterRecommendationSnapshot GetWebRecommendations(long scanId)
	{
		PopulationScanJob populationScanJob = this.populationScanJob;
		if (populationScanJob == null || (scanId > 0 && populationScanJob.Id != scanId))
		{
			return new AnimalLimiterRecommendationSnapshot
			{
				ScanId = scanId,
				Error = "Recommendation scan was not found or has expired. Run Analyze & Recommend again."
			};
		}
		if (!populationScanJob.RecommendationScan)
		{
			return new AnimalLimiterRecommendationSnapshot
			{
				ScanId = populationScanJob.Id,
				Error = "The most recent scan was a normal limiter population scan. Run Analyze & Recommend to inspect unruled entities too."
			};
		}
		if (!populationScanJob.Completed)
		{
			return new AnimalLimiterRecommendationSnapshot
			{
				ScanId = populationScanJob.Id,
				Error = "Recommendation scan is still running."
			};
		}
		if (!string.IsNullOrWhiteSpace(populationScanJob.Error))
		{
			return new AnimalLimiterRecommendationSnapshot
			{
				ScanId = populationScanJob.Id,
				Error = populationScanJob.Error
			};
		}
		List<AnimalLimiterRecommendationInfo> list = new List<AnimalLimiterRecommendationInfo>();
		foreach (IGrouping<string, PopulationStatusRow> item in populationScanJob.Rows.GroupBy((PopulationStatusRow x) => x.Code, StringComparer.OrdinalIgnoreCase))
		{
			List<int> list2 = (from x in item
				select Math.Max(0, x.Eligible) into x
				orderby x
				select x).ToList();
			if (list2.Count == 0)
			{
				continue;
			}
			int num = list2.Sum();
			int num2 = list2[list2.Count - 1];
			if (num2 < 8 || num < 12)
			{
				continue;
			}
			double num3 = list2.Average();
			int num4 = PercentileNearestRank(list2, 0.5);
			int num5 = PercentileNearestRank(list2, 0.75);
			int suggested;
			string confidence;
			if (list2.Count >= 3)
			{
				suggested = Math.Max(4, (int)Math.Ceiling(Math.Max((double)num4 * 1.75, (double)num5 * 1.25)));
				confidence = ((list2.Count >= 5) ? "High" : "Medium");
			}
			else if (list2.Count == 2)
			{
				suggested = Math.Max(4, (int)Math.Ceiling(num3 * 1.15));
				confidence = "Medium";
			}
			else
			{
				suggested = Math.Max(4, (int)Math.Ceiling((double)num2 * 0.8));
				confidence = "Low";
			}
			suggested = Math.Min(suggested, Math.Max(0, num2 - 1));
			if (suggested < 0)
			{
				continue;
			}
			int num6 = list2.Sum((int x) => Math.Max(0, x - suggested));
			if (num6 <= 0)
			{
				continue;
			}
			string key = item.Key;
			int? effectiveLimit = ResolveLimitForCode(key, null, out string rule);
			bool hasValue = effectiveLimit.HasValue;
			if (!effectiveLimit.HasValue || effectiveLimit.Value > suggested)
			{
				int value = item.Select((PopulationStatusRow x) => x.RadiusChunks).DefaultIfEmpty(config.RadiusChunks).First();
				int count = list2.Count;
				if (1 == 0)
				{
				}
				string text;
				if (count >= 5)
				{
					text = $"Observed {list2.Count} active regions. Typical eligible population was about {num4}-{num5}, while the hotspot reached {num2}. The suggested cap keeps headroom above the typical range while trimming the hotspot.";
				}
				else if (count >= 3)
				{
					text = $"Observed {list2.Count} active regions. Typical eligible population was about {num4}-{num5}, with a peak of {num2}. This is a conservative cap with headroom above the normal range.";
				}
				else
				{
					text = ((count != 2) ? $"Only one active region was observed with {num2} eligible entities. This low-confidence suggestion trims roughly 20% of that hotspot; review it carefully before applying." : $"Only two active regions were observed (average {num3:0.0}, peak {num2}), so this is a cautious recommendation and should be reviewed before applying.");
				}
				if (1 == 0)
				{
				}
				string reason = text;
				list.Add(new AnimalLimiterRecommendationInfo
				{
					Pattern = key,
					SuggestedLimit = suggested,
					RadiusChunks = Math.Clamp(value, 0, 32),
					RegionsObserved = list2.Count,
					TotalEligible = num,
					PeakEligible = num2,
					AverageEligible = num3,
					MedianEligible = num4,
					UpperQuartileEligible = num5,
					EstimatedWouldRemoveNow = num6,
					HasEffectiveRule = hasValue,
					EffectiveRule = (hasValue ? rule : ""),
					EffectiveLimit = effectiveLimit,
					Confidence = confidence,
					Reason = reason
				});
			}
		}
		list = (from x in list
			orderby x.EstimatedWouldRemoveNow descending, x.PeakEligible descending
			select x).ThenBy((AnimalLimiterRecommendationInfo x) => x.Pattern, StringComparer.OrdinalIgnoreCase).Take(100).ToList();
		return new AnimalLimiterRecommendationSnapshot
		{
			ScanId = populationScanJob.Id,
			Ready = true,
			Note = ((list.Count == 0) ? "No strong rule candidates were found in the currently loaded player areas. Recommendations are intentionally conservative." : "Recommendations are based only on currently loaded non-player agents near online players. Review and preview the estimated impact before applying; NSE never auto-applies these suggestions."),
			Recommendations = list
		};
	}

	private static int PercentileNearestRank(List<int> sortedValues, double percentile)
	{
		if (sortedValues.Count == 0)
		{
			return 0;
		}
		int index = Math.Clamp((int)Math.Ceiling((double)sortedValues.Count * percentile) - 1, 0, sortedValues.Count - 1);
		return sortedValues[index];
	}

	public List<string> GetGuiPopulationLines()
	{
		bool valueOrDefault = config.Enabled == true;
		List<PopulationStatusRow> rows = (valueOrDefault ? BuildPopulationRowsFromTrackedBucketsImmediate() : BuildPopulationRowsFromLoadedSnapshotImmediate());
		return BuildPopulationLines(valueOrDefault ? "Live tracked buckets" : "Preview snapshot (limiter disabled)", rows, 500);
	}

	private List<PopulationScanWorkItem> BuildPopulationScanWorkFromTrackedBuckets()
	{
		List<PopulationScanWorkItem> list = new List<PopulationScanWorkItem>();
		foreach (KeyValuePair<BucketKey, EntityBucket> item in entityBuckets.ToList())
		{
			BucketKey key = item.Key;
			int? num = ResolveLimitForCode(key.Code, null, out string rule);
			if (num.HasValue && ResolveRadiusForRule(rule) == key.RadiusChunks)
			{
				List<EntityAgent> list2 = (from x in item.Value.EntityIds.Select(ResolveLoadedAgent)
					where x?.Alive ?? false
					select (x)).ToList();
				if (list2.Count != 0)
				{
					list.Add(new PopulationScanWorkItem(key.Code, key.X, key.Z, rule, num.Value, key.RadiusChunks, list2));
				}
			}
		}
		return list;
	}

	private List<PopulationScanWorkItem> BuildPopulationScanWorkFromLoadedSnapshot(bool includeUnruledEntities)
	{
		List<PopulationScanWorkItem> list = new List<PopulationScanWorkItem>();
		int num = 32;
		List<EntityAgent> source = GatherLoadedAgentsNearPlayers();
		foreach (IGrouping<string, EntityAgent> item in source.GroupBy(CodeOf, StringComparer.OrdinalIgnoreCase))
		{
			string code = item.Key;
			int? num2 = ResolveLimitForCode(code, null, out string rule);
			if (!num2.HasValue)
			{
				if (!includeUnruledEntities || config.ExcludeRules.Any((string pattern) => GlobMatch(code, pattern)))
				{
					continue;
				}
				rule = "<unruled>";
				num2 = Math.Max(0, config.DefaultPerEntityLimit);
			}
			int num3 = ((rule == "<unruled>") ? Math.Clamp(config.RadiusChunks, 0, 32) : ResolveRadiusForRule(rule));
			int num4 = num3 * 2 + 1;
			int regionBlocks = Math.Max(num, num4 * num);
			foreach (IGrouping<(int, int), PositionedAgent> item2 in from entity in item
				select (!EntityPositionResolver.TryGetCoordinates(entity, out var x, out var _, out var z, out var _)) ? null : new PositionedAgent(entity, x, z) into x
				where x != null
				select (x) into x
				group x by (X: FloorDiv((int)Math.Floor(x.X), regionBlocks), Z: FloorDiv((int)Math.Floor(x.Z), regionBlocks)))
			{
				List<EntityAgent> list2 = item2.Select((PositionedAgent x) => x.Entity).ToList();
				if (list2.Count != 0)
				{
					list.Add(new PopulationScanWorkItem(code, item2.Key.Item1, item2.Key.Item2, rule, num2.Value, num3, list2));
				}
			}
		}
		return list;
	}

	private void ProcessPopulationScanTick()
	{
		PopulationScanJob populationScanJob = this.populationScanJob;
		if (populationScanJob == null || populationScanJob.Completed)
		{
			StopPopulationScanListener();
			return;
		}
		Stopwatch stopwatch = Stopwatch.StartNew();
		try
		{
			while (populationScanJob.ItemIndex < populationScanJob.Items.Count && stopwatch.Elapsed.TotalMilliseconds < 4.0)
			{
				PopulationScanWorkItem populationScanWorkItem = populationScanJob.Items[populationScanJob.ItemIndex];
				while (populationScanJob.EntityIndex < populationScanWorkItem.Entities.Count && stopwatch.Elapsed.TotalMilliseconds < 4.0)
				{
					EntityAgent entityAgent = populationScanWorkItem.Entities[populationScanJob.EntityIndex++];
					populationScanJob.ProcessedEntities++;
					if (entityAgent != null && entityAgent.Alive)
					{
						populationScanJob.CurrentTracked++;
						if (IsProtected(entityAgent, out string _) || IsInsideHardPlayerProtection(entityAgent, populationScanJob.PlayerPositions))
						{
							populationScanJob.CurrentProtected++;
						}
					}
				}
				if (populationScanJob.EntityIndex < populationScanWorkItem.Entities.Count)
				{
					break;
				}
				if (populationScanJob.CurrentTracked > 0)
				{
					populationScanJob.Rows.Add(new PopulationStatusRow(populationScanWorkItem.Code, populationScanWorkItem.RegionX, populationScanWorkItem.RegionZ, populationScanWorkItem.Rule, populationScanJob.CurrentTracked, populationScanWorkItem.Limit, populationScanWorkItem.RadiusChunks, populationScanJob.CurrentProtected, Math.Max(0, populationScanJob.CurrentTracked - populationScanJob.CurrentProtected)));
				}
				populationScanJob.ProcessedRegions++;
				populationScanJob.ItemIndex++;
				populationScanJob.EntityIndex = 0;
				populationScanJob.CurrentTracked = 0;
				populationScanJob.CurrentProtected = 0;
			}
			if (populationScanJob.ItemIndex >= populationScanJob.Items.Count)
			{
				CompletePopulationScan(populationScanJob);
			}
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
		job.Lines = BuildPopulationLines(job.Mode, job.Rows, 500);
		ReleasePopulationScanWorkingSet(job);
		StopPopulationScanListener();
	}

	private void FailPopulationScan(Exception ex)
	{
		PopulationScanJob populationScanJob = this.populationScanJob;
		if (populationScanJob != null)
		{
			populationScanJob.Completed = true;
			populationScanJob.CompletedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
			populationScanJob.Error = ex.GetType().Name + ": " + ex.Message;
			ReleasePopulationScanWorkingSet(populationScanJob);
		}
		StopPopulationScanListener();
		sapi.Logger.Warning("[AnimalLimiter] Population scan failed safely: {0}: {1}", ex.GetType().Name, ex.Message);
	}

	private static void ReleasePopulationScanWorkingSet(PopulationScanJob job)
	{
		job.Items = new List<PopulationScanWorkItem>();
		job.PlayerPositions = new List<(double, double, double)>();
		job.ItemIndex = 0;
		job.EntityIndex = 0;
		job.CurrentTracked = 0;
		job.CurrentProtected = 0;
	}

	private void StopPopulationScanListener()
	{
		if (populationScanListenerId >= 0)
		{
			try
			{
				sapi.Event.UnregisterGameTickListener(populationScanListenerId);
			}
			catch
			{
			}
			populationScanListenerId = -1L;
		}
	}

	private AnimalPopulationScanState SnapshotPopulationScanState(PopulationScanJob job)
	{
		int progressPercent = ((job.TotalEntities > 0) ? Math.Clamp((int)Math.Round((double)job.ProcessedEntities * 100.0 / (double)job.TotalEntities), 0, 100) : (job.Completed ? 100 : 0));
		long num = ((job.CompletedUnixMilliseconds > 0) ? job.CompletedUnixMilliseconds : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
		return new AnimalPopulationScanState
		{
			ScanId = job.Id,
			Running = !job.Completed,
			Completed = job.Completed,
			ProgressPercent = progressPercent,
			ProcessedEntities = job.ProcessedEntities,
			TotalEntities = job.TotalEntities,
			ProcessedRegions = job.ProcessedRegions,
			TotalRegions = job.TotalRegions,
			StartedUnixMilliseconds = job.StartedUnixMilliseconds,
			DurationMilliseconds = Math.Max(0L, num - job.StartedUnixMilliseconds),
			Mode = job.Mode,
			Error = job.Error,
			Lines = ((job.Completed && string.IsNullOrWhiteSpace(job.Error)) ? new List<string>(job.Lines) : new List<string>())
		};
	}

	private List<(double X, double Y, double Z)> CaptureOnlinePlayerPositions()
	{
		List<(double, double, double)> list = new List<(double, double, double)>();
		IPlayer[] allOnlinePlayers = sapi.World.AllOnlinePlayers;
		foreach (IPlayer player in allOnlinePlayers)
		{
			if (player?.Entity != null && EntityPositionResolver.TryGetCoordinates(player.Entity, out var x, out var y, out var z, out var _))
			{
				list.Add((x, y, z));
			}
		}
		return list;
	}

	private bool IsInsideHardPlayerProtection(Entity entity, List<(double X, double Y, double Z)> playerPositions)
	{
		if (config.PlayerProtectionRadiusBlocks <= 0 || playerPositions.Count == 0)
		{
			return false;
		}
		if (!EntityPositionResolver.TryGetCoordinates(entity, out var x, out var y, out var z, out var _))
		{
			return false;
		}
		double num = (double)config.PlayerProtectionRadiusBlocks * (double)config.PlayerProtectionRadiusBlocks;
		foreach (var playerPosition in playerPositions)
		{
			double num2 = playerPosition.X - x;
			double num3 = playerPosition.Y - y;
			double num4 = playerPosition.Z - z;
			if (num2 * num2 + num3 * num3 + num4 * num4 <= num)
			{
				return true;
			}
		}
		return false;
	}

	private static List<string> BuildPopulationLines(string mode, List<PopulationStatusRow> rows, int maxRegionalRows)
	{
		List<string> list = new List<string> { "MODE\t" + mode };
		foreach (var item in (from @group in rows.GroupBy((PopulationStatusRow x) => x.Code, StringComparer.OrdinalIgnoreCase)
			select new
			{
				Code = @group.Key,
				Tracked = @group.Sum((PopulationStatusRow x) => x.Tracked),
				Protected = @group.Sum((PopulationStatusRow x) => x.Protected),
				Eligible = @group.Sum((PopulationStatusRow x) => x.Eligible),
				Regions = @group.Count(),
				OverRegions = @group.Count((PopulationStatusRow x) => x.Eligible > x.Limit)
			} into x
			orderby x.Tracked descending
			select x).ThenBy(x => x.Code, StringComparer.OrdinalIgnoreCase))
		{
			list.Add($"TOTAL\t{item.Code}\t{item.Tracked}\t{item.Protected}\t{item.Eligible}\t{item.Regions}\t{item.OverRegions}");
		}
		List<PopulationStatusRow> list2 = (from x in rows
			orderby x.Eligible > x.Limit descending, Math.Max(0, x.Eligible - x.Limit) descending, x.Tracked descending
			select x).ThenBy((PopulationStatusRow x) => x.Code, StringComparer.OrdinalIgnoreCase).ThenBy((PopulationStatusRow x) => x.RegionX).ThenBy((PopulationStatusRow x) => x.RegionZ)
			.ToList();
		foreach (PopulationStatusRow item2 in list2.Take(Math.Max(1, maxRegionalRows)))
		{
			list.Add($"ROW\t{item2.Code}\t{item2.RegionX},{item2.RegionZ}\t{item2.Rule}\t{item2.Tracked}\t{item2.Limit}\t{item2.RadiusChunks}\t{item2.Protected}\t{item2.Eligible}");
		}
		if (list2.Count > maxRegionalRows)
		{
			list.Add($"TRUNCATED\t{list2.Count - maxRegionalRows}\t{list2.Count}");
		}
		return list;
	}

	private List<PopulationStatusRow> BuildPopulationRowsFromTrackedBucketsImmediate()
	{
		List<PopulationStatusRow> list = new List<PopulationStatusRow>();
		List<(double, double, double)> playerPositions = CaptureOnlinePlayerPositions();
		foreach (KeyValuePair<BucketKey, EntityBucket> item in entityBuckets.ToList())
		{
			BucketKey key = item.Key;
			EntityBucket value = item.Value;
			int? num = ResolveLimitForCode(key.Code, null, out string rule);
			if (!num.HasValue || ResolveRadiusForRule(rule) != key.RadiusChunks)
			{
				continue;
			}
			int num2 = 0;
			int num3 = 0;
			foreach (long entityId in value.EntityIds)
			{
				EntityAgent entityAgent = ResolveLoadedAgent(entityId);
				if (entityAgent != null && entityAgent.Alive)
				{
					num2++;
					if (IsProtected(entityAgent, out string _) || IsInsideHardPlayerProtection(entityAgent, playerPositions))
					{
						num3++;
					}
				}
			}
			if (num2 != 0)
			{
				list.Add(new PopulationStatusRow(key.Code, key.X, key.Z, rule, num2, num.Value, key.RadiusChunks, num3, Math.Max(0, num2 - num3)));
			}
		}
		return list;
	}

	private List<PopulationStatusRow> BuildPopulationRowsFromLoadedSnapshotImmediate()
	{
		List<PopulationStatusRow> list = new List<PopulationStatusRow>();
		int num = 32;
		List<EntityAgent> source = GatherLoadedAgentsNearPlayers();
		List<(double, double, double)> playerPositions = CaptureOnlinePlayerPositions();
		foreach (IGrouping<string, EntityAgent> item in source.GroupBy(CodeOf, StringComparer.OrdinalIgnoreCase))
		{
			string key = item.Key;
			int? num2 = ResolveLimitForCode(key, null, out string rule);
			if (!num2.HasValue)
			{
				continue;
			}
			int num3 = ResolveRadiusForRule(rule);
			int num4 = num3 * 2 + 1;
			int regionBlocks = Math.Max(num, num4 * num);
			foreach (IGrouping<(int, int), PositionedAgent> item2 in from entity2 in item
				select (!EntityPositionResolver.TryGetCoordinates(entity2, out var x, out var _, out var z, out var _)) ? null : new PositionedAgent(entity2, x, z) into x
				where x != null
				select (x) into x
				group x by (X: FloorDiv((int)Math.Floor(x.X), regionBlocks), Z: FloorDiv((int)Math.Floor(x.Z), regionBlocks)))
			{
				int num5 = 0;
				int num6 = 0;
				foreach (PositionedAgent item3 in item2)
				{
					EntityAgent entity = item3.Entity;
					if (entity != null && entity.Alive)
					{
						num5++;
						if (IsProtected(entity, out string _) || IsInsideHardPlayerProtection(entity, playerPositions))
						{
							num6++;
						}
					}
				}
				if (num5 != 0)
				{
					list.Add(new PopulationStatusRow(key, item2.Key.Item1, item2.Key.Item2, rule, num5, num2.Value, num3, num6, Math.Max(0, num5 - num6)));
				}
			}
		}
		return list;
	}

	public List<string> GetGuiRuleLines()
	{
		List<string> list = new List<string>
		{
			$"Rules: {config.Rules.Count}",
			$"Exclusions: {config.ExcludeRules.Count}",
			"",
			"LIMIT RULES"
		};
		foreach (KeyValuePair<string, int> item in config.Rules.OrderByDescending((KeyValuePair<string, int> x) => Specificity(x.Key)).ThenBy((KeyValuePair<string, int> x) => x.Key, StringComparer.OrdinalIgnoreCase))
		{
			bool flag = config.RuleRadiusChunks.TryGetValue(item.Key, out var value);
			int value2 = (flag ? Math.Clamp(value, 0, 32) : Math.Clamp(config.RadiusChunks, 0, 32));
			list.Add($"{item.Key}    |    {item.Value}    |    {value2} chunks{(flag ? "" : " (default)")}");
		}
		if (config.Rules.Count == 0)
		{
			list.Add("(none)");
		}
		list.Add("");
		list.Add("EXCLUSIONS");
		foreach (string item2 in config.ExcludeRules.OrderBy((string x) => x, StringComparer.OrdinalIgnoreCase))
		{
			list.Add(item2);
		}
		if (config.ExcludeRules.Count == 0)
		{
			list.Add("(none)");
		}
		return list;
	}

	public List<string> GetGuiSettingsLines()
	{
		return new List<string>
		{
			"Animal Limiter: " + ((config.Enabled == true) ? "ENABLED" : "DISABLED"),
			$"Default region radius: {config.RadiusChunks} chunks",
			$"Default per-entity limit: {config.DefaultPerEntityLimit}",
			$"Player protection radius: {config.PlayerProtectionRadiusBlocks} blocks",
			$"Reconciliation: {config.SweepIntervalSeconds}s",
			"Generation filter: " + config.GenerationFilter,
			"",
			"Switches save immediately."
		};
	}

	public List<string> GetGuiRemovalLogLines(int maxCount = 50)
	{
		CleanupHistory();
		List<RemovalRecord> list = history.OrderByDescending((RemovalRecord x) => x.RemovedUnixSeconds).Take(Math.Clamp(maxCount, 1, 100)).ToList();
		if (list.Count == 0)
		{
			return new List<string> { "No Animal Limiter removals have been recorded." };
		}
		List<string> list2 = new List<string>(list.Count);
		foreach (RemovalRecord item in list)
		{
			string value = DateTimeOffset.FromUnixTimeSeconds(item.RemovedUnixSeconds).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
			string value2 = (item.Returned ? " [RETURNED]" : "");
			string value3 = (string.IsNullOrWhiteSpace(item.NearestPlayerName) ? "" : $" | nearest {item.NearestPlayerName} {item.NearestPlayerDistance:0.0}b");
			list2.Add($"{value} | {item.RemovalId}{value2} | {item.Code} | {item.X:0.0}, {item.Y:0.0}, {item.Z:0.0} | rule {item.Rule} | limit {item.Limit} | before {item.CountBeforeCull}{value3}");
		}
		return list2;
	}

	public List<string> GetGuiStatusLines()
	{
		List<string> list = new List<string>
		{
			"Animal Limiter: " + ((config.Enabled == true) ? "ENABLED" : "DISABLED"),
			"Universal limiting: " + (config.LimitAllEntities ? "ON" : "OFF"),
			$"Default per-entity limit: {config.DefaultPerEntityLimit}",
			$"Default region radius: {config.RadiusChunks} chunks ({config.RadiusChunks * 2 + 1}x{config.RadiusChunks * 2 + 1})",
			$"Reconciliation interval: {config.SweepIntervalSeconds}s",
			"Generation filter: " + config.GenerationFilter,
			$"Protections: pets={config.ProtectPets}, farm={config.ProtectFarmAnimals}, named={config.ProtectNamedEntities}",
			$"Stored rules: {config.Rules.Count}; exclusions: {config.ExcludeRules.Count}",
			"",
			"Rules:"
		};
		foreach (KeyValuePair<string, int> item in (from x in config.Rules
			orderby Specificity(x.Key) descending, x.Key
			select x).Take(40))
		{
			list.Add($"  {item.Key} = {item.Value}");
		}
		if (config.Rules.Count == 0)
		{
			list.Add("  (none)");
		}
		if (config.Rules.Count > 40)
		{
			list.Add($"  ... {config.Rules.Count - 40} more");
		}
		return list;
	}

	private void CmdRemove(IServerPlayer p, int g, CmdArgs a)
	{
		string text = NormalizePattern(a.PopWord());
		if (string.IsNullOrWhiteSpace(text))
		{
			Msg(p, g, "Usage: /animallimit remove <pattern>");
			return;
		}
		bool flag = config.Rules.Remove(text);
		config.RuleRadiusChunks.Remove(text);
		SaveConfig();
		if (flag)
		{
			InvalidateBucketsAndReconcile();
		}
		Msg(p, g, flag ? ("Removed rule " + text + ".") : ("No exact stored rule named " + text + "."));
	}

	private void CmdExclude(IServerPlayer p, int g, CmdArgs a)
	{
		string pattern = NormalizePattern(a.PopWord());
		if (string.IsNullOrWhiteSpace(pattern))
		{
			Msg(p, g, "Usage: /animallimit exclude <pattern>");
			return;
		}
		if (!config.ExcludeRules.Any((string x) => x.Equals(pattern, StringComparison.OrdinalIgnoreCase)))
		{
			config.ExcludeRules.Add(pattern);
		}
		SaveConfig();
		Msg(p, g, "Excluded " + pattern + " from limiting.");
	}

	private void CmdInclude(IServerPlayer p, int g, CmdArgs a)
	{
		string pattern = NormalizePattern(a.PopWord());
		int num = config.ExcludeRules.RemoveAll((string x) => x.Equals(pattern, StringComparison.OrdinalIgnoreCase));
		SaveConfig();
		Msg(p, g, (num > 0) ? ("Removed exclusion " + pattern + ".") : ("No exclusion named " + pattern + "."));
	}

	private void CmdProtect(IServerPlayer p, int g, CmdArgs a)
	{
		string text = (a.PopWord() ?? "").ToLowerInvariant();
		string text2 = (a.PopWord() ?? "").ToLowerInvariant();
		if ((text != "pets" && text != "farm" && text != "named") || (text2 != "on" && text2 != "off"))
		{
			Msg(p, g, "Usage: /animallimit protect pets|farm|named on|off");
			return;
		}
		bool flag = text2 == "on";
		if (text == "pets")
		{
			config.ProtectPets = flag;
		}
		if (text == "farm")
		{
			config.ProtectFarmAnimals = flag;
		}
		if (text == "named")
		{
			config.ProtectNamedEntities = flag;
		}
		SaveConfig();
		Msg(p, g, $"Protection {text} = {flag}.");
	}

	private void CmdGeneration(IServerPlayer p, int g, CmdArgs a)
	{
		string text = (a.PopWord() ?? "").ToLowerInvariant();
		if (1 == 0)
		{
		}
		string text2 = text switch
		{
			"off" => "Off", 
			"wildonly" => "WildOnly", 
			"bredonly" => "BredOnly", 
			"protectbred" => "ProtectBred", 
			_ => "", 
		};
		if (1 == 0)
		{
		}
		string text3 = text2;
		if (text3.Length == 0)
		{
			Msg(p, g, "Usage: /animallimit generation off|wildonly|bredonly|protectbred");
			return;
		}
		config.GenerationFilter = text3;
		SaveConfig();
		string text4 = ((text3 == "BredOnly" && config.ProtectFarmAnimals) ? " Note: farm protection is ON, so bred/farm entities will still be protected until /animallimit protect farm off." : "");
		Msg(p, g, "Generation filter = " + text3 + "." + text4);
	}

	private void CmdList(IServerPlayer p, int g)
	{
		StringBuilder stringBuilder = new StringBuilder();
		stringBuilder.AppendLine("Rules (most specific matching rule wins):");
		foreach (KeyValuePair<string, int> item in from x in config.Rules
			orderby Specificity(x.Key) descending, x.Key
			select x)
		{
			StringBuilder stringBuilder2 = stringBuilder;
			StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(5, 2, stringBuilder2);
			handler.AppendLiteral("  ");
			handler.AppendFormatted(item.Key);
			handler.AppendLiteral(" = ");
			handler.AppendFormatted(item.Value);
			stringBuilder2.AppendLine(ref handler);
		}
		stringBuilder.AppendLine("Excludes: " + ((config.ExcludeRules.Count == 0) ? "(none)" : string.Join(", ", config.ExcludeRules)));
		Msg(p, g, stringBuilder.ToString().TrimEnd());
	}

	private void CmdFind(IServerPlayer p, int g, CmdArgs a)
	{
		string q = (a.PopWord() ?? "").ToLowerInvariant();
		if (q.Length == 0)
		{
			Msg(p, g, "Usage: /animallimit find <text>");
			return;
		}
		List<string> list = (from c in RegisteredEntityCodes()
			where c.Contains(q, StringComparison.OrdinalIgnoreCase)
			select c).Take(50).ToList();
		Msg(p, g, (list.Count == 0) ? "No registered entity codes matched." : string.Join("\n", list));
	}

	private void CmdMatches(IServerPlayer p, int g, CmdArgs a)
	{
		string pattern = NormalizePattern(a.PopWord());
		if (pattern.Length == 0)
		{
			Msg(p, g, "Usage: /animallimit matches <pattern>");
			return;
		}
		List<string> list = (from c in RegisteredEntityCodes()
			where GlobMatch(c, pattern)
			select c).Take(100).ToList();
		Msg(p, g, (list.Count == 0) ? "No registered entity codes matched." : ($"Matches ({list.Count}{((list.Count == 100) ? "+" : "")}):\n" + string.Join("\n", list)));
	}

	private void CmdTop(IServerPlayer p, int g, CmdArgs a)
	{
		string pattern = NormalizePattern(a.PopWord());
		List<EntityAgent> source = GatherLoadedAgentsNearPlayers();
		if (pattern.Length > 0)
		{
			source = source.Where((EntityAgent e) => GlobMatch(CodeOf(e), pattern)).ToList();
		}
		List<(string, int)> list = (from x in source.GroupBy(CodeOf, StringComparer.OrdinalIgnoreCase)
			select (Code: x.Key, Count: x.Count()) into x
			orderby x.Count descending
			select x).Take(30).ToList();
		Msg(p, g, (list.Count == 0) ? "No matching loaded entities." : ("Top loaded entities:\n" + string.Join("\n", list.Select(((string Code, int Count) x) => $"{x.Count,5}  {x.Code}"))));
	}

	private void CmdPreview(IServerPlayer p, int g, CmdArgs a)
	{
		string text = NormalizePattern(a.PopWord());
		int? limit = null;
		string text2 = a.PopWord();
		if (!string.IsNullOrWhiteSpace(text2))
		{
			if (!int.TryParse(text2, out var result) || result < 0)
			{
				Msg(p, g, "Usage: /animallimit preview [pattern] [limit]");
				return;
			}
			limit = result;
		}
		SweepResult sweepResult = SweepAllPlayerRegions(preview: true, (text.Length == 0) ? null : new PreviewOverride(text, limit, config.RadiusChunks));
		Msg(p, g, $"Preview only: scanned {sweepResult.Scanned} eligible entities across {sweepResult.Regions} active regions; {sweepResult.OverLimitGroups} entity-type/region groups over limit; {sweepResult.WouldRemove} would be removed; {sweepResult.Protected} protected/skipped. Nothing was removed.");
	}

	private void CmdCull(IServerPlayer p, int g)
	{
		SweepResult sweepResult = SweepAllPlayerRegions(preview: false, null);
		Msg(p, g, $"Cull complete: scanned {sweepResult.Scanned}; removed {sweepResult.Removed}; protected/skipped {sweepResult.Protected}; regions {sweepResult.Regions}.");
	}

	private void CmdHistory(IServerPlayer p, int g, CmdArgs a)
	{
		int result = 10;
		int.TryParse(a.PopWord(), out result);
		result = Math.Clamp(result, 1, 50);
		List<RemovalRecord> list = history.OrderByDescending((RemovalRecord x) => x.RemovedUnixSeconds).Take(result).ToList();
		if (list.Count == 0)
		{
			Msg(p, g, "Removal history is empty.");
			return;
		}
		Msg(p, g, string.Join("\n", list.Select((RemovalRecord r) => $"{r.RemovalId} {(r.Returned ? "[RETURNED]" : "")} {r.Code} @ {r.X:0.0},{r.Y:0.0},{r.Z:0.0} rule={r.Rule} limit={r.Limit}")));
	}

	private void CmdReturn(IServerPlayer p, int g, CmdArgs a)
	{
		string id = a.PopWord() ?? "";
		RemovalRecord removalRecord = (id.Equals("last", StringComparison.OrdinalIgnoreCase) ? (from r in history
			where !r.Returned
			orderby r.RemovedUnixSeconds descending, r.RemovalId descending
			select r).FirstOrDefault() : history.LastOrDefault((RemovalRecord r) => r.RemovalId.Equals(id, StringComparison.OrdinalIgnoreCase)));
		string error;
		if (removalRecord == null)
		{
			Msg(p, g, "Removal record not found (or no unreturned record for 'last').");
		}
		else if (removalRecord.Returned)
		{
			Msg(p, g, "That record was already returned.");
		}
		else if (Restore(removalRecord, out error))
		{
			removalRecord.Returned = true;
			SaveHistory();
			sapi.Logger.Notification("[AnimalLimiter] Returned {0} ({1}) to {2:0.0},{3:0.0},{4:0.0}", removalRecord.RemovalId, removalRecord.Code, removalRecord.X, removalRecord.Y, removalRecord.Z);
			Msg(p, g, $"Returned {removalRecord.Code} from {removalRecord.RemovalId} at {removalRecord.X:0.0},{removalRecord.Y:0.0},{removalRecord.Z:0.0}.");
		}
		else
		{
			Msg(p, g, "Return failed: " + error);
		}
	}

	private SweepResult SweepAllPlayerRegions(bool preview, PreviewOverride? previewOverride)
	{
		SweepResult sweepResult = new SweepResult();
		if (!config.LimitAllEntities && config.Rules.Count == 0 && previewOverride == null)
		{
			return sweepResult;
		}
		int num = 32;
		List<EntityAgent> list = GatherLoadedAgentsNearPlayers();
		sweepResult.Scanned = list.Count;
		List<PositionedAgent> source = (from e in list
			select EntityPositionResolver.TryGetCoordinates(e, out var x, out var _, out var z, out var _) ? new PositionedAgent(e, x, z) : null into x
			where x != null
			select (x)).ToList();
		foreach (IGrouping<string, PositionedAgent> item in source.GroupBy((PositionedAgent x) => CodeOf(x.Entity), StringComparer.OrdinalIgnoreCase))
		{
			string key = item.Key;
			int? num2 = ResolveLimitForCode(key, previewOverride, out string rule);
			if (!num2.HasValue)
			{
				continue;
			}
			int num3 = ((previewOverride != null && !string.IsNullOrWhiteSpace(previewOverride.Pattern)) ? previewOverride.RadiusChunks : ResolveRadiusForRule(rule));
			int num4 = num3 * 2 + 1;
			int regionBlocks = Math.Max(num, num4 * num);
			foreach (IGrouping<(int, int), PositionedAgent> item2 in from x in item
				group x by (X: FloorDiv((int)Math.Floor(x.X), regionBlocks), Z: FloorDiv((int)Math.Floor(x.Z), regionBlocks)))
			{
				sweepResult.Regions++;
				List<EntityAgent> list2 = (from x in item2
					select x.Entity into e
					where e.Alive
					select e).ToList();
				List<EntityAgent> list3 = new List<EntityAgent>();
				foreach (EntityAgent item3 in list2)
				{
					if (IsProtected(item3, out string _) || IsInsideHardPlayerProtection(item3))
					{
						sweepResult.Protected++;
					}
					else
					{
						list3.Add(item3);
					}
				}
				if (list3.Count <= num2.Value)
				{
					continue;
				}
				sweepResult.OverLimitGroups++;
				int count = list3.Count - num2.Value;
				List<EntityAgent> source2 = (config.KeepNearestToPlayers ? list3.OrderByDescending(NearestPlayerDistanceSq).ToList() : list3.OrderByDescending((EntityAgent e) => e.EntityId).ToList());
				foreach (EntityAgent item4 in source2.Take(count))
				{
					if (preview)
					{
						sweepResult.WouldRemove++;
					}
					else if (RemoveEntity(item4, rule, num2.Value, list3.Count))
					{
						sweepResult.Removed++;
						UntrackEntity(item4.EntityId);
					}
				}
			}
		}
		if (!preview && historyDirty)
		{
			SaveHistory();
		}
		return sweepResult;
	}

	private List<EntityAgent> GatherLoadedAgentsNearPlayers()
	{
		Dictionary<long, EntityAgent> dictionary = new Dictionary<long, EntityAgent>();
		int value = ((config.RuleRadiusChunks.Count == 0) ? config.RadiusChunks : Math.Max(config.RadiusChunks, config.RuleRadiusChunks.Values.Max()));
		float num = Math.Max(32f, (float)((Math.Clamp(value, 0, 32) * 2 + 1) * 32) * 1.5f);
		float vertRange = Math.Max(num, sapi.World.BlockAccessor.MapSizeY);
		IPlayer[] allOnlinePlayers = sapi.World.AllOnlinePlayers;
		foreach (IPlayer player in allOnlinePlayers)
		{
			if (player?.Entity == null || !EntityPositionResolver.TryGetCoordinates(player.Entity, out var x, out var y, out var z, out var _))
			{
				continue;
			}
			Vec3d position = new Vec3d(x, y, z);
			Entity[] entitiesAround = sapi.World.GetEntitiesAround(position, num, vertRange, (Entity e) => e is EntityAgent && !(e is EntityPlayer));
			foreach (Entity entity in entitiesAround)
			{
				if (entity is EntityAgent { Alive: not false } entityAgent)
				{
					dictionary[entityAgent.EntityId] = entityAgent;
				}
			}
		}
		return dictionary.Values.ToList();
	}

	private int ResolveRadiusForRule(string? rule)
	{
		if (!string.IsNullOrWhiteSpace(rule) && !rule.Equals("<default>", StringComparison.OrdinalIgnoreCase) && !rule.StartsWith("preview:", StringComparison.OrdinalIgnoreCase) && config.RuleRadiusChunks.TryGetValue(rule, out var value))
		{
			return Math.Clamp(value, 0, 32);
		}
		return Math.Clamp(config.RadiusChunks, 0, 32);
	}

	private int? ResolveLimitForCode(string code, PreviewOverride? preview, out string rule)
	{
		rule = "";
		if (preview == null && resolvedRuleCache.TryGetValue(code, out var value))
		{
			rule = value.Rule;
			return value.HasLimit ? new int?(value.Limit) : ((int?)null);
		}
		int? result = null;
		string text = "";
		if (!config.ExcludeRules.Any((string p) => GlobMatch(code, p)))
		{
			if (preview != null)
			{
				if (!GlobMatch(code, preview.Pattern))
				{
					return null;
				}
				if (preview.Limit.HasValue)
				{
					rule = "preview:" + preview.Pattern;
					return preview.Limit.Value;
				}
			}
			KeyValuePair<string, int>? keyValuePair = null;
			foreach (KeyValuePair<string, int> rule2 in config.Rules)
			{
				if (GlobMatch(code, rule2.Key) && (!keyValuePair.HasValue || CompareRulePriority(rule2.Key, keyValuePair.Value.Key) < 0))
				{
					keyValuePair = rule2;
				}
			}
			if (keyValuePair.HasValue)
			{
				text = keyValuePair.Value.Key;
				result = Math.Max(0, keyValuePair.Value.Value);
			}
			else if (config.LimitAllEntities)
			{
				text = "<default>";
				result = config.DefaultPerEntityLimit;
			}
		}
		rule = text;
		if (preview == null)
		{
			if (resolvedRuleCache.Count >= 4096 && !resolvedRuleCache.ContainsKey(code))
			{
				resolvedRuleCache.Clear();
			}
			resolvedRuleCache[code] = new ResolvedRuleCacheEntry(result.HasValue, result.GetValueOrDefault(), text);
		}
		return result;
	}

	private static int CompareRulePriority(string left, string right)
	{
		bool flag = !left.Contains('*');
		bool flag2 = !right.Contains('*');
		if (flag != flag2)
		{
			return (!flag) ? 1 : (-1);
		}
		int num = Specificity(right).CompareTo(Specificity(left));
		if (num != 0)
		{
			return num;
		}
		int num2 = right.Length.CompareTo(left.Length);
		if (num2 != 0)
		{
			return num2;
		}
		return StringComparer.OrdinalIgnoreCase.Compare(left, right);
	}

	private bool IsProtected(EntityAgent entity, out string why)
	{
		why = "";
		if (entity is EntityPlayer)
		{
			why = "player";
			return true;
		}
		string code = CodeOf(entity);
		if (config.ExcludeRules.Any((string p) => GlobMatch(code, p)))
		{
			why = "excluded";
			return true;
		}
		if (config.ProtectPets && LooksTamedOrOwned(entity))
		{
			why = "pet/owned";
			return true;
		}
		string text = (config.GenerationFilter ?? "Off").ToLowerInvariant();
		bool flag = (config.ProtectFarmAnimals || text == "wildonly" || text == "protectbred" || text == "bredonly") && LooksBred(entity);
		if (config.ProtectFarmAnimals & flag)
		{
			why = "farm/bred";
			return true;
		}
		if (config.ProtectNamedEntities && LooksNamed(entity))
		{
			why = "named";
			return true;
		}
		if ((text == "wildonly" || text == "protectbred") & flag)
		{
			why = "generation protected";
			return true;
		}
		if (text == "bredonly" && !flag)
		{
			why = "generation wild skipped";
			return true;
		}
		return false;
	}

	private bool LooksTamedOrOwned(Entity entity)
	{
		foreach (string ownerAttributeKey in config.OwnerAttributeKeys)
		{
			if (entity.WatchedAttributes.HasAttribute(ownerAttributeKey))
			{
				string value = entity.WatchedAttributes.GetString(ownerAttributeKey, "");
				if (!string.IsNullOrWhiteSpace(value))
				{
					return true;
				}
				if (entity.WatchedAttributes.GetAsBool(ownerAttributeKey))
				{
					return true;
				}
			}
		}
		foreach (string tamedAttributeKey in config.TamedAttributeKeys)
		{
			if (entity.WatchedAttributes.GetAsBool(tamedAttributeKey))
			{
				return true;
			}
		}
		return false;
	}

	private bool LooksNamed(Entity entity)
	{
		foreach (string nameAttributeKey in config.NameAttributeKeys)
		{
			string value = entity.WatchedAttributes.GetString(nameAttributeKey, "");
			if (!string.IsNullOrWhiteSpace(value))
			{
				return true;
			}
		}
		return false;
	}

	private bool LooksBred(Entity entity)
	{
		foreach (string generationAttributeKey in config.GenerationAttributeKeys)
		{
			if (entity.WatchedAttributes.GetAsBool(generationAttributeKey))
			{
				return true;
			}
		}
		foreach (string tamedAttributeKey in config.TamedAttributeKeys)
		{
			if (tamedAttributeKey.Contains("domestic", StringComparison.OrdinalIgnoreCase) && entity.WatchedAttributes.GetAsBool(tamedAttributeKey))
			{
				return true;
			}
		}
		return false;
	}

	private bool IsInsideHardPlayerProtection(Entity entity)
	{
		if (config.PlayerProtectionRadiusBlocks <= 0)
		{
			return false;
		}
		double num = (double)config.PlayerProtectionRadiusBlocks * (double)config.PlayerProtectionRadiusBlocks;
		return NearestPlayerDistanceSq(entity) <= num;
	}

	private double NearestPlayerDistanceSq(Entity entity)
	{
		if (!EntityPositionResolver.TryGetCoordinates(entity, out var x, out var y, out var z, out var dimension))
		{
			return double.MaxValue;
		}
		double num = double.MaxValue;
		IPlayer[] allOnlinePlayers = sapi.World.AllOnlinePlayers;
		foreach (IPlayer player in allOnlinePlayers)
		{
			if (player?.Entity != null && EntityPositionResolver.TryGetCoordinates(player.Entity, out var x2, out var y2, out var z2, out dimension))
			{
				double num2 = x2 - x;
				double num3 = y2 - y;
				double num4 = z2 - z;
				double num5 = num2 * num2 + num3 * num3 + num4 * num4;
				if (num5 < num)
				{
					num = num5;
				}
			}
		}
		return num;
	}

	private (string Name, double Distance) NearestPlayer(Entity entity)
	{
		if (!EntityPositionResolver.TryGetCoordinates(entity, out var x, out var y, out var z, out var dimension))
		{
			return (Name: "", Distance: -1.0);
		}
		string item = "";
		double num = double.MaxValue;
		IPlayer[] allOnlinePlayers = sapi.World.AllOnlinePlayers;
		foreach (IPlayer player in allOnlinePlayers)
		{
			if (player?.Entity != null && EntityPositionResolver.TryGetCoordinates(player.Entity, out var x2, out var y2, out var z2, out dimension))
			{
				double num2 = x2 - x;
				double num3 = y2 - y;
				double num4 = z2 - z;
				double num5 = num2 * num2 + num3 * num3 + num4 * num4;
				if (num5 < num)
				{
					num = num5;
					item = player.PlayerName;
				}
			}
		}
		return (Name: item, Distance: (num == double.MaxValue) ? (-1.0) : Math.Sqrt(num));
	}

	private bool RemoveEntity(EntityAgent entity, string rule, int limit, int countBefore)
	{
		try
		{
			string text = CodeOf(entity);
			if (!EntityPositionResolver.TryGetPose(entity, out var x, out var y, out var z, out var _, out var yaw, out var pitch))
			{
				sapi.Logger.Warning("[AnimalLimiter] Skipped removal for entity {0}: runtime position unavailable.", entity.EntityId);
				return false;
			}
			(string, double) tuple = NearestPlayer(entity);
			RemovalRecord removalRecord = new RemovalRecord
			{
				RemovalId = MakeRemovalId(),
				OriginalEntityId = entity.EntityId,
				Code = text,
				DisplayName = text,
				X = x,
				Y = y,
				Z = z,
				Yaw = yaw,
				Pitch = pitch,
				ChunkX = (int)Math.Floor(x / 32.0),
				ChunkZ = (int)Math.Floor(z / 32.0),
				Rule = rule,
				Limit = limit,
				CountBeforeCull = countBefore,
				RadiusChunks = config.RadiusChunks,
				NearestPlayerName = tuple.Item1,
				NearestPlayerDistance = tuple.Item2,
				LookedTamedOrOwned = LooksTamedOrOwned(entity),
				LookedFarmBred = LooksBred(entity),
				LookedNamed = LooksNamed(entity),
				WatchedAttributesBase64 = SerializeTree(entity.WatchedAttributes),
				RemovedUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
			};
			entity.Die(EnumDespawnReason.Removed);
			history.Add(removalRecord);
			historyDirty = true;
			CleanupHistory();
			LogRemoval(removalRecord);
			return true;
		}
		catch (Exception ex)
		{
			sapi.Logger.Error("[AnimalLimiter] Failed to remove entity {0}: {1}", entity.EntityId, ex);
			return false;
		}
	}

	private bool Restore(RemovalRecord rec, out string error)
	{
		error = "";
		try
		{
			EntityProperties entityType = sapi.World.GetEntityType(new AssetLocation(rec.Code));
			if (entityType == null)
			{
				error = "entity type is not registered: " + rec.Code;
				return false;
			}
			Entity entity = sapi.World.ClassRegistry.CreateEntity(entityType);
			if (!EntityPositionResolver.TrySetPose(entity, rec.X, rec.Y, rec.Z, rec.Yaw, rec.Pitch))
			{
				error = "runtime entity position is unavailable";
				return false;
			}
			if (!string.IsNullOrWhiteSpace(rec.WatchedAttributesBase64))
			{
				RestoreTree(entity.WatchedAttributes, rec.WatchedAttributesBase64);
			}
			sapi.World.SpawnEntity(entity);
			return true;
		}
		catch (Exception ex)
		{
			error = ex.GetType().Name + ": " + ex.Message;
			sapi.Logger.Error("[AnimalLimiter] Return failed for {0}: {1}", rec.RemovalId, ex);
			return false;
		}
	}

	private string SerializeTree(ITreeAttribute tree)
	{
		using MemoryStream memoryStream = new MemoryStream();
		using (BinaryWriter stream = new BinaryWriter(memoryStream, Encoding.UTF8, leaveOpen: true))
		{
			tree.ToBytes(stream);
		}
		return Convert.ToBase64String(memoryStream.ToArray());
	}

	private void RestoreTree(SyncedTreeAttribute target, string base64)
	{
		byte[] buffer = Convert.FromBase64String(base64);
		using MemoryStream input = new MemoryStream(buffer);
		using BinaryReader stream = new BinaryReader(input, Encoding.UTF8, leaveOpen: true);
		target.Clear();
		target.FromBytes(stream);
	}

	private void LogRemoval(RemovalRecord r)
	{
		if (config.LogActions)
		{
			sapi.Logger.Notification("[AnimalLimiter] REMOVED {0} id={1} code={2} pos={3:0.0},{4:0.0},{5:0.0} chunk={6},{7} rule={8} limit={9} before={10} nearest={11}({12:0.0}m) pet={13} bred={14} named={15}", r.RemovalId, r.OriginalEntityId, r.Code, r.X, r.Y, r.Z, r.ChunkX, r.ChunkZ, r.Rule, r.Limit, r.CountBeforeCull, r.NearestPlayerName, r.NearestPlayerDistance, r.LookedTamedOrOwned, r.LookedFarmBred, r.LookedNamed);
		}
		if (!config.LogToCsv)
		{
			return;
		}
		lock (csvLock)
		{
			try
			{
				string orCreateDataPath = sapi.GetOrCreateDataPath(config.CsvDirName);
				Directory.CreateDirectory(orCreateDataPath);
				string path = Path.Combine(orCreateDataPath, $"{config.CsvFilePrefix}-{DateTime.UtcNow:yyyy-MM-dd}.csv");
				if (!File.Exists(path))
				{
					File.WriteAllText(path, "utc,removalId,entityId,code,x,y,z,chunkX,chunkZ,rule,limit,countBefore,radiusChunks,nearestPlayer,nearestDistance,petOrOwned,bred,named\n", Encoding.UTF8);
				}
				_003C_003Ey__InlineArray18<object> buffer = default;
				buffer[0] = Csv(DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
				buffer[1] = Csv(r.RemovalId);
				buffer[2] = r.OriginalEntityId;
				buffer[3] = Csv(r.Code);
				buffer[4] = F(r.X);
				buffer[5] = F(r.Y);
				buffer[6] = F(r.Z);
				buffer[7] = r.ChunkX;
				buffer[8] = r.ChunkZ;
				buffer[9] = Csv(r.Rule);
				buffer[10] = r.Limit;
				buffer[11] = r.CountBeforeCull;
				buffer[12] = r.RadiusChunks;
				buffer[13] = Csv(r.NearestPlayerName);
				buffer[14] = F(r.NearestPlayerDistance);
				buffer[15] = r.LookedTamedOrOwned;
				buffer[16] = r.LookedFarmBred;
				buffer[17] = r.LookedNamed;
				string contents = string.Join(",", (ReadOnlySpan<object?>)buffer) + "\n";
				File.AppendAllText(path, contents, Encoding.UTF8);
			}
			catch (Exception ex)
			{
				sapi.Logger.Error("[AnimalLimiter] CSV logging failed: {0}", ex.Message);
			}
		}
	}

	private string MakeRemovalId()
	{
		return $"AL-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{++removalSequence % 10000:D4}";
	}

	private static int ParseRemovalSequence(string? id)
	{
		if (string.IsNullOrWhiteSpace(id))
		{
			return 0;
		}
		int num = id.LastIndexOf('-');
		int result;
		return (num >= 0 && int.TryParse(id.Substring(num + 1), out result)) ? result : 0;
	}

	private static string F(double d)
	{
		return d.ToString("0.###", CultureInfo.InvariantCulture);
	}

	private static string Csv(string? s)
	{
		return "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
	}

	private static string FullCode(AssetLocation? code)
	{
		return (code == null) ? "unknown:unknown" : (code.Domain + ":" + code.Path).ToLowerInvariant();
	}

	private static string CodeOf(Entity e)
	{
		return FullCode(e.Code ?? e.Properties?.Code);
	}

	private static string NormalizePattern(string? s)
	{
		return (s ?? "").Trim().ToLowerInvariant();
	}

	private IEnumerable<string> RegisteredEntityCodes()
	{
		return sapi.World.EntityTypes.Select((EntityProperties p) => FullCode(p.Code)).Distinct(StringComparer.OrdinalIgnoreCase);
	}

	private int CountRegisteredMatches(string p)
	{
		return RegisteredEntityCodes().Count((string c) => GlobMatch(c, p));
	}

	private static int Specificity(string pattern)
	{
		return pattern.Count((char c) => c != '*');
	}

	private static bool GlobMatch(string value, string pattern)
	{
		if (string.IsNullOrWhiteSpace(pattern))
		{
			return false;
		}
		value = value.ToLowerInvariant();
		pattern = pattern.ToLowerInvariant();
		if (!pattern.Contains('*'))
		{
			return value.Equals(pattern, StringComparison.OrdinalIgnoreCase);
		}
		string[] array = pattern.Split('*');
		int startIndex = 0;
		if (!pattern.StartsWith('*') && !value.StartsWith(array[0], StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		string[] array2 = array;
		foreach (string text in array2)
		{
			if (text.Length != 0)
			{
				int num = value.IndexOf(text, startIndex, StringComparison.OrdinalIgnoreCase);
				if (num < 0)
				{
					return false;
				}
				startIndex = num + text.Length;
			}
		}
		if (!pattern.EndsWith('*') && array[^1].Length > 0 && !value.EndsWith(array[^1], StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		return true;
	}

	private static int FloorDiv(int a, int b)
	{
		int num = a / b;
		int num2 = a % b;
		if (num2 != 0 && num2 < 0 != b < 0)
		{
			num--;
		}
		return num;
	}

	private void Msg(IServerPlayer p, int groupId, string text)
	{
		sapi.SendMessage(p, groupId, text, EnumChatType.CommandSuccess);
	}
}
