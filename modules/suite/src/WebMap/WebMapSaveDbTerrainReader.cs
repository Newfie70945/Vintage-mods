using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Data.Sqlite;
using ProtoBuf;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace BlockLogger.WebMap;

/// <summary>
/// Read-only terrain source for web-map exports.
///
/// Inspired by the save-database architecture used by WebCartographer, but implemented
/// independently for Server Essentials. The reader never attaches chunks to the running
/// world. It reads the compact mapchunk RainHeightMap first and then deserializes only the
/// vertical chunk sections that can contain the visible surface.
///
/// All Vintage Story server-internal types are discovered through reflection. If the
/// current runtime changes those internals, creation fails with a diagnostic. Bulk exports
/// preserve the published map instead of silently hydrating the live world.
/// </summary>
internal sealed class WebMapSaveDbTerrainReader : IDisposable
{
    private const int ChunkSize = 32;
    // Only export-owned pools are capped; never alter the running world's pool.
    private const int ExportPoolCacheSize = 64;
    public string LastFailure { get; private set; } = "";

    private readonly ICoreServerAPI api;
    private readonly SqliteConnection connection;
    private readonly SqliteCommand getMapChunk;
    private readonly Dictionary<int, SqliteCommand> getChunkBatches = new();
    private readonly object serverMain;
    private readonly object chunkDataPool;
    private readonly MethodInfo serverChunkFromBytes;
    private readonly MethodInfo chunkPosToIndex;
    private readonly MethodInfo? chunkDataPoolFreeAll;
    private bool disposed;
    private int consecutiveFailures;
    private bool disabled;
    public bool IsActive => !disposed && !disabled;

    private WebMapSaveDbTerrainReader(
        ICoreServerAPI api,
        SqliteConnection connection,
        object serverMain,
        object chunkDataPool,
        MethodInfo serverChunkFromBytes,
        MethodInfo chunkPosToIndex,
        MethodInfo? chunkDataPoolFreeAll)
    {
        this.api = api;
        this.connection = connection;
        this.serverMain = serverMain;
        this.chunkDataPool = chunkDataPool;
        this.serverChunkFromBytes = serverChunkFromBytes;
        this.chunkPosToIndex = chunkPosToIndex;
        this.chunkDataPoolFreeAll = chunkDataPoolFreeAll;

        getMapChunk = connection.CreateCommand();
        getMapChunk.CommandText = "SELECT data FROM mapchunk WHERE position=$position LIMIT 1";
        getMapChunk.Parameters.Add("$position", SqliteType.Integer);
        getMapChunk.Prepare();

    }

