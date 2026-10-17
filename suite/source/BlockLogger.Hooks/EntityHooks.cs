using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using BlockLogger.Config;
using BlockLogger.Models;
using BlockLogger.Services;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace BlockLogger.Hooks;

public sealed class EntityHooks
{
	private readonly struct EntityTypeInfo(string code, string entityClass)
	{
		public string Code { get; } = code;

		public string Class { get; } = entityClass;
	}

	private sealed class EntityTypeInfoHolder
	{
		public EntityTypeInfo Info { get; }

		public EntityTypeInfoHolder(EntityTypeInfo info)
		{
			Info = info;
		}
	}

	private readonly struct AggregateKey(long windowStartTicks, int action, int entityTypeId, int regionX, int regionZ) : IEquatable<AggregateKey>
	{
		public long WindowStartTicks { get; } = windowStartTicks;

		public int Action { get; } = action;

		public int EntityTypeId { get; } = entityTypeId;

		public int RegionX { get; } = regionX;

		public int RegionZ { get; } = regionZ;

		public bool Equals(AggregateKey other)
		{
			return WindowStartTicks == other.WindowStartTicks && Action == other.Action && EntityTypeId == other.EntityTypeId && RegionX == other.RegionX && RegionZ == other.RegionZ;
		}

		public override bool Equals(object? obj)
		{
			return obj is AggregateKey other && Equals(other);
		}

		public override int GetHashCode()
		{
			return HashCode.Combine(WindowStartTicks, Action, EntityTypeId, RegionX, RegionZ);
		}
	}

	private sealed class AggregateBucket
	{
		public long WindowStartTicks;

		public long WindowEndTicks;

		public int Action;

		public int EntityTypeId;

		public string EntityCode = "";

		public string EntityClass = "";

		public string WorldId = "";

		public string Dimension = "overworld";

		public int SampleX;

		public int SampleY;

		public int SampleZ;

		public long Count;

		public int RegionX;

		public int RegionZ;
	}

	private sealed class BulkWorldLoadSuppressionScope : IDisposable
	{
		private int disposed;

		public void Dispose()
		{
			if (Interlocked.Exchange(ref disposed, 1) == 0)
			{
				Interlocked.Decrement(ref bulkWorldLoadSuppressionDepth);
			}
		}
	}

	private readonly ICoreServerAPI api;

	private readonly BlockLoggerConfig config;

	private readonly LogStorageService queue;

	private readonly IngameTimeFormatter timeFormatter;

	private readonly string worldId;

	private readonly Dictionary<int, EntityTypeInfo> typeCache = new Dictionary<int, EntityTypeInfo>();

	private readonly ConditionalWeakTable<EntityProperties, EntityTypeInfoHolder> propertiesTypeCache = new ConditionalWeakTable<EntityProperties, EntityTypeInfoHolder>();

	private readonly ConcurrentDictionary<AggregateKey, AggregateBucket> aggregateBuckets = new ConcurrentDictionary<AggregateKey, AggregateBucket>();

	private Timer? aggregateFlushTimer;

	private int aggregateTimerStarted;

	private readonly object aggregateFlushLock = new object();

	private long hookFailures;

	private long lastHookWarningTicks;

	private static int bulkWorldLoadSuppressionDepth;

	private static bool LifecycleAuditSuppressed => Volatile.Read(in bulkWorldLoadSuppressionDepth) > 0;

	public static event Action<Entity, LogActionType>? EntityObserved;

	public static IDisposable SuppressLifecycleAuditForBulkWorldLoad()
	{
		Interlocked.Increment(ref bulkWorldLoadSuppressionDepth);
		return new BulkWorldLoadSuppressionScope();
	}

	public EntityHooks(ICoreServerAPI api, BlockLoggerConfig config, LogStorageService queue, IngameTimeFormatter timeFormatter)
	{
		this.api = api;
		this.config = config;
		this.queue = queue;
		this.timeFormatter = timeFormatter;
		worldId = api.World.SavegameIdentifier ?? "unknownworld";
		foreach (EntityProperties entityType in api.World.EntityTypes)
		{
			if (entityType != null)
			{
				int propertiesId = EntityRuntimeAccessors.GetPropertiesId(entityType);
				string code = EntityRuntimeAccessors.GetPropertiesCode(entityType)?.ToShortString() ?? "unknown";
				string entityClass = EntityRuntimeAccessors.GetPropertiesClass(entityType) ?? "Entity";
				EntityTypeInfo info = new EntityTypeInfo(code, entityClass);
				if (propertiesId >= 0)
				{
					typeCache[propertiesId] = info;
				}
				propertiesTypeCache.GetValue(entityType, (EntityProperties _) => new EntityTypeInfoHolder(info));
			}
		}
	}

