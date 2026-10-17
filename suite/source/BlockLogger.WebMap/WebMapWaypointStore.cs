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

internal sealed class WebMapWaypointStore
{
	private sealed class WebMapWaypointDocument
	{
		public int SchemaVersion { get; set; } = 3;

		public string WorldId { get; set; } = "";

		[JsonRequired]
		public List<WebMapWaypoint> Waypoints { get; set; } = new List<WebMapWaypoint>();
	}

	private const int MaxWaypoints = 500;

	private readonly ICoreServerAPI api;

	private readonly object gate = new object();

	private readonly string filePath;

	private readonly string worldId;

	private readonly JsonSerializerOptions options = new JsonSerializerOptions
	{
		WriteIndented = true
	};

	private List<WebMapWaypoint> waypoints = new List<WebMapWaypoint>();

	private bool storageReady = true;

	public string StorageError { get; private set; } = "";

	public WebMapWaypointStore(ICoreServerAPI api, string mapDataRoot)
	{
		this.api = api;
		worldId = api.World.SavegameIdentifier ?? api.WorldManager.SaveGame.SavegameIdentifier ?? "unknownworld";
		string text = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(worldId))).ToLowerInvariant().Substring(0, 16);
		filePath = Path.Combine(mapDataRoot, "waypoints-" + text + ".json");
		Load();
	}

	public List<WebMapWaypoint> Snapshot()
	{
		lock (gate)
		{
			List<WebMapWaypoint> list = new List<WebMapWaypoint>(waypoints.Count);
			for (int i = 0; i < waypoints.Count; i++)
			{
				list.Add(waypoints[i].Clone());
			}
			return list;
		}
	}

	public int CountPlayerWaypoints(string playerUid)
	{
		return CountPlayerWaypoints(playerUid, null);
	}

	public int CountPlayerWaypoints(string playerUid, string? type)
	{
		string text = (playerUid ?? "").Trim();
		string text2 = (string.IsNullOrWhiteSpace(type) ? "" : NormalizeType(type));
		if (text.Length == 0)
		{
			return 0;
		}
		lock (gate)
		{
			int num = 0;
			for (int i = 0; i < waypoints.Count; i++)
			{
				WebMapWaypoint webMapWaypoint = waypoints[i];
				if (webMapWaypoint.IsPlayerWaypoint && string.Equals(webMapWaypoint.OwnerPlayerUid, text, StringComparison.Ordinal) && (text2.Length == 0 || string.Equals(NormalizeType(webMapWaypoint.Type), text2, StringComparison.Ordinal)))
				{
					num++;
				}
			}
			return num;
		}
	}

	public WebMapWaypoint CreatePlayerWaypoint(string playerUid, string playerName, string type, string name, string icon, double x, double y, double z, int dimension, int maxPlayerWaypoints)
	{
		string text = (playerUid ?? "").Trim();
		string text2 = (playerName ?? "player").Trim();
		string text3 = (name ?? "").Trim();
		string text4 = NormalizeType(type);
		string icon2 = NormalizeIcon(icon);
		if (text.Length == 0)
		{
			throw new InvalidOperationException("Player UID is unavailable.");
		}
		if (text3.Length == 0)
		{
			throw new InvalidOperationException("Waypoint name is required.");
		}
		if (text3.Length > 80)
		{
			text3 = text3.Substring(0, 80);
		}
		if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z))
		{
			throw new InvalidOperationException("Player position is unavailable.");
		}
		maxPlayerWaypoints = Math.Clamp(maxPlayerWaypoints, 0, 100);
		if (maxPlayerWaypoints <= 0)
		{
			throw new InvalidOperationException("Player-created Web Map waypoints are disabled by the server.");
		}
		lock (gate)
		{
			EnsureWritable();
			int num = 0;
			for (int i = 0; i < waypoints.Count; i++)
			{
				WebMapWaypoint webMapWaypoint = waypoints[i];
				if (webMapWaypoint.IsPlayerWaypoint && string.Equals(webMapWaypoint.OwnerPlayerUid, text, StringComparison.Ordinal))
				{
					if (string.Equals(webMapWaypoint.Name, text3, StringComparison.OrdinalIgnoreCase))
					{
						throw new InvalidOperationException("You already have a waypoint named '" + text3 + "'. Remove it first or use another name.");
					}
					if (string.Equals(NormalizeType(webMapWaypoint.Type), text4, StringComparison.Ordinal))
					{
						num++;
					}
				}
			}
			if (num >= maxPlayerWaypoints)
			{
				throw new InvalidOperationException($"Waypoint limit reached ({num}/{maxPlayerWaypoints}). Remove one before adding another.");
			}
			if (waypoints.Count >= 500)
			{
				throw new InvalidOperationException($"Web Map is limited to {500} waypoints per world.");
			}
			long num2 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
			if (text2.Length == 0)
			{
				text2 = "player";
			}
			if (text2.Length > 80)
			{
				text2 = text2.Substring(0, 80);
			}
			WebMapWaypoint webMapWaypoint2 = new WebMapWaypoint
			{
				Id = Guid.NewGuid().ToString("N"),
				Name = text3,
				Description = "",
				Type = text4,
				Icon = icon2,
				X = x,
				Y = y,
				Z = z,
				Dimension = Math.Clamp(dimension, -1024, 1024),
				Color = ((text4 == "major") ? "#f59e0b" : "#38bdf8"),
				IsPlayerWaypoint = true,
				OwnerPlayerUid = text,
				OwnerPlayerName = text2,
				CreatedBy = text2,
				CreatedUnixMilliseconds = num2,
				UpdatedBy = text2,
				UpdatedUnixMilliseconds = num2
			};
			List<WebMapWaypoint> next = new List<WebMapWaypoint>(waypoints) { webMapWaypoint2 };
			CommitLocked(next);
			return webMapWaypoint2.Clone();
		}
	}

	public bool DeletePlayerWaypoint(string playerUid, string nameOrId, out WebMapWaypoint? deleted)
	{
		deleted = null;
		string text = (playerUid ?? "").Trim();
		string text2 = (nameOrId ?? "").Trim();
		if (text.Length == 0 || text2.Length == 0)
		{
			return false;
		}
		lock (gate)
		{
			EnsureWritable();
			int num = -1;
			for (int i = 0; i < waypoints.Count; i++)
			{
				WebMapWaypoint webMapWaypoint = waypoints[i];
				if (webMapWaypoint.IsPlayerWaypoint && string.Equals(webMapWaypoint.OwnerPlayerUid, text, StringComparison.Ordinal) && (string.Equals(webMapWaypoint.Name, text2, StringComparison.OrdinalIgnoreCase) || string.Equals(webMapWaypoint.Id, text2, StringComparison.OrdinalIgnoreCase) || (text2.Length >= 6 && webMapWaypoint.Id.StartsWith(text2, StringComparison.OrdinalIgnoreCase))))
				{
					num = i;
					break;
				}
			}
			if (num < 0)
			{
				return false;
			}
			List<WebMapWaypoint> list = new List<WebMapWaypoint>(waypoints);
			WebMapWaypoint webMapWaypoint2 = list[num].Clone();
			list.RemoveAt(num);
			CommitLocked(list);
			deleted = webMapWaypoint2;
			return true;
		}
	}

	public WebMapWaypoint Upsert(WebMapWaypoint incoming, string actor)
	{
		if (incoming == null)
		{
			incoming = new WebMapWaypoint();
		}
		string text = (incoming.Name ?? "").Trim();
		if (text.Length == 0)
		{
			throw new InvalidOperationException("Waypoint name is required.");
		}
		if (text.Length > 80)
		{
			text = text.Substring(0, 80);
		}
		string text2 = (incoming.Description ?? "").Trim();
		if (text2.Length > 300)
		{
			text2 = text2.Substring(0, 300);
		}
		if (!double.IsFinite(incoming.X) || !double.IsFinite(incoming.Y) || !double.IsFinite(incoming.Z))
		{
			throw new InvalidOperationException("Waypoint coordinates must be finite numbers.");
		}
		long num = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		string text3 = (incoming.Id ?? "").Trim();
		if (text3.Length > 64)
		{
			text3 = "";
		}
		string text4 = (actor ?? "admin").Trim();
		if (text4.Length == 0)
		{
			text4 = "admin";
		}
		if (text4.Length > 80)
		{
			text4 = text4.Substring(0, 80);
		}
		string type = NormalizeType(incoming.Type);
		string icon = NormalizeIcon(incoming.Icon);
		lock (gate)
		{
			EnsureWritable();
			int num2 = -1;
			if (text3.Length > 0)
			{
				for (int i = 0; i < waypoints.Count; i++)
				{
					if (string.Equals(waypoints[i].Id, text3, StringComparison.Ordinal))
					{
						num2 = i;
						break;
					}
				}
			}
			List<WebMapWaypoint> list = new List<WebMapWaypoint>(waypoints);
			WebMapWaypoint webMapWaypoint2;
			if (num2 >= 0)
			{
				WebMapWaypoint webMapWaypoint = waypoints[num2];
				webMapWaypoint2 = (list[num2] = new WebMapWaypoint
				{
					Id = webMapWaypoint.Id,
					Name = text,
					Description = text2,
					Type = type,
					Icon = icon,
					X = incoming.X,
					Y = incoming.Y,
					Z = incoming.Z,
					Dimension = Math.Clamp(incoming.Dimension, -1024, 1024),
					Color = NormalizeColor(incoming.Color),
					IsPlayerWaypoint = webMapWaypoint.IsPlayerWaypoint,
					OwnerPlayerUid = webMapWaypoint.OwnerPlayerUid,
					OwnerPlayerName = webMapWaypoint.OwnerPlayerName,
					CreatedBy = webMapWaypoint.CreatedBy,
					CreatedUnixMilliseconds = webMapWaypoint.CreatedUnixMilliseconds,
					UpdatedBy = text4,
					UpdatedUnixMilliseconds = num
				});
			}
			else
			{
				if (waypoints.Count >= 500)
				{
					throw new InvalidOperationException($"Web Map is limited to {500} waypoints per world.");
				}
				webMapWaypoint2 = new WebMapWaypoint
				{
					Id = Guid.NewGuid().ToString("N"),
					Name = text,
					Description = text2,
					Type = type,
					Icon = icon,
					X = incoming.X,
					Y = incoming.Y,
					Z = incoming.Z,
					Dimension = Math.Clamp(incoming.Dimension, -1024, 1024),
					Color = NormalizeColor(incoming.Color),
					IsPlayerWaypoint = false,
					CreatedBy = text4,
					CreatedUnixMilliseconds = num,
					UpdatedBy = text4,
					UpdatedUnixMilliseconds = num
				};
				list.Add(webMapWaypoint2);
			}
			CommitLocked(list);
			return webMapWaypoint2.Clone();
		}
	}

	public bool Delete(string? id)
	{
		string target = (id ?? "").Trim();
		if (target.Length == 0)
		{
			return false;
		}
		lock (gate)
		{
			EnsureWritable();
			List<WebMapWaypoint> list = new List<WebMapWaypoint>(waypoints);
			int num = list.RemoveAll((WebMapWaypoint x) => string.Equals(x.Id, target, StringComparison.Ordinal));
			if (num <= 0)
			{
				return false;
			}
			CommitLocked(list);
			return true;
		}
	}

	private void Load()
	{
		if (!File.Exists(filePath))
		{
			return;
		}
		try
		{
			WebMapWaypointDocument webMapWaypointDocument = JsonStateRecovery.Read<WebMapWaypointDocument>(filePath, options);
			if (webMapWaypointDocument.SchemaVersion < 1 || webMapWaypointDocument.SchemaVersion > 3 || !string.Equals(webMapWaypointDocument.WorldId ?? "", worldId, StringComparison.Ordinal) || webMapWaypointDocument.Waypoints == null || webMapWaypointDocument.Waypoints.Count > 500)
			{
				throw new InvalidDataException("Unsupported, foreign-world or incomplete waypoint document.");
			}
			HashSet<string> hashSet = new HashSet<string>(StringComparer.Ordinal);
			List<WebMapWaypoint> list = new List<WebMapWaypoint>(Math.Min(webMapWaypointDocument.Waypoints?.Count ?? 0, 500));
			if (webMapWaypointDocument.Waypoints != null)
			{
				for (int i = 0; i < webMapWaypointDocument.Waypoints.Count; i++)
				{
					if (list.Count >= 500)
					{
						break;
					}
					WebMapWaypoint webMapWaypoint = webMapWaypointDocument.Waypoints[i];
					if (webMapWaypoint == null || string.IsNullOrWhiteSpace(webMapWaypoint.Id) || string.IsNullOrWhiteSpace(webMapWaypoint.Name) || !hashSet.Add(webMapWaypoint.Id))
					{
						throw new InvalidDataException("Invalid or duplicate waypoint identity.");
					}
					if (!double.IsFinite(webMapWaypoint.X) || !double.IsFinite(webMapWaypoint.Y) || !double.IsFinite(webMapWaypoint.Z))
					{
						throw new InvalidDataException("Invalid waypoint coordinates.");
					}
					webMapWaypoint.Name = webMapWaypoint.Name.Trim();
					if (webMapWaypoint.Name.Length > 80)
					{
						webMapWaypoint.Name = webMapWaypoint.Name.Substring(0, 80);
					}
					webMapWaypoint.Description = (webMapWaypoint.Description ?? "").Trim();
					if (webMapWaypoint.Description.Length > 300)
					{
						webMapWaypoint.Description = webMapWaypoint.Description.Substring(0, 300);
					}
					webMapWaypoint.Type = NormalizeType(webMapWaypoint.Type);
					webMapWaypoint.Icon = NormalizeIcon(webMapWaypoint.Icon);
					webMapWaypoint.Color = NormalizeColor(webMapWaypoint.Color);
					webMapWaypoint.OwnerPlayerUid = (webMapWaypoint.OwnerPlayerUid ?? "").Trim();
					webMapWaypoint.OwnerPlayerName = (webMapWaypoint.OwnerPlayerName ?? "").Trim();
					if (webMapWaypoint.IsPlayerWaypoint && webMapWaypoint.OwnerPlayerUid.Length == 0)
					{
						throw new InvalidDataException("Player waypoint ownership is missing.");
					}
					list.Add(webMapWaypoint);
				}
			}
			lock (gate)
			{
				waypoints = list;
			}
		}
		catch (Exception ex)
		{
			storageReady = false;
			StorageError = "Waypoint storage could not be read. The original file was retained; restore it and reload the Suite before editing waypoints.";
			try
			{
				JsonStateRecovery.Preserve(filePath);
			}
			catch
			{
			}
			api.Logger.Warning("[ServerEssentials/WebMap] waypoint storage unavailable ({0}); edits are blocked and the original file was retained.", ex.GetType().Name);
		}
	}

	private void EnsureWritable()
	{
		if (!storageReady)
		{
			throw new InvalidOperationException(StorageError);
		}
	}

	private void CommitLocked(List<WebMapWaypoint> next)
	{
		string directoryName = Path.GetDirectoryName(filePath);
		if (!string.IsNullOrWhiteSpace(directoryName))
		{
			Directory.CreateDirectory(directoryName);
		}
		string text = filePath + ".tmp";
		WebMapWaypointDocument value = new WebMapWaypointDocument
		{
			SchemaVersion = 3,
			WorldId = worldId,
			Waypoints = next
		};
		try
		{
			byte[] array = JsonSerializer.SerializeToUtf8Bytes(value, options);
			using (FileStream fileStream = new FileStream(text, FileMode.Create, FileAccess.Write, FileShare.None))
			{
				fileStream.Write(array);
				fileStream.Flush(flushToDisk: true);
			}
			File.Move(text, filePath, overwrite: true);
			waypoints = next;
			StorageError = "";
		}
		catch
		{
			StorageError = "Waypoint changes could not be saved. Existing waypoints are unchanged; retry when storage is writable.";
			throw new InvalidOperationException(StorageError);
		}
	}

	internal static string NormalizeType(string? value)
	{
		string text = (value ?? "").Trim().ToLowerInvariant();
		return (text == "minor") ? "minor" : "major";
	}

	internal static string NormalizeIcon(string? value)
	{
		string text = (value ?? "").Trim().ToLowerInvariant();
		if (1 == 0)
		{
		}
		string result;
		switch (text)
		{
		case "base":
		case "home":
			result = "base";
			break;
		case "settlement":
		case "town":
		case "village":
			result = "settlement";
			break;
		case "shop":
		case "market":
			result = "shop";
			break;
		case "mine":
		case "cave":
			result = "mine";
			break;
		case "resource":
			result = "resource";
			break;
		case "danger":
		case "warning":
			result = "danger";
			break;
		case "landmark":
			result = "landmark";
			break;
		case "road":
		case "route":
			result = "road";
			break;
		case "farm":
			result = "farm";
			break;
		default:
			result = "pin";
			break;
		}
		if (1 == 0)
		{
		}
		return result;
	}

	private static string NormalizeColor(string? value)
	{
		string text = (value ?? "").Trim();
		if (text.Length == 7 && text[0] == '#')
		{
			for (int i = 1; i < text.Length; i++)
			{
				char c = text[i];
				if ((c < '0' || c > '9') && (c < 'a' || c > 'f') && (c < 'A' || c > 'F'))
				{
					return "#38bdf8";
				}
			}
			return text.ToLowerInvariant();
		}
		return "#38bdf8";
	}
}
