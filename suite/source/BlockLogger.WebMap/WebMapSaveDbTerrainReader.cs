using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Data.Sqlite;
using ProtoBuf;
using Vintagestory.API.Server;

namespace BlockLogger.WebMap;

internal sealed class WebMapSaveDbTerrainReader : IDisposable
{
	[ProtoContract]
	private sealed class TrimmedTerrainChunk
	{
		[ProtoMember(1)]
		public byte[]? Blocks { get; set; }

		[ProtoMember(2)]
		public byte[]? Light { get; set; }

		[ProtoMember(3)]
		public byte[]? SaturatedLight { get; set; }

		[ProtoMember(12)]
		public string GameVersionCreated { get; set; } = "";

		[ProtoMember(13)]
		public bool Empty { get; set; }

		[ProtoMember(15)]
		public int CompressionVersion { get; set; }

		[ProtoMember(16)]
		public byte[]? Fluids { get; set; }
	}

	[ProtoContract]
	private sealed class TrimmedMapChunk
	{
		[ProtoMember(3)]
		public ushort[]? RainHeightMap { get; set; }
	}

	private const int ChunkSize = 32;

	private const int ExportPoolCacheSize = 64;

	private readonly ICoreServerAPI api;

	private readonly SqliteConnection connection;

	private readonly SqliteCommand getMapChunk;

	private readonly Dictionary<int, SqliteCommand> getChunkBatches = new Dictionary<int, SqliteCommand>();

	private readonly object serverMain;

	private readonly object chunkDataPool;

	private readonly MethodInfo serverChunkFromBytes;

	private readonly MethodInfo chunkPosToIndex;

	private readonly MethodInfo? chunkDataPoolFreeAll;

	private bool disposed;

	private int consecutiveFailures;

	private bool disabled;

	public string LastFailure { get; private set; } = "";

	public bool IsActive => !disposed && !disabled;

