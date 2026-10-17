using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace BlockLogger.WebMap;

internal static class WebMapExportLogSuppressor
{
	private sealed class Scope : IDisposable
	{
		private readonly bool enabled;

		private int disposed;

		public Scope(bool enabled)
		{
			this.enabled = enabled;
		}

		public void Dispose()
		{
			if (enabled && Interlocked.Exchange(ref disposed, 1) == 0)
			{
				EndScope();
			}
		}
	}

	private const string HarmonyId = "newfies.serveressentials.webmap.exportlognoise";

	private const string EntityErrorPrefix = "Failed loading an entity (type ";

	private const string EntityErrorSuffix = ") in a chunk. Will discard, sorry. Exception logged to verbose debug.";

	private const string EntityVerbosePrefix = "Failed loading an entity in a chunk. Will discard, sorry. Exception:";

	private static readonly object InstallLock = new object();

	private static readonly ConcurrentDictionary<string, int> SuppressedByType = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);

	private static int activeScopes;

	private static int installed;

	private static int installFailed;

	private static ICoreServerAPI? serverApi;

	private static object? harmonyInstance;

	public static IDisposable Begin(ICoreServerAPI api)
	{
		if (serverApi == null)
		{
			serverApi = api;
		}
		EnsureInstalled(api);
		if (Volatile.Read(in installed) == 1)
		{
			if (Interlocked.Increment(ref activeScopes) == 1)
			{
				SuppressedByType.Clear();
			}
			return new Scope(enabled: true);
		}
		return new Scope(enabled: false);
	}

	private static void EnsureInstalled(ICoreServerAPI api)
	{
		if (Volatile.Read(in installed) == 1 || Volatile.Read(in installFailed) == 1)
		{
			return;
		}
		lock (InstallLock)
		{
			if (installed == 1 || installFailed == 1)
			{
				return;
			}
			try
			{
				Assembly assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault((Assembly a) => string.Equals(a.GetName().Name, "0Harmony", StringComparison.OrdinalIgnoreCase));
				if (assembly == null)
				{
					try
					{
						assembly = Assembly.Load("0Harmony");
					}
					catch
					{
					}
				}
				Type type = assembly?.GetType("HarmonyLib.Harmony");
				Type harmonyMethodType = assembly?.GetType("HarmonyLib.HarmonyMethod");
				if (type == null || harmonyMethodType == null)
				{
					throw new InvalidOperationException("0Harmony was not available in the server AppDomain.");
				}
				MethodInfo methodInfo = typeof(LoggerBase).GetMethods(BindingFlags.Instance | BindingFlags.Public).FirstOrDefault((MethodInfo m) =>
				{
					if (!string.Equals(m.Name, "Log", StringComparison.Ordinal))
					{
						return false;
					}
					ParameterInfo[] parameters2 = m.GetParameters();
					return parameters2.Length == 3 && parameters2[0].ParameterType == typeof(EnumLogType) && parameters2[1].ParameterType == typeof(string) && parameters2[2].ParameterType == typeof(object[]);
				});
				MethodInfo method = typeof(WebMapExportLogSuppressor).GetMethod("BeforeLoggerLog", BindingFlags.Static | BindingFlags.NonPublic);
				if (methodInfo == null || method == null)
				{
					throw new MissingMethodException("Vintage Story LoggerBase.Log(EnumLogType,string,object[]) was not found.");
				}
				object obj2 = Activator.CreateInstance(type, "newfies.serveressentials.webmap.exportlognoise");
				object obj3 = Activator.CreateInstance(harmonyMethodType, method);
				if (obj2 == null || obj3 == null)
				{
					throw new InvalidOperationException("Harmony patch objects could not be created.");
				}
				MethodInfo methodInfo2 = (from m in type.GetMethods(BindingFlags.Instance | BindingFlags.Public)
					where string.Equals(m.Name, "Patch", StringComparison.Ordinal)
					select m).FirstOrDefault((MethodInfo m) =>
				{
					ParameterInfo[] parameters2 = m.GetParameters();
					return parameters2.Length >= 2 && typeof(MethodBase).IsAssignableFrom(parameters2[0].ParameterType) && parameters2[1].ParameterType == harmonyMethodType;
				});
				if (methodInfo2 == null)
				{
					throw new MissingMethodException("Compatible Harmony.Patch method was not found.");
				}
				ParameterInfo[] parameters = methodInfo2.GetParameters();
				object[] array = new object[parameters.Length];
				array[0] = methodInfo;
				array[1] = obj3;
				methodInfo2.Invoke(obj2, array);
				harmonyInstance = obj2;
				Volatile.Write(ref installed, 1);
				api.Logger.Notification("[ServerEssentials/WebMap] export-only entity-load log suppression is active. Only Vintage Story's failed chunk-entity load message pair is filtered while terrain export is running.");
			}
			catch (Exception ex)
			{
				Volatile.Write(ref installFailed, 1);
				api.Logger.Warning("[ServerEssentials/WebMap] export-only entity-load log suppression could not be installed; terrain export will continue normally. {0}: {1}", ex.GetType().Name, ex.Message);
			}
		}
	}

	private static bool BeforeLoggerLog(EnumLogType logType, string format, object[] args)
	{
		try
		{
			if (Volatile.Read(in activeScopes) <= 0 || string.IsNullOrEmpty(format))
			{
				return true;
			}
			if (logType == EnumLogType.Error && format.StartsWith("Failed loading an entity (type ", StringComparison.Ordinal) && format.EndsWith(") in a chunk. Will discard, sorry. Exception logged to verbose debug.", StringComparison.Ordinal))
			{
				string key = ExtractEntityType(format);
				SuppressedByType.AddOrUpdate(key, 1, (string _, int count) => count + 1);
				return false;
			}
			if (logType == EnumLogType.VerboseDebug && format.StartsWith("Failed loading an entity in a chunk. Will discard, sorry. Exception:", StringComparison.Ordinal))
			{
				return false;
			}
		}
		catch
		{
		}
		return true;
	}

	private static string ExtractEntityType(string message)
	{
		int length = "Failed loading an entity (type ".Length;
		int num = message.IndexOf(") in a chunk. Will discard, sorry. Exception logged to verbose debug.", length, StringComparison.Ordinal);
		if (num <= length)
		{
			return "unknown";
		}
		int num2 = length;
		string text = message.Substring(num2, num - num2).Trim();
		return (text.Length == 0) ? "unknown" : text;
	}

	private static void EndScope()
	{
		int num = Interlocked.Decrement(ref activeScopes);
		if (num > 0)
		{
			return;
		}
		if (num < 0)
		{
			Interlocked.Exchange(ref activeScopes, 0);
			return;
		}
		int num2 = SuppressedByType.Values.Sum();
		if (num2 <= 0)
		{
			return;
		}
		try
		{
			string text = string.Join(", ", from kv in SuppressedByType.OrderByDescending((KeyValuePair<string, int> kv) => kv.Value).ThenBy((KeyValuePair<string, int> kv) => kv.Key, StringComparer.OrdinalIgnoreCase).Take(8)
				select $"{kv.Key}={kv.Value}");
			serverApi?.Logger.Notification("[ServerEssentials/WebMap] terrain export suppressed {0:n0} expected chunk-entity load error(s){1}. These messages were export-only noise; unrelated errors were not filtered.", num2, string.IsNullOrWhiteSpace(text) ? "" : (" (" + text + ")"));
		}
		catch
		{
		}
		finally
		{
			SuppressedByType.Clear();
		}
	}
}
