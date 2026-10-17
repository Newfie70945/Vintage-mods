using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BlockLogger.Services;

internal static class WorldSavePathResolver
{
	internal static string Resolve(object worldManager, string dataBasePath)
	{
		Type type = worldManager.GetType();
		List<string> list = new List<string>();
		string[] array = new string[2] { "CurrentWorldFilepath", "CurrentWorldName" };
		foreach (string name in array)
		{
			string text = null;
			if (type.GetProperty(name)?.GetValue(worldManager) is string text2)
			{
				text = text2;
			}
			else
			{
				Type[] interfaces = type.GetInterfaces();
				foreach (Type type2 in interfaces)
				{
					if (type2.GetProperty(name)?.GetValue(worldManager) is string text3)
					{
						text = text3;
						break;
					}
				}
			}
			if (string.IsNullOrWhiteSpace(text))
			{
				continue;
			}
			foreach (string item in BuildWorldPathCandidates(text, dataBasePath))
			{
				string fullPath;
				try
				{
					fullPath = Path.GetFullPath(item);
				}
				catch
				{
					continue;
				}
				if (!list.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
				{
					list.Add(fullPath);
				}
				if (File.Exists(fullPath))
				{
					return fullPath;
				}
			}
		}
		string value = ((list.Count == 0) ? "no candidate paths were produced" : string.Join("; ", list));
		throw new FileNotFoundException("Unable to locate the active Vintage Story save database. " + $"Checked: {value}. DataBasePath='{dataBasePath}'.");
	}

	private static IEnumerable<string> BuildWorldPathCandidates(string rawValue, string dataBasePath)
	{
		string value = rawValue.Trim();
		if (value.Length == 0)
		{
			yield break;
		}
		yield return value;
		if (!Path.HasExtension(value))
		{
			yield return value + ".vcdbs";
		}
		if (Path.IsPathRooted(value))
		{
			yield break;
		}
		string normalized = value.Replace('\\', '/').TrimStart('/');
		if (normalized.StartsWith("data/", StringComparison.OrdinalIgnoreCase))
		{
			string relativeToData = normalized.Substring("data/".Length);
			yield return Path.Combine(dataBasePath, relativeToData.Replace('/', Path.DirectorySeparatorChar));
			if (!Path.HasExtension(relativeToData))
			{
				yield return Path.Combine(dataBasePath, (relativeToData + ".vcdbs").Replace('/', Path.DirectorySeparatorChar));
			}
		}
		if (normalized.StartsWith("Saves/", StringComparison.OrdinalIgnoreCase))
		{
			yield return Path.Combine(dataBasePath, normalized.Replace('/', Path.DirectorySeparatorChar));
			if (!Path.HasExtension(normalized))
			{
				yield return Path.Combine(dataBasePath, (normalized + ".vcdbs").Replace('/', Path.DirectorySeparatorChar));
			}
		}
		string fileName = Path.GetFileName(normalized);
		if (!string.IsNullOrWhiteSpace(fileName))
		{
			yield return Path.Combine(dataBasePath, "Saves", fileName);
			if (!Path.HasExtension(fileName))
			{
				yield return Path.Combine(dataBasePath, "Saves", fileName + ".vcdbs");
			}
		}
	}
}