	private WebMapSaveDbTerrainReader(ICoreServerAPI api, SqliteConnection connection, object serverMain, object chunkDataPool, MethodInfo serverChunkFromBytes, MethodInfo chunkPosToIndex, MethodInfo? chunkDataPoolFreeAll)
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
		SqliteConnection sqliteConnection = null;
		object pool = null;
		MethodInfo freeAll = null;
		try
		{
			object serverMain = FindServerMain(api);
			if (serverMain == null)
			{
				reason = "Vintage Story ServerMain runtime object was not discoverable.";
				return null;
			}
			Type type = serverMain.GetType();
			Assembly assembly = type.Assembly;
			Type[] source = SafeGetTypes(assembly);
			Type type2 = source.FirstOrDefault((Type t) => string.Equals(t.Name, "ChunkDataPool", StringComparison.Ordinal));
			Type type3 = source.FirstOrDefault((Type t) => string.Equals(t.Name, "ServerChunk", StringComparison.Ordinal));
			Type type4 = source.FirstOrDefault((Type t) => string.Equals(t.FullName, "Vintagestory.Common.Database.ChunkPos", StringComparison.Ordinal)) ?? source.FirstOrDefault((Type t) => string.Equals(t.Name, "ChunkPos", StringComparison.Ordinal));
			if (type2 == null || type3 == null || type4 == null)
			{
				reason = "Vintage Story ChunkDataPool/ServerChunk/ChunkPos internals were not found.";
				return null;
			}
			MethodInfo methodInfo = type4.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault((MethodInfo m) =>
			{
				if (!string.Equals(m.Name, "ToChunkIndex", StringComparison.Ordinal))
				{
					return false;
				}
				ParameterInfo[] parameters = m.GetParameters();
				return parameters.Length == 3 && parameters[0].ParameterType == typeof(int) && parameters[1].ParameterType == typeof(int) && parameters[2].ParameterType == typeof(int) && (m.ReturnType == typeof(ulong) || m.ReturnType == typeof(long));
			});
			if (methodInfo == null)
			{
				reason = "Vintage Story ChunkPos.ToChunkIndex(int,int,int) was not found.";
				return null;
			}
			ConstructorInfo constructorInfo = type2.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault((ConstructorInfo c) =>
			{
				ParameterInfo[] parameters = c.GetParameters();
				return parameters.Length == 2 && parameters[0].ParameterType == typeof(int) && parameters[1].ParameterType.IsInstanceOfType(serverMain);
			});
			if (constructorInfo == null)
			{
				reason = "Compatible Vintage Story ChunkDataPool constructor was not found.";
				return null;
			}
			FieldInfo field = type2.GetField("CacheSize", BindingFlags.Instance | BindingFlags.Public);
			if (field?.FieldType != typeof(int))
			{
				throw new NotSupportedException("Vintage Story export pool cache limit was not found.");
			}
			freeAll = type2.GetMethod("FreeAll", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
			pool = constructorInfo.Invoke(new object[2] { 32, serverMain });
			field.SetValue(pool, 64);
			ValidateTerrainContract(type3);
			MethodInfo methodInfo2 = (from m in type3.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
				where string.Equals(m.Name, "FromBytes", StringComparison.Ordinal)
				select m).FirstOrDefault((MethodInfo m) =>
			{
				ParameterInfo[] parameters = m.GetParameters();
				return parameters.Length == 3 && parameters[0].ParameterType == typeof(byte[]) && parameters[1].ParameterType.IsInstanceOfType(pool) && parameters[2].ParameterType.IsInstanceOfType(serverMain);
			});
			if (methodInfo2 == null)
			{
				reason = "Compatible Vintage Story ServerChunk.FromBytes overload was not found.";
				TryFreePool(pool, freeAll);
				return null;
			}
			SqliteConnectionStringBuilder sqliteConnectionStringBuilder = new SqliteConnectionStringBuilder
			{
				DataSource = Path.GetFullPath(savePath),
				Mode = SqliteOpenMode.ReadOnly,
				Cache = SqliteCacheMode.Private,
				Pooling = false,
				DefaultTimeout = 10
			};
			sqliteConnection = new SqliteConnection(sqliteConnectionStringBuilder.ConnectionString);
			sqliteConnection.Open();
			using (SqliteCommand sqliteCommand = sqliteConnection.CreateCommand())
			{
				sqliteCommand.CommandText = "PRAGMA query_only=ON; PRAGMA busy_timeout=10000;";
				sqliteCommand.ExecuteNonQuery();
			}
			reason = "Direct read-only save database terrain reader active.";
			return new WebMapSaveDbTerrainReader(api, sqliteConnection, serverMain, pool, methodInfo2, methodInfo, freeAll);
		}
		catch (Exception ex)
		{
			try
			{
				sqliteConnection?.Dispose();
			}
			catch
			{
			}
			if (pool != null)
			{
				TryFreePool(pool, freeAll);
			}
			reason = ex.GetType().Name + ": " + ex.Message;
			return null;
		}
	}