	public void Register()
	{
		api.Event.OnPlayerInteractEntity += OnPlayerInteractEntity;
		api.Event.OnEntitySpawn += OnEntitySpawn;
		api.Event.OnEntityDeath += OnEntityDeath;
		api.Event.OnEntityDespawn += OnEntityDespawn;
		api.Event.PlayerDeath += OnPlayerDeath;
		aggregateFlushTimer = new Timer((object? _) =>
		{
			FlushExpiredAggregates(flushAll: false);
		}, null, -1, -1);
	}

	public void Unregister()
	{
		api.Event.OnPlayerInteractEntity -= OnPlayerInteractEntity;
		api.Event.OnEntitySpawn -= OnEntitySpawn;
		api.Event.OnEntityDeath -= OnEntityDeath;
		api.Event.OnEntityDespawn -= OnEntityDespawn;
		api.Event.PlayerDeath -= OnPlayerDeath;
		aggregateFlushTimer?.Dispose();
		aggregateFlushTimer = null;
		FlushExpiredAggregates(flushAll: true);
	}

	private void OnEntitySpawn(Entity entity)
	{
		if (entity == null || entity is EntityPlayer || LifecycleAuditSuppressed)
		{
			return;
		}
		try
		{
			EntityObserved?.Invoke(entity, LogActionType.EntitySpawn);
		}
		catch (Exception ex)
		{
			ReportHookFailure("EntityObserved", ex);
		}
		if (!config.Enabled || !config.LogEntitySpawn)
		{
			return;
		}
		try
		{
			QueueEntityRecord(LogActionType.EntitySpawn, entity, null, null, null);
		}
		catch (Exception ex2)
		{
			ReportHookFailure("OnEntitySpawn", ex2);
		}
	}

	private void OnEntityDeath(Entity entity, DamageSource damageSource)
	{
		if (entity == null || entity is EntityPlayer)
		{
			return;
		}
		try
		{
			EntityObserved?.Invoke(entity, LogActionType.EntityDeath);
		}
		catch (Exception ex)
		{
			ReportHookFailure("EntityObservedDeath", ex);
		}
		if (!config.Enabled || !config.LogEntityDeath)
		{
			return;
		}
		try
		{
			QueueEntityRecord(LogActionType.EntityDeath, entity, null, null, damageSource);
		}
		catch (Exception ex2)
		{
			ReportHookFailure("OnEntityDeath", ex2);
		}
	}

	private void OnEntityDespawn(Entity entity, EntityDespawnData despawnData)
	{
		if (entity == null || entity is EntityPlayer || LifecycleAuditSuppressed)
		{
			return;
		}
		try
		{
			EntityObserved?.Invoke(entity, LogActionType.EntityDespawn);
		}
		catch (Exception ex)
		{
			ReportHookFailure("EntityObservedDespawn", ex);
		}
		if (!config.Enabled || !config.LogEntityDespawn)
		{
			return;
		}
		try
		{
			QueueEntityRecord(LogActionType.EntityDespawn, entity, null, null, null, despawnData);
		}
		catch (Exception ex2)
		{
			ReportHookFailure("OnEntityDespawn", ex2);
		}
	}

	private void OnPlayerDeath(IServerPlayer player, DamageSource damageSource)
	{
		if (!config.Enabled || !config.LogEntityDeath || player == null)
		{
			return;
		}
		try
		{
			Entity playerEntity = EntityRuntimeAccessors.GetPlayerEntity(player);
			if (playerEntity != null)
			{
				QueueEntityRecord(LogActionType.EntityDeath, playerEntity, player, null, damageSource);
			}
		}
		catch (Exception ex)
		{
			ReportHookFailure("PlayerDeath", ex);
		}
	}

	private void OnPlayerInteractEntity(Entity entity, IPlayer byPlayer, ItemSlot slot, Vec3d hitPosition, int mode, ref EnumHandling handling)
	{
		if (!config.Enabled || !config.LogEntityInteract || entity == null || byPlayer == null)
		{
			return;
		}
		try
		{
			QueueEntityRecord(LogActionType.EntityInteract, entity, byPlayer, slot, null);
		}
		catch (Exception ex)
		{
			ReportHookFailure("OnPlayerInteractEntity", ex);
		}
	}