    public static WebMapSaveDbTerrainReader? TryOpen(ICoreServerAPI api, string savePath, out string reason)
    {
        reason = "";
        SqliteConnection? connection = null;
        object? pool = null;
        MethodInfo? freeAll = null;
        try
        {
            object? serverMain = FindServerMain(api);
            if (serverMain == null)
            {
                reason = "Vintage Story ServerMain runtime object was not discoverable.";
                return null;
            }

            Type serverType = serverMain.GetType();
            Assembly assembly = serverType.Assembly;
            Type[] types = SafeGetTypes(assembly);
            Type? poolType = types.FirstOrDefault(t => string.Equals(t.Name, "ChunkDataPool", StringComparison.Ordinal));
            Type? chunkType = types.FirstOrDefault(t => string.Equals(t.Name, "ServerChunk", StringComparison.Ordinal));
            Type? chunkPosType = types.FirstOrDefault(t => string.Equals(t.FullName, "Vintagestory.Common.Database.ChunkPos", StringComparison.Ordinal)) ??
                types.FirstOrDefault(t => string.Equals(t.Name, "ChunkPos", StringComparison.Ordinal));
            if (poolType == null || chunkType == null || chunkPosType == null)
            {
                reason = "Vintage Story ChunkDataPool/ServerChunk/ChunkPos internals were not found.";
                return null;
            }

            MethodInfo? toChunkIndex = chunkPosType.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(m =>
                {
                    if (!string.Equals(m.Name, "ToChunkIndex", StringComparison.Ordinal)) return false;
                    ParameterInfo[] p = m.GetParameters();
                    return p.Length == 3 &&
                           p[0].ParameterType == typeof(int) &&
                           p[1].ParameterType == typeof(int) &&
                           p[2].ParameterType == typeof(int) &&
                           (m.ReturnType == typeof(ulong) || m.ReturnType == typeof(long));
                });
            if (toChunkIndex == null)
            {
                reason = "Vintage Story ChunkPos.ToChunkIndex(int,int,int) was not found.";
                return null;
            }

            ConstructorInfo? poolCtor = poolType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(c =>
                {
                    ParameterInfo[] p = c.GetParameters();
                    return p.Length == 2 && p[0].ParameterType == typeof(int) && p[1].ParameterType.IsInstanceOfType(serverMain);
                });
            if (poolCtor == null)
            {
                reason = "Compatible Vintage Story ChunkDataPool constructor was not found.";
                return null;
            }

            FieldInfo? cacheSize = poolType.GetField("CacheSize", BindingFlags.Instance | BindingFlags.Public);
            if (cacheSize?.FieldType != typeof(int))
                throw new NotSupportedException("Vintage Story export pool cache limit was not found.");
            freeAll = poolType.GetMethod("FreeAll", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            pool = poolCtor.Invoke(new object[] { ChunkSize, serverMain });
            cacheSize.SetValue(pool, ExportPoolCacheSize);
            ValidateTerrainContract(chunkType);

            MethodInfo? fromBytes = chunkType.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(m => string.Equals(m.Name, "FromBytes", StringComparison.Ordinal))
                .FirstOrDefault(m =>
                {
                    ParameterInfo[] p = m.GetParameters();
                    return p.Length == 3 &&
                           p[0].ParameterType == typeof(byte[]) &&
                           p[1].ParameterType.IsInstanceOfType(pool) &&
                           p[2].ParameterType.IsInstanceOfType(serverMain);
                });
            if (fromBytes == null)
            {
                reason = "Compatible Vintage Story ServerChunk.FromBytes overload was not found.";
                TryFreePool(pool, freeAll);
                return null;
            }

            var csb = new SqliteConnectionStringBuilder
            {
                DataSource = Path.GetFullPath(savePath),
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
                DefaultTimeout = 10
            };
            connection = new SqliteConnection(csb.ConnectionString);
            connection.Open();
            using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA query_only=ON; PRAGMA busy_timeout=10000;";
                pragma.ExecuteNonQuery();
            }

            reason = "Direct read-only save database terrain reader active.";
            return new WebMapSaveDbTerrainReader(api, connection, serverMain, pool, fromBytes, toChunkIndex, freeAll);
        }
        catch (Exception ex)
        {
            try { connection?.Dispose(); } catch { }
            if (pool != null) TryFreePool(pool, freeAll);
            reason = ex.GetType().Name + ": " + ex.Message;
            return null;
        }
    }