	public bool TryLoadColumn(int chunkX, int chunkZ, int mapSizeY, IServerChunk[] chunks, out ushort[]? rainHeightMap)
	{
		rainHeightMap = null;
		if (disposed || disabled)
		{
			return false;
		}
		try
		{
			byte[] array = ReadBlob(getMapChunk, ToChunkIndex(chunkX, 0, chunkZ));
			if (array == null || array.Length == 0)
			{
				return RecordFailure(chunkX, chunkZ, "mapchunk row was not found");
			}
			using (MemoryStream source = new MemoryStream(array, writable: false))
			{
				TrimmedMapChunk trimmedMapChunk = Serializer.Deserialize<TrimmedMapChunk>(source);
				rainHeightMap = ((trimmedMapChunk != null && trimmedMapChunk.RainHeightMap?.Length >= 1024) ? trimmedMapChunk.RainHeightMap : null);
			}
			int num = Math.Min(chunks.Length, Math.Max(1, (mapSizeY + 32 - 1) / 32));
			bool[] array2 = ArrayPool<bool>.Shared.Rent(Math.Max(1, num));
			long[] array3 = ArrayPool<long>.Shared.Rent(Math.Max(1, num));
			int[] array4 = ArrayPool<int>.Shared.Rent(Math.Max(1, num));
			try
			{
				Array.Clear(array2, 0, num);
				if (rainHeightMap == null)
				{
					Array.Fill(array2, value: true, 0, num);
				}
				int num2 = 0;
				while (rainHeightMap != null && num2 < 1024)
				{
					int num3 = Math.Clamp(rainHeightMap[num2], 0, Math.Max(0, mapSizeY - 1));
					int num4 = Math.Clamp(num3 / 32, 0, num - 1);
					array2[num4] = true;
					if (num4 > 0)
					{
						array2[num4 - 1] = true;
					}
					num2++;
				}
				int num5 = 0;
				for (int i = 0; i < num; i++)
				{
					if (array2[i])
					{
						array3[num5] = ToChunkIndex(chunkX, i, chunkZ);
						array4[num5] = i;
						num5++;
					}
				}
				if (num5 <= 0)
				{
					return RecordFailure(chunkX, chunkZ, "no surface chunk sections were requested");
				}
				SqliteCommand chunkBatchCommand = GetChunkBatchCommand(num5);
				for (int j = 0; j < num5; j++)
				{
					chunkBatchCommand.Parameters[j].Value = array3[j];
				}
				int num6 = 0;
				using (SqliteDataReader sqliteDataReader = chunkBatchCommand.ExecuteReader())
				{
					while (sqliteDataReader.Read())
					{
						long @int = sqliteDataReader.GetInt64(0);
						if (sqliteDataReader.IsDBNull(1))
						{
							continue;
						}
						byte[] array5 = (byte[])sqliteDataReader.GetValue(1);
						if (array5.Length == 0)
						{
							continue;
						}
						int num7 = -1;
						for (int k = 0; k < num5; k++)
						{
							if (array3[k] == @int)
							{
								num7 = array4[k];
								break;
							}
						}
						if (num7 >= 0 && num7 < chunks.Length)
						{
							byte[] array6 = TerrainOnlyBytes(array5);
							object obj = serverChunkFromBytes.Invoke(null, new object[3] { array6, chunkDataPool, serverMain });
							if (obj is IServerChunk serverChunk)
							{
								chunks[num7] = serverChunk;
								num6++;
							}
						}
					}
				}
				if (num6 != num5)
				{
					return RecordFailure(chunkX, chunkZ, "one or more requested terrain sections were missing or could not be decoded");
				}
				consecutiveFailures = 0;
				return true;
			}
			finally
			{
				ArrayPool<int>.Shared.Return(array4);
				ArrayPool<long>.Shared.Return(array3);
				ArrayPool<bool>.Shared.Return(array2);
			}
		}
		catch (TargetInvocationException ex)
		{
			return RecordFailure(chunkX, chunkZ, ex.InnerException?.Message ?? ex.Message);
		}
		catch (Exception ex2)
		{
			return RecordFailure(chunkX, chunkZ, ex2.GetType().Name + ": " + ex2.Message);
		}
	}

	private static byte[] TerrainOnlyBytes(byte[] bytes)
	{
		using MemoryStream source = new MemoryStream(bytes, writable: false);
		TrimmedTerrainChunk instance = Serializer.Deserialize<TrimmedTerrainChunk>(source);
		using MemoryStream memoryStream = new MemoryStream();
		Serializer.Serialize((Stream)memoryStream, instance);
		return memoryStream.ToArray();
	}

