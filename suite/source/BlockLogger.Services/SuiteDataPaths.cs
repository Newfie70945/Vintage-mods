using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Vintagestory.API.Server;

namespace BlockLogger.Services;

public static class SuiteDataPaths
{
	public static string GetSuiteRoot(ICoreServerAPI api)
	{
		string orCreateDataPath = api.GetOrCreateDataPath("ModData");
		string text = Path.Combine(orCreateDataPath, "NewfiesServerEssentialsSuite");
		Directory.CreateDirectory(text);
		return text;
	}

	public static string GetWorldRoot(ICoreServerAPI api)
	{
		string suiteRoot = GetSuiteRoot(api);
		string text = Path.Combine(suiteRoot, "worlds");
		Directory.CreateDirectory(text);
		string worldId = api.World.SavegameIdentifier ?? "unknownworld";
		string path = MakeWorldFolder(worldId);
		string text2 = Path.Combine(text, path);
		Directory.CreateDirectory(text2);
		return text2;
	}

	public static string GetBackupsRoot(ICoreServerAPI api)
	{
		string text = Path.Combine(GetWorldRoot(api), "backups");
		Directory.CreateDirectory(text);
		return text;
	}

	public static string MakeWorldFolder(string worldId)
	{
		if (worldId == null)
		{
			worldId = "unknownworld";
		}
		StringBuilder stringBuilder = new StringBuilder();
		string text = worldId;
		foreach (char c in text)
		{
			bool flag = char.IsLetterOrDigit(c);
			bool flag2 = flag;
			if (!flag2)
			{
				bool flag3 = ((c == '-' || c == '_') ? true : false);
				flag2 = flag3;
			}
			if (flag2)
			{
				stringBuilder.Append(c);
			}
			else if (char.IsWhiteSpace(c))
			{
				stringBuilder.Append('-');
			}
			if (stringBuilder.Length >= 40)
			{
				break;
			}
		}
		string text2 = ((stringBuilder.Length == 0) ? "world" : stringBuilder.ToString().Trim('-'));
		if (text2.Length == 0)
		{
			text2 = "world";
		}
		string text3 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(worldId))).ToLowerInvariant().Substring(0, 12);
		return text2 + "-" + text3;
	}
}
