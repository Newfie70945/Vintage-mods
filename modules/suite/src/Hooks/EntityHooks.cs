using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Runtime.CompilerServices;
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
    private readonly struct EntityTypeInfo
    {
        public EntityTypeInfo(string code, string entityClass) { Code = code; Class = entityClass; }
        public string Code { get; }
        public string Class { get; }
    }

    private sealed class EntityTypeInfoHolder
    {
        public EntityTypeInfoHolder(EntityTypeInfo info) { Info = info; }
        public EntityTypeInfo Info { get; }
    }

    private readonly struct AggregateKey : IEquatable<AggregateKey>
    {
        public AggregateKey(long windowStartTicks, int action, int entityTypeId, int regionX, int regionZ)
        { WindowStartTicks = windowStartTicks; Action = action; EntityTypeId = entityTypeId; RegionX = regionX; RegionZ = regionZ; }
        public long WindowStartTicks { get; }
        public int Action { get; }
        public int EntityTypeId { get; }
        public int RegionX { get; }
        public int RegionZ { get; }
        public bool Equals(AggregateKey other) => WindowStartTicks == other.WindowStartTicks && Action == other.Action && EntityTypeId == other.EntityTypeId && RegionX == other.RegionX && RegionZ == other.RegionZ;
        public override bool Equals(object? obj) => obj is AggregateKey other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(WindowStartTicks, Action, EntityTypeId, RegionX, RegionZ);
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

    private readonly ICoreServerAPI api;
    private readonly BlockLoggerConfig config;
    private readonly LogStorageService queue;
    private readonly IngameTimeFormatter timeFormatter;
    private readonly string worldId;
    private readonly Dictionary<int, EntityTypeInfo> typeCache = new();
    private readonly ConditionalWeakTable<EntityProperties, EntityTypeInfoHolder> propertiesTypeCache = new();
    private readonly ConcurrentDictionary<AggregateKey, AggregateBucket> aggregateBuckets = new();
    private Timer? aggregateFlushTimer;
    private int aggregateTimerStarted;
    private readonly object aggregateFlushLock = new();
    private long hookFailures;
    private long lastHookWarningTicks;
    private static int bulkWorldLoadSuppressionDepth;

    // Per-entity handling is individually fast, so a "slow >= 1 ms" metric cannot see a mass chunk
    // load or unload. These sum the main-thread time spent in the handlers per second instead.
    private static readonly SuiteBurstTimer SpawnBurst = new("BlockLogger spawn handling (total per second)");
    private static readonly SuiteBurstTimer DespawnBurst = new("BlockLogger despawn handling (total per second)");

    public static IDisposable SuppressLifecycleAuditForBulkWorldLoad()
    {
        Interlocked.Increment(ref bulkWorldLoadSuppressionDepth);
        return new BulkWorldLoadSuppressionScope();
    }

    private sealed class BulkWorldLoadSuppressionScope : IDisposable
    {
        private int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            Interlocked.Decrement(ref bulkWorldLoadSuppressionDepth);
        }
    }

    private static bool LifecycleAuditSuppressed => Volatile.Read(ref bulkWorldLoadSuppressionDepth) > 0;

    // Shared in-process lifecycle bus. Other subsystems can consume the existing
    // Vintage Story hook without registering another engine callback.
    public static event Action<Entity, LogActionType>? EntityObserved;

    public EntityHooks(ICoreServerAPI api, BlockLoggerConfig config, LogStorageService queue, IngameTimeFormatter timeFormatter)
    {
        this.api = api;
        this.config = config;
        this.queue = queue;
        this.timeFormatter = timeFormatter;
        worldId = api.World.SavegameIdentifier ?? "unknownworld";

        foreach (var props in api.World.EntityTypes)
        {
            if (props == null) continue;
            var id = EntityRuntimeAccessors.GetPropertiesId(props);
            var code = EntityRuntimeAccessors.GetPropertiesCode(props)?.ToShortString() ?? "unknown";
            var entityClass = EntityRuntimeAccessors.GetPropertiesClass(props) ?? "Entity";
            var info = new EntityTypeInfo(code, entityClass);
            if (id >= 0) typeCache[id] = info;
            propertiesTypeCache.GetValue(props, _ => new EntityTypeInfoHolder(info));
        }
    }

    public void Register()
    {
        api.Event.OnPlayerInteractEntity += OnPlayerInteractEntity;
        api.Event.OnEntitySpawn += OnEntitySpawn;
        api.Event.OnEntityDeath += OnEntityDeath;
        api.Event.OnEntityDespawn += OnEntityDespawn;
        api.Event.PlayerDeath += OnPlayerDeath;

        // Keep the aggregate timer completely dormant unless aggregation is actually used.
        // Full-fidelity mode is the default, so most servers should never pay this periodic wakeup.
        aggregateFlushTimer = new Timer(_ => FlushExpiredAggregates(false), null, Timeout.Infinite, Timeout.Infinite);
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
        FlushExpiredAggregates(true);
        SpawnBurst.Flush();
        DespawnBurst.Flush();
    }

    private void OnEntitySpawn(Entity entity)
    {
        // Player joins/respawns also pass through the entity spawn hook. Those are
        // not useful mob lifecycle records and previously produced "spawned player".
        if (entity == null || entity is EntityPlayer) return;
        if (LifecycleAuditSuppressed) return;

        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            try { EntityObserved?.Invoke(entity, LogActionType.EntitySpawn); }
            catch (Exception ex) { ReportHookFailure("EntityObserved", ex); }

            if (!config.Enabled || !config.LogEntitySpawn) return;
            try { QueueEntityRecord(LogActionType.EntitySpawn, entity, null, null, null); }
            catch (Exception ex) { ReportHookFailure("OnEntitySpawn", ex); }
        }
        finally { SpawnBurst.Add(System.Diagnostics.Stopwatch.GetTimestamp() - started); }
    }

    private void OnEntityDeath(Entity entity, DamageSource damageSource)
    {
        if (entity == null || entity is EntityPlayer) return;

        try { EntityObserved?.Invoke(entity, LogActionType.EntityDeath); }
        catch (Exception ex) { ReportHookFailure("EntityObservedDeath", ex); }

        if (!config.Enabled || !config.LogEntityDeath) return;
        try { QueueEntityRecord(LogActionType.EntityDeath, entity, null, null, damageSource); }
        catch (Exception ex) { ReportHookFailure("OnEntityDeath", ex); }
    }

    private void OnEntityDespawn(Entity entity, EntityDespawnData despawnData)
    {
        // Keep player connect/disconnect lifecycle noise out of the mob audit log.
        if (entity == null || entity is EntityPlayer) return;
        if (LifecycleAuditSuppressed) return;

        var profiler=api.World?.FrameProfiler;
        bool tracing=profiler?.Enabled==true;
        long started=System.Diagnostics.Stopwatch.GetTimestamp();
        if(tracing)profiler!.Enter("suite-entity-despawn");
        try
        {
            try { EntityObserved?.Invoke(entity, LogActionType.EntityDespawn); }
            catch (Exception ex) { ReportHookFailure("EntityObservedDespawn", ex); }
            if(tracing)profiler!.Mark("suite-despawn-observers");
            if (!config.Enabled || !config.LogEntityDespawn) return;
            try { QueueEntityRecord(LogActionType.EntityDespawn, entity, null, null, null, despawnData); }
            catch (Exception ex) { ReportHookFailure("OnEntityDespawn", ex); }
            if(tracing)profiler!.Mark("suite-despawn-audit-queue");
        }
        finally
        {
            if(tracing)profiler!.Leave();
            DespawnBurst.Add(System.Diagnostics.Stopwatch.GetTimestamp()-started);
            double elapsed=System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if(elapsed>=1)SuiteTimingRegistry.Record("BlockLogger despawn handling (slow >=1ms)",elapsed);
        }
    }

    private void OnPlayerDeath(IServerPlayer player, DamageSource damageSource)
    {
        if (!config.Enabled || !config.LogEntityDeath || player == null) return;
        try
        {
            var playerEntity = EntityRuntimeAccessors.GetPlayerEntity(player);
            if (playerEntity != null)
                QueueEntityRecord(LogActionType.EntityDeath, playerEntity, player, null, damageSource);
        }
        catch (Exception ex) { ReportHookFailure("PlayerDeath", ex); }
    }

    private void OnPlayerInteractEntity(Entity entity, IPlayer byPlayer, ItemSlot slot, Vec3d hitPosition, int mode, ref EnumHandling handling)
    {
        if (!config.Enabled || !config.LogEntityInteract || entity == null || byPlayer == null) return;
        try { QueueEntityRecord(LogActionType.EntityInteract, entity, byPlayer, slot, null); }
        catch (Exception ex) { ReportHookFailure("OnPlayerInteractEntity", ex); }
    }

    private void ReportHookFailure(string hookName, Exception ex)
    {
        Interlocked.Increment(ref hookFailures);
        var now = DateTime.UtcNow.Ticks;
        var previous = Interlocked.Read(ref lastHookWarningTicks);
        if (now - previous < TimeSpan.FromSeconds(30).Ticks) return;
        if (Interlocked.CompareExchange(ref lastHookWarningTicks, now, previous) != previous) return;
        api.Logger.Warning("[BlockLogger] Entity hook {0} failed; record skipped. Failures={1}. {2}: {3}",
            hookName, Interlocked.Read(ref hookFailures), ex.GetType().Name, ex.Message);
    }

    private EntityTypeInfo GetTypeInfo(Entity entity)
    {
        var props = EntityRuntimeAccessors.GetProperties(entity);
        if (props != null && propertiesTypeCache.TryGetValue(props, out var byProperties))
            return byProperties.Info;

        var propsId = EntityRuntimeAccessors.GetPropertiesId(props);

        // Cache by the live EntityProperties object identity. Unlike the numeric properties id,
        // object identity cannot alias an unrelated runtime/modded registration, so this avoids
        // rebuilding Code/Class strings on every lifecycle event without reintroducing mislabels.
        var liveCode = EntityRuntimeAccessors.GetPropertiesCode(props)?.ToShortString();
        var liveClass = EntityRuntimeAccessors.GetPropertiesClass(props);

        if (!string.IsNullOrWhiteSpace(liveCode))
        {
            var liveInfo = new EntityTypeInfo(
                liveCode,
                !string.IsNullOrWhiteSpace(liveClass) ? liveClass : entity.GetType().Name);

            if (props != null) propertiesTypeCache.GetValue(props, _ => new EntityTypeInfoHolder(liveInfo));
            if (propsId >= 0) typeCache[propsId] = liveInfo;
            return liveInfo;
        }

        // Numeric cache remains fallback-only for unusual entities whose live properties code
        // cannot be read through the runtime-safe accessor.
        if (propsId >= 0 && typeCache.TryGetValue(propsId, out var cached)) return cached;

        return new EntityTypeInfo(
            entity.GetType().Name,
            !string.IsNullOrWhiteSpace(liveClass) ? liveClass : entity.GetType().Name);
    }

    private bool ShouldIgnoreEntity(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return false;
        var prefixes = config.IgnoredEntityCodePrefixes;
        if (prefixes == null || prefixes.Count == 0) return false;
        for (var i = 0; i < prefixes.Count; i++)
        {
            var prefix = prefixes[i];
            if (!string.IsNullOrWhiteSpace(prefix) && code.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private int GetAdaptiveAggregateWindowMs()
    {
        var baseMs = Math.Max(1000, config.EntityAggregateWindowMs);
        if (!config.AdaptiveEntityAggregation) return baseMs;

        var maxMs = Math.Max(baseMs, config.EntityAggregateMaxWindowMs);
        var queuePercent = queue.QueueCapacity <= 0 ? 0 : (int)((queue.PendingEvents * 100L) / queue.QueueCapacity);
        var aggregatePressure = queue.PendingEntityAggregates >= Math.Max(32, queue.QueueCapacity / 8);
        var stressed = queuePercent >= config.LoadShedQueuePercent || aggregatePressure || queue.LastBatchElapsedMs >= config.LoadShedBatchMs;
        if (!stressed) return baseMs;

        var severe = queuePercent >= Math.Min(95, config.LoadShedQueuePercent + 20) ||
                     queue.PendingEntityAggregates >= Math.Max(64, queue.QueueCapacity / 5) ||
                     queue.LastBatchElapsedMs >= config.LoadShedBatchMs * 2L;
        return severe ? maxMs : Math.Min(maxMs, baseMs * 2);
    }

    private void EnsureAggregateTimerStarted()
    {
        if (aggregateFlushTimer == null) return;
        if (Interlocked.Exchange(ref aggregateTimerStarted, 1) != 0) return;
        try { aggregateFlushTimer.Change(1000, 1000); }
        catch { Interlocked.Exchange(ref aggregateTimerStarted, 0); }
    }

    private void RecordBulkAggregate(LogActionType action, Entity entity, EntityTypeInfo info, int entityTypeId)
    {
        EnsureAggregateTimerStarted();
        var windowMs = GetAdaptiveAggregateWindowMs();
        var nowTicks = DateTime.UtcNow.Ticks;
        var windowTicks = TimeSpan.FromMilliseconds(windowMs).Ticks;
        var windowStart = nowTicks - (nowTicks % windowTicks);
        var typeKey = entityTypeId >= 0 ? entityTypeId : entity.GetType().GetHashCode();

        if (!config.EntityAggregateRegionBuckets)
        {
            var key = new AggregateKey(windowStart, (int)action, typeKey, 0, 0);
            if (aggregateBuckets.TryGetValue(key, out var existing))
            {
                Interlocked.Increment(ref existing.Count);
                return;
            }

            var x = 0; var y = 0; var z = 0; var dimension = "0";
            if (EntityPositionResolver.TryGetCoordinates(entity, out var px, out var py, out var pz, out var posDimension))
            {
                x = (int)Math.Floor(px); y = (int)Math.Floor(py); z = (int)Math.Floor(pz);
                dimension = ResolveDimension(posDimension);
            }

            var bucket = new AggregateBucket
            {
                WindowStartTicks = windowStart, WindowEndTicks = windowStart + windowTicks,
                Action = (int)action, EntityTypeId = entityTypeId, EntityCode = info.Code, EntityClass = info.Class,
                WorldId = worldId, Dimension = dimension, SampleX = x, SampleY = y, SampleZ = z,
                RegionX = 0, RegionZ = 0, Count = 1
            };

            if (!aggregateBuckets.TryAdd(key, bucket) && aggregateBuckets.TryGetValue(key, out existing))
                Interlocked.Increment(ref existing.Count);
            return;
        }

        var sx = 0; var sy = 0; var sz = 0; var sdim = "0";
        var regionX = 0; var regionZ = 0;
        if (EntityPositionResolver.TryGetCoordinates(entity, out var ex, out var ey, out var ez, out var edim))
        {
            sx = (int)Math.Floor(ex); sy = (int)Math.Floor(ey); sz = (int)Math.Floor(ez);
            sdim = ResolveDimension(edim);
            var size = Math.Max(8, config.EntityAggregateRegionSize);
            regionX = FloorDiv(sx, size);
            regionZ = FloorDiv(sz, size);
        }

        var regionKey = new AggregateKey(windowStart, (int)action, typeKey, regionX, regionZ);
        if (aggregateBuckets.TryGetValue(regionKey, out var regionExisting))
        {
            Interlocked.Increment(ref regionExisting.Count);
            return;
        }

        var regionBucket = new AggregateBucket
        {
            WindowStartTicks = windowStart, WindowEndTicks = windowStart + windowTicks,
            Action = (int)action, EntityTypeId = entityTypeId, EntityCode = info.Code, EntityClass = info.Class,
            WorldId = worldId, Dimension = sdim, SampleX = sx, SampleY = sy, SampleZ = sz,
            RegionX = regionX, RegionZ = regionZ, Count = 1
        };
        if (!aggregateBuckets.TryAdd(regionKey, regionBucket) && aggregateBuckets.TryGetValue(regionKey, out regionExisting))
            Interlocked.Increment(ref regionExisting.Count);
    }

    private static int FloorDiv(int value, int divisor)
    {
        var q = value / divisor;
        var r = value % divisor;
        return r != 0 && ((r < 0) != (divisor < 0)) ? q - 1 : q;
    }

    private void FlushExpiredAggregates(bool flushAll)
    {
        lock (aggregateFlushLock)
        {
            var now = DateTime.UtcNow.Ticks;
            foreach (var pair in aggregateBuckets)
            {
                var b = pair.Value;
                if (!flushAll && b.WindowEndTicks > now) continue;
                if (!aggregateBuckets.TryRemove(pair.Key, out b)) continue;

                queue.TryEnqueueEntityAggregate(new EntityActivityAggregate(
                    b.WindowStartTicks, b.WindowEndTicks, b.Action, b.EntityTypeId,
                    b.EntityCode, b.EntityClass, Math.Max(1, Interlocked.Read(ref b.Count)),
                    b.WorldId, b.Dimension, b.SampleX, b.SampleY, b.SampleZ, b.RegionX, b.RegionZ));
            }
        }
    }

    private IPlayer? ResolveDamagePlayer(Entity? sourceEntity)
    {
        if (sourceEntity is not EntityPlayer playerEntity) return null;

        try
        {
            if (playerEntity.Player != null) return playerEntity.Player;
        }
        catch { }

        try
        {
            var uid = playerEntity.PlayerUID;
            if (!string.IsNullOrWhiteSpace(uid)) return api.World.PlayerByUid(uid);
        }
        catch { }

        return null;
    }

    private void QueueEntityRecord(LogActionType action, Entity entity, IPlayer? byPlayer, ItemSlot? slot, DamageSource? damageSource, EntityDespawnData? despawnData = null)
    {
        // Defensive second layer: never persist player spawn/despawn as mob lifecycle
        // records even if another hook path calls this method in the future.
        if (entity is EntityPlayer &&
            action is LogActionType.EntitySpawn or LogActionType.EntityDespawn)
            return;

        var info = GetTypeInfo(entity);
        if (ShouldIgnoreEntity(info.Code)) return;

        var properties = EntityRuntimeAccessors.GetProperties(entity);
        var entityTypeId = EntityRuntimeAccessors.GetPropertiesId(properties);

        if (config.AggregateEntitySpawnDespawn && (action is LogActionType.EntitySpawn or LogActionType.EntityDespawn))
        {
            RecordBulkAggregate(action, entity, info, entityTypeId);
            return;
        }

        if (!EntityPositionResolver.TryGetCoordinates(entity, out var px, out var py, out var pz, out var posDimension)) return;
        var x = (int)Math.Floor(px);
        var y = (int)Math.Floor(py);
        var z = (int)Math.Floor(pz);
        var dimension = ResolveDimension(posDimension);

        string? heldItem = null;
        var heldStackSize = 0;
        if (slot?.Itemstack != null)
        {
            heldItem = slot.Itemstack.Collectible?.Code?.ToShortString();
            heldStackSize = slot.Itemstack.StackSize;
        }

        long sourceEntityId = -1;
        string? sourceCode = null;
        string? sourcePlayerUid = null;
        string? sourcePlayerName = null;
        double? sourceX = null, sourceY = null, sourceZ = null;

        // GetCauseEntity() resolves projectile ownership as well as direct melee sources,
        // which makes death attribution useful for bows/thrown projectiles too.
        var sourceEntity = EntityRuntimeAccessors.GetDamageCauseEntity(damageSource)
            ?? EntityRuntimeAccessors.GetDamageSourceEntity(damageSource);
        var sourcePlayer = ResolveDamagePlayer(sourceEntity);
        if (sourceEntity != null)
        {
            sourceEntityId = EntityRuntimeAccessors.GetEntityId(sourceEntity);
            sourceCode = GetTypeInfo(sourceEntity).Code;
        }
        if (sourcePlayer != null)
        {
            sourcePlayerUid = sourcePlayer.PlayerUID;
            sourcePlayerName = sourcePlayer.PlayerName;
        }

        // For non-player entity deaths the acting/killing player becomes the primary
        // player attribution, making normal player-history queries answer "who killed it?".
        // Player-death records keep their existing victim attribution while the killer is
        // retained in sourcePlayerUid/sourcePlayerName metadata below.
        var attributedPlayer = byPlayer;
        if (attributedPlayer == null && action == LogActionType.EntityDeath)
            attributedPlayer = sourcePlayer;

        var sourcePos = EntityRuntimeAccessors.GetDamageSourcePosition(damageSource);
        if (sourcePos != null) { sourceX = sourcePos.X; sourceY = sourcePos.Y; sourceZ = sourcePos.Z; }

        queue.TryEnqueueEntityFast(new QueuedEntityEventPayload(
            DateTime.UtcNow.Ticks, timeFormatter.GetCurrentStamp(), action,
            attributedPlayer?.PlayerUID ?? string.Empty, attributedPlayer?.PlayerName ?? string.Empty,
            worldId, dimension, x, y, z,
            EntityRuntimeAccessors.GetEntityId(entity), entityTypeId, info.Code, info.Class,
            heldItem, heldStackSize, EntityRuntimeAccessors.GetDamageTier(damageSource),
            EntityRuntimeAccessors.GetDamageType(damageSource), sourceEntityId, sourceCode,
            sourcePlayerUid, sourcePlayerName, sourceX, sourceY, sourceZ,
            EntityRuntimeAccessors.GetDespawnReason(despawnData), 1));
    }

    private static string ResolveDimension(int dimension) => dimension.ToString();
}