	private void ReportHookFailure(string hookName, Exception ex)
	{
		Interlocked.Increment(ref hookFailures);
		long ticks = DateTime.UtcNow.Ticks;
		long num = Interlocked.Read(in lastHookWarningTicks);
		if (ticks - num >= TimeSpan.FromSeconds(30L).Ticks && Interlocked.CompareExchange(ref lastHookWarningTicks, ticks, num) == num)
		{
			api.Logger.Warning("[BlockLogger] Entity hook {0} failed; record skipped. Failures={1}. {2}: {3}", hookName, Interlocked.Read(in hookFailures), ex.GetType().Name, ex.Message);
		}
	}

	private EntityTypeInfo GetTypeInfo(Entity entity)
	{
		EntityProperties properties = EntityRuntimeAccessors.GetProperties(entity);
		if (properties != null && propertiesTypeCache.TryGetValue(properties, out EntityTypeInfoHolder value))
		{
			return value.Info;
		}
		int propertiesId = EntityRuntimeAccessors.GetPropertiesId(properties);
		string text = EntityRuntimeAccessors.GetPropertiesCode(properties)?.ToShortString();
		string propertiesClass = EntityRuntimeAccessors.GetPropertiesClass(properties);
		if (!string.IsNullOrWhiteSpace(text))
		{
			EntityTypeInfo liveInfo = new EntityTypeInfo(text, (!string.IsNullOrWhiteSpace(propertiesClass)) ? propertiesClass : entity.GetType().Name);
			if (properties != null)
			{
				propertiesTypeCache.GetValue(properties, (EntityProperties _) => new EntityTypeInfoHolder(liveInfo));
			}
			if (propertiesId >= 0)
			{
				typeCache[propertiesId] = liveInfo;
			}
			return liveInfo;
		}
		if (propertiesId >= 0 && typeCache.TryGetValue(propertiesId, out var value2))
		{
			return value2;
		}
		return new EntityTypeInfo(entity.GetType().Name, (!string.IsNullOrWhiteSpace(propertiesClass)) ? propertiesClass : entity.GetType().Name);
	}

