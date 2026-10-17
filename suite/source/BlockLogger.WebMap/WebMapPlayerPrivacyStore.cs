using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Newfies.Storage;
using Vintagestory.API.Server;

namespace BlockLogger.WebMap;

internal sealed class WebMapPlayerPrivacyStore
{
	private sealed class PrivacyDocument
	{
		public int SchemaVersion { get; set; } = 1;

		[JsonRequired]
		public List<string> HiddenPlayerUids { get; set; } = new List<string>();
	}

	private readonly object gate = new object();

	private readonly ICoreServerAPI api;

	private bool storageReady = true;

	private bool dirty;

	private readonly string filePath;

	private readonly JsonSerializerOptions options = new JsonSerializerOptions
	{
		WriteIndented = true
	};

	private HashSet<string> hiddenPlayerUids = new HashSet<string>(StringComparer.Ordinal);

	public string StorageError { get; private set; } = "";

	public WebMapPlayerPrivacyStore(ICoreServerAPI api, string mapDataRoot)
	{
		this.api = api;
		string s = api.World.SavegameIdentifier ?? api.WorldManager.SaveGame.SavegameIdentifier ?? "unknownworld";
		string text = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant().Substring(0, 16);
		filePath = Path.Combine(mapDataRoot, "player-privacy-" + text + ".json");
		Load();
	}

	public bool IsHidden(string? playerUid)
	{
		string text = (playerUid ?? "").Trim();
		if (text.Length == 0)
		{
			return false;
		}
		lock (gate)
		{
			return !storageReady || hiddenPlayerUids.Contains(text);
		}
	}

	public bool SetHidden(string? playerUid, bool hidden)
	{
		string text = (playerUid ?? "").Trim();
		if (text.Length == 0)
		{
			return false;
		}
		lock (gate)
		{
			if (!storageReady)
			{
				throw new InvalidOperationException(StorageError);
			}
			HashSet<string> hashSet = new HashSet<string>(hiddenPlayerUids, StringComparer.Ordinal);
			bool flag = (hidden ? hashSet.Add(text) : hashSet.Remove(text));
			if (!flag && !dirty)
			{
				return false;
			}
			try
			{
				SaveLocked(hashSet);
				hiddenPlayerUids = hashSet;
				dirty = false;
				StorageError = "";
			}
			catch
			{
				if (hidden)
				{
					hiddenPlayerUids.Add(text);
					dirty = true;
				}
				StorageError = "Live-map privacy could not be saved. Hidden players remain hidden; retry when storage is writable.";
				throw new InvalidOperationException(StorageError);
			}
			return flag;
		}
	}

	private void Load()
	{
		try
		{
			if (!File.Exists(filePath))
			{
				return;
			}
			PrivacyDocument privacyDocument = JsonStateRecovery.Read<PrivacyDocument>(filePath, options);
			if (privacyDocument.SchemaVersion != 1 || privacyDocument.HiddenPlayerUids == null)
			{
				throw new InvalidDataException("Unsupported or incomplete privacy document.");
			}
			HashSet<string> hashSet = new HashSet<string>(StringComparer.Ordinal);
			foreach (string hiddenPlayerUid in privacyDocument.HiddenPlayerUids)
			{
				string text = (hiddenPlayerUid ?? "").Trim();
				if (text.Length == 0 || text.Length > 200)
				{
					throw new InvalidDataException("Invalid privacy identity.");
				}
				hashSet.Add(text);
			}
			hiddenPlayerUids = hashSet;
		}
		catch (Exception ex)
		{
			storageReady = false;
			StorageError = "Live-player map data is hidden because privacy storage could not be read. Restore the privacy file and reload the Suite.";
			try
			{
				JsonStateRecovery.Preserve(filePath);
			}
			catch
			{
			}
			api.Logger.Warning("[ServerEssentials/WebMap] privacy storage unavailable ({0}); live-player exposure is blocked and the original file was retained.", ex.GetType().Name);
		}
	}

	private void SaveLocked(HashSet<string> next)
	{
		string path = Path.GetDirectoryName(filePath) ?? ".";
		Directory.CreateDirectory(path);
		string text = filePath + ".tmp";
		PrivacyDocument value = new PrivacyDocument
		{
			HiddenPlayerUids = new List<string>(next)
		};
		byte[] array = JsonSerializer.SerializeToUtf8Bytes(value, options);
		using (FileStream fileStream = new FileStream(text, FileMode.Create, FileAccess.Write, FileShare.None))
		{
			fileStream.Write(array);
			fileStream.Flush(flushToDisk: true);
		}
		File.Move(text, filePath, overwrite: true);
	}
}