	private static void ValidateTerrainContract(Type chunkType)
	{
		Dictionary<int, Type> dictionary = new Dictionary<int, Type>();
		dictionary[1] = typeof(byte[]);
		dictionary[2] = typeof(byte[]);
		dictionary[3] = typeof(byte[]);
		dictionary[12] = typeof(string);
		dictionary[13] = typeof(bool);
		dictionary[15] = typeof(int);
		dictionary[16] = typeof(byte[]);
		Dictionary<int, Type> dictionary2 = dictionary;
		MemberInfo[] members = chunkType.GetMembers(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		foreach (MemberInfo memberInfo in members)
		{
			ProtoMemberAttribute customAttribute = memberInfo.GetCustomAttribute<ProtoMemberAttribute>();
			if (customAttribute != null && dictionary2.TryGetValue(customAttribute.Tag, out var value))
			{
				Type type = ((memberInfo is FieldInfo fieldInfo) ? fieldInfo.FieldType : (memberInfo as PropertyInfo)?.PropertyType);
				if (type != value)
				{
					throw new NotSupportedException("Vintage Story terrain chunk contract changed at field " + customAttribute.Tag);
				}
				dictionary2.Remove(customAttribute.Tag);
			}
		}
		if (dictionary2.Count != 0)
		{
			throw new NotSupportedException("Vintage Story terrain chunk contract is incomplete.");
		}
	}

	private SqliteCommand GetChunkBatchCommand(int count)
	{
		count = Math.Max(1, count);
		if (getChunkBatches.TryGetValue(count, out SqliteCommand value))
		{
			return value;
		}
		SqliteCommand sqliteCommand = connection.CreateCommand();
		string[] array = new string[count];
		for (int i = 0; i < count; i++)
		{
			array[i] = "$p" + i.ToString(CultureInfo.InvariantCulture);
			sqliteCommand.Parameters.Add(array[i], SqliteType.Integer);
		}
		sqliteCommand.CommandText = "SELECT position,data FROM chunk WHERE position IN (" + string.Join(',', array) + ")";
		sqliteCommand.Prepare();
		getChunkBatches[count] = sqliteCommand;
		return sqliteCommand;
	}

	private bool RecordFailure(int chunkX, int chunkZ, string reason)
	{
		LastFailure = reason;
		consecutiveFailures++;
		if (consecutiveFailures <= 2)
		{
			api.Logger.Debug("[ServerEssentials/WebMap] direct save DB terrain read missed at {0},{1}: {2}", chunkX, chunkZ, reason);
		}
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
		object obj = command.ExecuteScalar();
		return obj as byte[];
	}

	private long ToChunkIndex(int x, int y, int z)
	{
		object obj = chunkPosToIndex.Invoke(null, new object[3] { x, y, z });
		if (1 == 0)
		{
		}
		long result;
		if (!(obj is ulong num))
		{
			if (!(obj is long num2))
			{
				throw new InvalidOperationException("Vintage Story ChunkPos.ToChunkIndex returned an unexpected value.");
			}
			result = num2;
		}
		else
		{
			result = (long)num;
		}
		if (1 == 0)
		{
		}
		return result;
	}

	private static object? FindServerMain(ICoreServerAPI api)
	{
		object server = api.Server;
		if (server != null && string.Equals(server.GetType().Name, "ServerMain", StringComparison.Ordinal))
		{
			return server;
		}
		object[] array = new object[4] { server, api, api.WorldManager, api.World };
		foreach (object obj in array)
		{
			if (obj == null)
			{
				continue;
			}
			object obj2 = obj;
			Type type = obj2.GetType();
			FieldInfo[] fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			foreach (FieldInfo fieldInfo in fields)
			{
				if (!string.Equals(fieldInfo.FieldType.Name, "ServerMain", StringComparison.Ordinal))
				{
					continue;
				}
				try
				{
					object value = fieldInfo.GetValue(obj2);
					if (value != null)
					{
						return value;
					}
				}
				catch
				{
				}
			}
			PropertyInfo[] properties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			foreach (PropertyInfo propertyInfo in properties)
			{
				if (!string.Equals(propertyInfo.PropertyType.Name, "ServerMain", StringComparison.Ordinal) || propertyInfo.GetIndexParameters().Length != 0)
				{
					continue;
				}
				try
				{
					object value2 = propertyInfo.GetValue(obj2);
					if (value2 != null)
					{
						return value2;
					}
				}
				catch
				{
				}
			}
		}
		return null;
	}

	private static Type[] SafeGetTypes(Assembly assembly)
	{
		try
		{
			return assembly.GetTypes();
		}
		catch (ReflectionTypeLoadException ex)
		{
			return ex.Types.Where((Type t) => t != null).Cast<Type>().ToArray();
		}
	}

	private static void TryFreePool(object pool, MethodInfo? freeAll)
	{
		try
		{
			freeAll?.Invoke(pool, null);
		}
		catch
		{
		}
	}

	public void Dispose()
	{
		if (disposed)
		{
			return;
		}
		disposed = true;
		try
		{
			getMapChunk.Dispose();
		}
		catch
		{
		}
		foreach (SqliteCommand value in getChunkBatches.Values)
		{
			try
			{
				value.Dispose();
			}
			catch
			{
			}
		}
		getChunkBatches.Clear();
		try
		{
			connection.Dispose();
		}
		catch
		{
		}
		TryFreePool(chunkDataPool, chunkDataPoolFreeAll);
	}
}