    /// <summary>
    /// Fills a caller-owned vertical chunk array. The array must be cleared before calling.
    /// Only the surface chunk section and the section immediately below it are normally read.
    /// If height metadata is missing, a detached full-column scan remains bounded to one column.
    /// </summary>
    public bool TryLoadColumn(int chunkX, int chunkZ, int mapSizeY, IServerChunk[] chunks, out ushort[]? rainHeightMap)
    {
        rainHeightMap = null;
        if (disposed || disabled) return false;

        try
        {
            byte[]? mapBytes = ReadBlob(getMapChunk, ToChunkIndex(chunkX, 0, chunkZ));
            if (mapBytes == null || mapBytes.Length == 0) return RecordFailure(chunkX, chunkZ, "mapchunk row was not found");

            using (var ms = new MemoryStream(mapBytes, writable: false))
            {
                var trimmed = Serializer.Deserialize<TrimmedMapChunk>(ms);
                // Old/incomplete height metadata can still be rendered by a detached
                // top-down terrain scan; it must never force world/entity hydration.
                rainHeightMap = trimmed?.RainHeightMap?.Length >= ChunkSize * ChunkSize ? trimmed.RainHeightMap : null;
            }

            int verticalChunks = Math.Min(chunks.Length, Math.Max(1, (mapSizeY + ChunkSize - 1) / ChunkSize));
            bool[] needed = ArrayPool<bool>.Shared.Rent(Math.Max(1, verticalChunks));
            long[] positions = ArrayPool<long>.Shared.Rent(Math.Max(1, verticalChunks));
            int[] positionCy = ArrayPool<int>.Shared.Rent(Math.Max(1, verticalChunks));
            try
            {
                Array.Clear(needed, 0, verticalChunks);
                if (rainHeightMap == null) Array.Fill(needed, true, 0, verticalChunks);
                for (int i = 0; rainHeightMap != null && i < ChunkSize * ChunkSize; i++)
                {
                    int height = Math.Clamp((int)rainHeightMap[i], 0, Math.Max(0, mapSizeY - 1));
                    int cy = Math.Clamp(height / ChunkSize, 0, verticalChunks - 1);
                    needed[cy] = true;
                    // RainHeightMap is normally exact, but keeping the section below makes the
                    // existing 16-block surface correction/fallback path robust at boundaries.
                    if (cy > 0) needed[cy - 1] = true;
                }

                int requestCount = 0;
                for (int cy = 0; cy < verticalChunks; cy++)
                {
                    if (!needed[cy]) continue;
                    positions[requestCount] = ToChunkIndex(chunkX, cy, chunkZ);
                    positionCy[requestCount] = cy;
                    requestCount++;
                }
                if (requestCount <= 0) return RecordFailure(chunkX, chunkZ, "no surface chunk sections were requested");

                SqliteCommand batch = GetChunkBatchCommand(requestCount);
                for (int i = 0; i < requestCount; i++) batch.Parameters[i].Value = positions[i];

                int loaded = 0;
                using (SqliteDataReader reader = batch.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        long position = reader.GetInt64(0);
                        if (reader.IsDBNull(1)) continue;
                        byte[] bytes = (byte[])reader.GetValue(1);
                        if (bytes.Length == 0) continue;

                        int cy = -1;
                        for (int i = 0; i < requestCount; i++)
                        {
                            if (positions[i] != position) continue;
                            cy = positionCy[i];
                            break;
                        }
                        if (cy < 0 || cy >= chunks.Length) continue;

                        // FromBytes normally reconstructs entities, block entities, decors and mod data.
                        // Their mod callbacks can allocate/retain state even for detached chunks.
                        // Strip that payload first, keeping vanilla block/fluid compression intact.
                        byte[] terrainBytes = TerrainOnlyBytes(bytes);
                        object? result = serverChunkFromBytes.Invoke(null, new object[] { terrainBytes, chunkDataPool, serverMain });
                        if (result is not IServerChunk chunk) continue;
                        chunks[cy] = chunk;
                        loaded++;
                    }
                }

                if (loaded != requestCount) return RecordFailure(chunkX, chunkZ, "one or more requested terrain sections were missing or could not be decoded");
                consecutiveFailures = 0;
                return true;
            }
            finally
            {
                ArrayPool<int>.Shared.Return(positionCy, clearArray: false);
                ArrayPool<long>.Shared.Return(positions, clearArray: false);
                ArrayPool<bool>.Shared.Return(needed, clearArray: false);
            }
        }
        catch (TargetInvocationException ex)
        {
            return RecordFailure(chunkX, chunkZ, ex.InnerException?.Message ?? ex.Message);
        }
        catch (Exception ex)
        {
            return RecordFailure(chunkX, chunkZ, ex.GetType().Name + ": " + ex.Message);
        }
    }


    private static byte[] TerrainOnlyBytes(byte[] bytes)
    {
        using var input = new MemoryStream(bytes, writable: false);
        TrimmedTerrainChunk terrain = Serializer.Deserialize<TrimmedTerrainChunk>(input);
        using var output = new MemoryStream();
        Serializer.Serialize(output, terrain);
        return output.ToArray();
    }

    private static void ValidateTerrainContract(Type chunkType)
    {
        var required = new Dictionary<int, Type>
        {
            [1] = typeof(byte[]), [2] = typeof(byte[]), [3] = typeof(byte[]),
            [12] = typeof(string), [13] = typeof(bool), [15] = typeof(int), [16] = typeof(byte[])
        };
        foreach (MemberInfo member in chunkType.GetMembers(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            ProtoMemberAttribute? attribute = member.GetCustomAttribute<ProtoMemberAttribute>();
            if (attribute == null || !required.TryGetValue(attribute.Tag, out Type? expected)) continue;
            Type? actual = member is FieldInfo field ? field.FieldType : (member as PropertyInfo)?.PropertyType;
            if (actual != expected) throw new NotSupportedException("Vintage Story terrain chunk contract changed at field " + attribute.Tag);
            required.Remove(attribute.Tag);
        }
        if (required.Count != 0) throw new NotSupportedException("Vintage Story terrain chunk contract is incomplete.");
    }

    // Match the verified 1.22.7 ServerChunk protobuf terrain fields. Unknown game/mod
    // fields are skipped by protobuf-net without deserializing their object graphs.
    [ProtoContract]
    private sealed class TrimmedTerrainChunk
    {
        [ProtoMember(1)] public byte[]? Blocks { get; set; }
        [ProtoMember(2)] public byte[]? Light { get; set; }
        [ProtoMember(3)] public byte[]? SaturatedLight { get; set; }
        [ProtoMember(12)] public string GameVersionCreated { get; set; } = "";
        [ProtoMember(13)] public bool Empty { get; set; }
        [ProtoMember(15)] public int CompressionVersion { get; set; }
        [ProtoMember(16)] public byte[]? Fluids { get; set; }
    }

    private SqliteCommand GetChunkBatchCommand(int count)
    {
        count = Math.Max(1, count);
        if (getChunkBatches.TryGetValue(count, out SqliteCommand? cached)) return cached;

        var command = connection.CreateCommand();
        var names = new string[count];
        for (int i = 0; i < count; i++)
        {
            names[i] = "$p" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            command.Parameters.Add(names[i], SqliteType.Integer);
        }
        command.CommandText = "SELECT position,data FROM chunk WHERE position IN (" + string.Join(',', names) + ")";
        command.Prepare();
        getChunkBatches[count] = command;
        return command;
    }

    private bool RecordFailure(int chunkX, int chunkZ, string reason)
    {
        LastFailure = reason;
        consecutiveFailures++;
        if (consecutiveFailures <= 2)
            api.Logger.Debug("[ServerEssentials/WebMap] direct save DB terrain read missed at {0},{1}: {2}", chunkX, chunkZ, reason);
        if (consecutiveFailures >= 8 && !disabled)
        {
            disabled = true;
            api.Logger.Warning("[ServerEssentials/WebMap] direct save DB terrain reader disabled after {0} consecutive misses ({1}). The published map will be preserved if this column cannot be read safely.", consecutiveFailures, reason);
        }
        return false;
    }

    private static byte[]? ReadBlob(SqliteCommand command, long position)
    {
        command.Parameters[0].Value = position;
        object? value = command.ExecuteScalar();
        return value as byte[];
    }

    private long ToChunkIndex(int x, int y, int z)
    {
        object? raw = chunkPosToIndex.Invoke(null, new object[] { x, y, z });
        return raw switch
        {
            ulong u => unchecked((long)u),
            long l => l,
            _ => throw new InvalidOperationException("Vintage Story ChunkPos.ToChunkIndex returned an unexpected value.")
        };
    }

    private static object? FindServerMain(ICoreServerAPI api)
    {
        object? direct = api.Server;
        if (direct != null && string.Equals(direct.GetType().Name, "ServerMain", StringComparison.Ordinal)) return direct;

        foreach (object? maybeCandidate in new object?[] { direct, api, api.WorldManager, api.World })
        {
            if (maybeCandidate == null) continue;
            object candidate = maybeCandidate;
            Type type = candidate.GetType();
            foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (!string.Equals(field.FieldType.Name, "ServerMain", StringComparison.Ordinal)) continue;
                try { if (field.GetValue(candidate) is object value) return value; } catch { }
            }
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (!string.Equals(property.PropertyType.Name, "ServerMain", StringComparison.Ordinal) || property.GetIndexParameters().Length != 0) continue;
                try { if (property.GetValue(candidate) is object value) return value; } catch { }
            }
        }

        return null;
    }

    private static Type[] SafeGetTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null).Cast<Type>().ToArray(); }
    }

    private static void TryFreePool(object pool, MethodInfo? freeAll)
    {
        try { freeAll?.Invoke(pool, null); } catch { }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { getMapChunk.Dispose(); } catch { }
        foreach (SqliteCommand command in getChunkBatches.Values)
        {
            try { command.Dispose(); } catch { }
        }
        getChunkBatches.Clear();
        try { connection.Dispose(); } catch { }
        TryFreePool(chunkDataPool, chunkDataPoolFreeAll);
    }

    [ProtoContract]
    private sealed class TrimmedMapChunk
    {
        [ProtoMember(3)]
        public ushort[]? RainHeightMap { get; set; }
    }
}