	private bool ShouldIgnoreEntity(string code)
	{
		if (string.IsNullOrWhiteSpace(code))
		{
			return false;
		}
		List<string> ignoredEntityCodePrefixes = config.IgnoredEntityCodePrefixes;
		if (ignoredEntityCodePrefixes == null || ignoredEntityCodePrefixes.Count == 0)
		{
			return false;
		}
		for (int i = 0; i < ignoredEntityCodePrefixes.Count; i++)
		{
			string value = ignoredEntityCodePrefixes[i];
			if (!string.IsNullOrWhiteSpace(value) && code.StartsWith(value, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}
		return false;
	}

	private int GetAdaptiveAggregateWindowMs()
	{
		int num = Math.Max(1000, config.EntityAggregateWindowMs);
		if (!config.AdaptiveEntityAggregation)
		{
			return num;
		}
		int num2 = Math.Max(num, config.EntityAggregateMaxWindowMs);
		int num3 = (int)((queue.QueueCapacity > 0) ? (queue.PendingEvents * 100 / queue.QueueCapacity) : 0);
		bool flag = queue.PendingEntityAggregates >= Math.Max(32, queue.QueueCapacity / 8);
		if (!((num3 >= config.LoadShedQueuePercent) | flag) && queue.LastBatchElapsedMs < config.LoadShedBatchMs)
		{
			return num;
		}
		return (num3 >= Math.Min(95, config.LoadShedQueuePercent + 20) || queue.PendingEntityAggregates >= Math.Max(64, queue.QueueCapacity / 5) || queue.LastBatchElapsedMs >= (long)config.LoadShedBatchMs * 2L) ? num2 : Math.Min(num2, num * 2);
	}

	private void EnsureAggregateTimerStarted()
	{
		if (aggregateFlushTimer == null || Interlocked.Exchange(ref aggregateTimerStarted, 1) != 0)
		{
			return;
		}
		try
		{
			aggregateFlushTimer.Change(1000, 1000);
		}
		catch
		{
			Interlocked.Exchange(ref aggregateTimerStarted, 0);
		}
	}

	private void RecordBulkAggregate(LogActionType action, Entity entity, EntityTypeInfo info, int entityTypeId)
	{
		EnsureAggregateTimerStarted();
		int adaptiveAggregateWindowMs = GetAdaptiveAggregateWindowMs();
		long ticks = DateTime.UtcNow.Ticks;
		long ticks2 = TimeSpan.FromMilliseconds(adaptiveAggregateWindowMs).Ticks;
		long num = ticks - ticks % ticks2;
		int entityTypeId2 = ((entityTypeId >= 0) ? entityTypeId : entity.GetType().GetHashCode());
		if (!config.EntityAggregateRegionBuckets)
		{
			AggregateKey key = new AggregateKey(num, (int)action, entityTypeId2, 0, 0);
			if (aggregateBuckets.TryGetValue(key, out AggregateBucket value))
			{
				Interlocked.Increment(ref value.Count);
				return;
			}
			int sampleX = 0;
			int sampleY = 0;
			int sampleZ = 0;
			string dimension = "0";
			if (EntityPositionResolver.TryGetCoordinates(entity, out var x, out var y, out var z, out var dimension2))
			{
				sampleX = (int)Math.Floor(x);
				sampleY = (int)Math.Floor(y);
				sampleZ = (int)Math.Floor(z);
				dimension = ResolveDimension(dimension2);
			}
			AggregateBucket value2 = new AggregateBucket
			{
				WindowStartTicks = num,
				WindowEndTicks = num + ticks2,
				Action = (int)action,
				EntityTypeId = entityTypeId,
				EntityCode = info.Code,
				EntityClass = info.Class,
				WorldId = worldId,
				Dimension = dimension,
				SampleX = sampleX,
				SampleY = sampleY,
				SampleZ = sampleZ,
				RegionX = 0,
				RegionZ = 0,
				Count = 1L
			};
			if (!aggregateBuckets.TryAdd(key, value2) && aggregateBuckets.TryGetValue(key, out value))
			{
				Interlocked.Increment(ref value.Count);
			}
			return;
		}
		int num2 = 0;
		int sampleY2 = 0;
		int num3 = 0;
		string dimension3 = "0";
		int regionX = 0;
		int regionZ = 0;
		if (EntityPositionResolver.TryGetCoordinates(entity, out var x2, out var y2, out var z2, out var dimension4))
		{
			num2 = (int)Math.Floor(x2);
			sampleY2 = (int)Math.Floor(y2);
			num3 = (int)Math.Floor(z2);
			dimension3 = ResolveDimension(dimension4);
			int divisor = Math.Max(8, config.EntityAggregateRegionSize);
			regionX = FloorDiv(num2, divisor);
			regionZ = FloorDiv(num3, divisor);
		}
		AggregateKey key2 = new AggregateKey(num, (int)action, entityTypeId2, regionX, regionZ);
		if (aggregateBuckets.TryGetValue(key2, out AggregateBucket value3))
		{
			Interlocked.Increment(ref value3.Count);
			return;
		}
		AggregateBucket value4 = new AggregateBucket
		{
			WindowStartTicks = num,
			WindowEndTicks = num + ticks2,
			Action = (int)action,
			EntityTypeId = entityTypeId,
			EntityCode = info.Code,
			EntityClass = info.Class,
			WorldId = worldId,
			Dimension = dimension3,
			SampleX = num2,
			SampleY = sampleY2,
			SampleZ = num3,
			RegionX = regionX,
			RegionZ = regionZ,
			Count = 1L
		};
		if (!aggregateBuckets.TryAdd(key2, value4) && aggregateBuckets.TryGetValue(key2, out value3))
		{
			Interlocked.Increment(ref value3.Count);
		}
	}

	private static int FloorDiv(int value, int divisor)
	{
		int num = value / divisor;
		int num2 = value % divisor;
		return (num2 != 0 && num2 < 0 != divisor < 0) ? (num - 1) : num;
	}

	private void FlushExpiredAggregates(bool flushAll)
	{
		lock (aggregateFlushLock)
		{
			long ticks = DateTime.UtcNow.Ticks;
			foreach (KeyValuePair<AggregateKey, AggregateBucket> aggregateBucket in aggregateBuckets)
			{
				AggregateBucket value = aggregateBucket.Value;
				if ((flushAll || value.WindowEndTicks <= ticks) && aggregateBuckets.TryRemove(aggregateBucket.Key, out value))
				{
					queue.TryEnqueueEntityAggregate(new EntityActivityAggregate(value.WindowStartTicks, value.WindowEndTicks, value.Action, value.EntityTypeId, value.EntityCode, value.EntityClass, Math.Max(1L, Interlocked.Read(in value.Count)), value.WorldId, value.Dimension, value.SampleX, value.SampleY, value.SampleZ, value.RegionX, value.RegionZ));
				}
			}
		}
	}

	private IPlayer? ResolveDamagePlayer(Entity? sourceEntity)
	{
		if (!(sourceEntity is EntityPlayer entityPlayer))
		{
			return null;
		}
		try
		{
			if (entityPlayer.Player != null)
			{
				return entityPlayer.Player;
			}
		}
		catch
		{
		}
		try
		{
			string playerUID = entityPlayer.PlayerUID;
			if (!string.IsNullOrWhiteSpace(playerUID))
			{
				return api.World.PlayerByUid(playerUID);
			}
		}
		catch
		{
		}
		return null;
	}

	private void QueueEntityRecord(LogActionType action, Entity entity, IPlayer? byPlayer, ItemSlot? slot, DamageSource? damageSource, EntityDespawnData? despawnData = null)
	{
		bool flag = entity is EntityPlayer;
		bool flag2 = flag;
		if (flag2)
		{
			bool flag3 = ((action == LogActionType.EntitySpawn || action == LogActionType.EntityDespawn) ? true : false);
			flag2 = flag3;
		}
		if (flag2)
		{
			return;
		}
		EntityTypeInfo typeInfo = GetTypeInfo(entity);
		if (ShouldIgnoreEntity(typeInfo.Code))
		{
			return;
		}
		EntityProperties properties = EntityRuntimeAccessors.GetProperties(entity);
		int propertiesId = EntityRuntimeAccessors.GetPropertiesId(properties);
		bool aggregateEntitySpawnDespawn = config.AggregateEntitySpawnDespawn;
		bool flag4 = aggregateEntitySpawnDespawn;
		if (flag4)
		{
			bool flag3 = ((action == LogActionType.EntitySpawn || action == LogActionType.EntityDespawn) ? true : false);
			flag4 = flag3;
		}
		double x;
		double y;
		double z;
		int dimension;
		if (flag4)
		{
			RecordBulkAggregate(action, entity, typeInfo, propertiesId);
		}
		else if (EntityPositionResolver.TryGetCoordinates(entity, out x, out y, out z, out dimension))
		{
			int x2 = (int)Math.Floor(x);
			int y2 = (int)Math.Floor(y);
			int z2 = (int)Math.Floor(z);
			string dimension2 = ResolveDimension(dimension);
			string heldItem = null;
			int heldStackSize = 0;
			if (slot?.Itemstack != null)
			{
				heldItem = slot.Itemstack.Collectible?.Code?.ToShortString();
				heldStackSize = slot.Itemstack.StackSize;
			}
			long sourceEntityId = -1L;
			string sourceEntityCode = null;
			string sourcePlayerUid = null;
			string sourcePlayerName = null;
			double? sourceX = null;
			double? sourceY = null;
			double? sourceZ = null;
			Entity entity2 = EntityRuntimeAccessors.GetDamageCauseEntity(damageSource) ?? EntityRuntimeAccessors.GetDamageSourceEntity(damageSource);
			IPlayer player = ResolveDamagePlayer(entity2);
			if (entity2 != null)
			{
				sourceEntityId = EntityRuntimeAccessors.GetEntityId(entity2);
				sourceEntityCode = GetTypeInfo(entity2).Code;
			}
			if (player != null)
			{
				sourcePlayerUid = player.PlayerUID;
				sourcePlayerName = player.PlayerName;
			}
			IPlayer player2 = byPlayer;
			if (player2 == null && action == LogActionType.EntityDeath)
			{
				player2 = player;
			}
			Vec3d damageSourcePosition = EntityRuntimeAccessors.GetDamageSourcePosition(damageSource);
			if (damageSourcePosition != null)
			{
				sourceX = damageSourcePosition.X;
				sourceY = damageSourcePosition.Y;
				sourceZ = damageSourcePosition.Z;
			}
			queue.TryEnqueueEntityFast(new QueuedEntityEventPayload(DateTime.UtcNow.Ticks, timeFormatter.GetCurrentStamp(), action, player2?.PlayerUID ?? string.Empty, player2?.PlayerName ?? string.Empty, worldId, dimension2, x2, y2, z2, EntityRuntimeAccessors.GetEntityId(entity), propertiesId, typeInfo.Code, typeInfo.Class, heldItem, heldStackSize, EntityRuntimeAccessors.GetDamageTier(damageSource), EntityRuntimeAccessors.GetDamageType(damageSource), sourceEntityId, sourceEntityCode, sourcePlayerUid, sourcePlayerName, sourceX, sourceY, sourceZ, EntityRuntimeAccessors.GetDespawnReason(despawnData)));
		}
	}

	private static string ResolveDimension(int dimension)
	{
		return dimension.ToString();
	}
}
