using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using BlockLogger.Afk;
using BlockLogger.AntiCheat;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace BlockLogger.Admin;

public sealed class JailService : IDisposable
{
	private const string SaveKey = "newfiesserveressentials:jail-v1";

	private const double EscapeRadius = 3.0;

	private readonly ICoreServerAPI api;

	private readonly Action<string, string, string, string, string, string> audit;

	private readonly Dictionary<string, JailSentence> sentences = new Dictionary<string, JailSentence>(StringComparer.OrdinalIgnoreCase);

	private JailPosition? jail;

	private long tickListenerId = -1L;

	private bool disposed;

	public JailService(ICoreServerAPI api, Action<string, string, string, string, string, string> audit)
	{
		this.api = api;
		this.audit = audit;
	}

	public void Start()
	{
		Load();
		api.Event.PlayerNowPlaying += OnPlayerNowPlaying;
		api.Event.PlayerRespawn += OnPlayerRespawn;
		api.Event.GameWorldSave += Save;
		tickListenerId = api.Event.RegisterGameTickListener(OnTick, 1000);
		foreach (IServerPlayer item in api.World.AllOnlinePlayers.OfType<IServerPlayer>())
		{
			EnforceJoinState(item);
		}
		api.Logger.Notification("[ServerEssentials/Jail] Started. jailConfigured={0}, activeOrPending={1}.", jail != null, sentences.Count);
	}

	public bool IsJailed(string? playerUid)
	{
		if (string.IsNullOrWhiteSpace(playerUid))
		{
			return false;
		}
		if (!sentences.TryGetValue(playerUid, out JailSentence value) || value.ReleasePending)
		{
			return false;
		}
		if (value.ReleaseAtUnixMilliseconds > 0 && value.ReleaseAtUnixMilliseconds <= NowMs())
		{
			return false;
		}
		return true;
	}

	public long GetReleaseAt(string? playerUid)
	{
		if (string.IsNullOrWhiteSpace(playerUid))
		{
			return 0L;
		}
		JailSentence value;
		return (sentences.TryGetValue(playerUid, out value) && !value.ReleasePending) ? value.ReleaseAtUnixMilliseconds : 0;
	}

	public string SetJailPosition(IServerPlayer actor, string source = "ingame")
	{
		EntityPos entityPos = EntityRuntimeCompat.GetPos(actor.Entity)?.Copy();
		if (entityPos == null)
		{
			return "Unable to read your current position.";
		}
		jail = JailPosition.From(entityPos);
		Save();
		audit(actor.PlayerName, "setjail", actor.PlayerUID, actor.PlayerName, $"Set jail cell to {entityPos.X:0.##},{entityPos.Y:0.##},{entityPos.Z:0.##} dim {entityPos.Dimension}", source);
		return $"Jail position set to {entityPos.X:0.#}, {entityPos.Y:0.#}, {entityPos.Z:0.#} (dimension {entityPos.Dimension}).";
	}

	public string JailPlayer(string actor, string targetName, string duration, string reason = "", string source = "ingame")
	{
		if (jail == null)
		{
			return "No jail position is configured. Stand in the jail cell and use /setjail first.";
		}
		IServerPlayer serverPlayer = FindOnline(targetName);
		if (serverPlayer?.Entity == null)
		{
			return "Player must be online to begin a jail sentence so their return position can be saved.";
		}
		if (!AdminService.TryParseDuration(duration, out var milliseconds, out var permanent))
		{
			return "Invalid duration. Use values such as 10m, 2h, 3d, 1w, or permanent.";
		}
		NewfiesAfkModSystem current = NewfiesAfkModSystem.Current;
		if (current != null && current.IsAfk(serverPlayer.PlayerUID))
		{
			current.ToggleForPlayer(serverPlayer);
		}
		long num = NowMs();
		if (!sentences.TryGetValue(serverPlayer.PlayerUID, out JailSentence value) || value.ReleasePending)
		{
			EntityPos entityPos = EntityRuntimeCompat.GetPos(serverPlayer.Entity)?.Copy();
			if (entityPos == null)
			{
				return "Unable to read the player's current position; jail was not applied.";
			}
			value = new JailSentence
			{
				PlayerUid = serverPlayer.PlayerUID,
				LastKnownName = serverPlayer.PlayerName,
				ReturnPosition = JailPosition.From(entityPos),
				StartedAtUnixMilliseconds = num
			};
		}
		value.LastKnownName = serverPlayer.PlayerName;
		value.JailedBy = actor;
		value.Reason = (string.IsNullOrWhiteSpace(reason) ? "Jailed by staff" : reason.Trim());
		value.ReleaseAtUnixMilliseconds = (permanent ? 0 : (num + milliseconds));
		value.ReleasePending = false;
		sentences[serverPlayer.PlayerUID] = value;
		Save();
		TeleportToJail(serverPlayer, "suite-jail");
		string text = (permanent ? "permanently" : ("for " + AdminService.FormatDuration(milliseconds)));
		serverPlayer.SendMessage(GlobalConstants.GeneralChatGroup, "You have been jailed " + text + ". Reason: " + value.Reason, EnumChatType.Notification);
		audit(actor, "jail", serverPlayer.PlayerUID, serverPlayer.PlayerName, "Jailed " + text + ". Reason: " + value.Reason, source);
		return $"Jailed {serverPlayer.PlayerName} {text}.";
	}

	public string UnjailPlayer(string actor, string targetNameOrUid, string reason = "", string source = "ingame")
	{
		JailSentence jailSentence = ResolveSentence(targetNameOrUid);
		if (jailSentence == null)
		{
			return "That player is not jailed.";
		}
		string text = (string.IsNullOrWhiteSpace(reason) ? "Released by staff" : reason.Trim());
		IServerPlayer serverPlayer = FindOnline(jailSentence.PlayerUid);
		if (serverPlayer?.Entity != null)
		{
			ReleaseOnline(serverPlayer, jailSentence, text, actor, source);
			return "Released " + jailSentence.LastKnownName + " from jail.";
		}
		jailSentence.ReleasePending = true;
		jailSentence.ReleaseAtUnixMilliseconds = NowMs();
		Save();
		audit(actor, "unjail", jailSentence.PlayerUid, jailSentence.LastKnownName, "Released while offline; return teleport pending next login. Reason: " + text, source);
		return "Released " + jailSentence.LastKnownName + ". Their return teleport will occur when they next join.";
	}

	public string GetInfo(string targetNameOrUid)
	{
		JailSentence jailSentence = ResolveSentence(targetNameOrUid);
		if (jailSentence == null)
		{
			return "That player is not jailed.";
		}
		if (jailSentence.ReleasePending)
		{
			return jailSentence.LastKnownName + " is released; return teleport is pending next login.";
		}
		long num = ((jailSentence.ReleaseAtUnixMilliseconds <= 0) ? (-1) : Math.Max(0L, jailSentence.ReleaseAtUnixMilliseconds - NowMs()));
		string value = ((num < 0) ? "permanent" : AdminService.FormatDuration(num));
		return $"{jailSentence.LastKnownName} is jailed. Remaining: {value}. By: {jailSentence.JailedBy}. Reason: {jailSentence.Reason}";
	}

	private void OnTick(float dt)
	{
		if (disposed || sentences.Count == 0)
		{
			return;
		}
		long num = NowMs();
		JailSentence[] array = sentences.Values.ToArray();
		foreach (JailSentence jailSentence in array)
		{
			IServerPlayer serverPlayer = FindOnline(jailSentence.PlayerUid);
			if (jailSentence.ReleasePending || (jailSentence.ReleaseAtUnixMilliseconds > 0 && jailSentence.ReleaseAtUnixMilliseconds <= num))
			{
				if (serverPlayer?.Entity != null)
				{
					ReleaseOnline(serverPlayer, jailSentence, "Sentence expired", "system", "system");
				}
				else if (!jailSentence.ReleasePending)
				{
					jailSentence.ReleasePending = true;
					Save();
				}
			}
			else
			{
				if (serverPlayer?.Entity == null || jail == null)
				{
					continue;
				}
				EntityPos pos = EntityRuntimeCompat.GetPos(serverPlayer.Entity);
				if (pos != null)
				{
					bool flag = pos.Dimension != jail.Dimension;
					double num2 = pos.X - jail.X;
					double num3 = pos.Y - jail.Y;
					double num4 = pos.Z - jail.Z;
					if (flag || num2 * num2 + num3 * num3 + num4 * num4 > 9.0)
					{
						TeleportToJail(serverPlayer, "suite-jail-return");
					}
				}
			}
		}
	}

	private void OnPlayerNowPlaying(IServerPlayer player)
	{
		EnforceJoinState(player);
	}

	private void OnPlayerRespawn(IServerPlayer player)
	{
		if (!IsJailed(player.PlayerUID))
		{
			return;
		}
		api.Event.EnqueueMainThreadTask(() =>
		{
			if (IsJailed(player.PlayerUID))
			{
				TeleportToJail(player, "suite-jail-respawn");
			}
		}, "nse-jail-respawn");
	}

	private void EnforceJoinState(IServerPlayer player)
	{
		if (sentences.TryGetValue(player.PlayerUID, out JailSentence value))
		{
			if (value.ReleasePending || (value.ReleaseAtUnixMilliseconds > 0 && value.ReleaseAtUnixMilliseconds <= NowMs()))
			{
				ReleaseOnline(player, value, "Sentence expired while offline", "system", "system");
				return;
			}
			TeleportToJail(player, "suite-jail-login");
			string text = ((value.ReleaseAtUnixMilliseconds <= 0) ? "permanent" : AdminService.FormatDuration(Math.Max(0L, value.ReleaseAtUnixMilliseconds - NowMs())));
			player.SendMessage(GlobalConstants.GeneralChatGroup, "You are still jailed. Remaining: " + text + ". Reason: " + value.Reason, EnumChatType.Notification);
		}
	}

	private void ReleaseOnline(IServerPlayer player, JailSentence sentence, string reason, string actor, string source)
	{
		try
		{
			NewfiesAfkModSystem current = NewfiesAfkModSystem.Current;
			if (current != null && current.IsAfk(player.PlayerUID))
			{
				current.ToggleForPlayer(player);
			}
			PacketGuardTrustedState.Mark(player, "suite-unjail", 5000);
			player.Entity.TeleportTo(sentence.ReturnPosition.ToEntityPos());
			sentences.Remove(sentence.PlayerUid);
			Save();
			player.SendMessage(GlobalConstants.GeneralChatGroup, "You have been released from jail. " + reason, EnumChatType.Notification);
			audit(actor, "unjail", sentence.PlayerUid, player.PlayerName, "Released and returned to pre-jail position. Reason: " + reason, source);
		}
		catch (Exception ex)
		{
			sentence.ReleasePending = true;
			sentence.ReleaseAtUnixMilliseconds = NowMs();
			sentences[sentence.PlayerUid] = sentence;
			Save();
			api.Logger.Warning("[ServerEssentials/Jail] Return teleport failed for {0}; release remains pending for retry: {1}", player.PlayerName, ex.Message);
			player.SendMessage(GlobalConstants.GeneralChatGroup, "Your jail sentence is over, but the return teleport failed. Server Essentials will retry automatically.", EnumChatType.CommandError);
			audit(actor, "unjail-return-pending", sentence.PlayerUid, player.PlayerName, "Sentence released but return teleport failed; retry pending. Reason: " + reason, source);
		}
	}

	private void TeleportToJail(IServerPlayer player, string reason)
	{
		if (jail != null && player.Entity != null)
		{
			PacketGuardTrustedState.Mark(player, reason, 5000);
			player.Entity.TeleportTo(jail.ToEntityPos());
			EntityRuntimeCompat.GetPos(player.Entity)?.Motion.Set(0.0, 0.0, 0.0);
		}
	}

	private IServerPlayer? FindOnline(string playerNameOrUid)
	{
		string search = (playerNameOrUid ?? "").Trim();
		return api.World.AllOnlinePlayers.OfType<IServerPlayer>().FirstOrDefault((IServerPlayer p) => p.PlayerUID.Equals(search, StringComparison.OrdinalIgnoreCase) || p.PlayerName.Equals(search, StringComparison.OrdinalIgnoreCase));
	}

	private JailSentence? ResolveSentence(string playerNameOrUid)
	{
		string search = (playerNameOrUid ?? "").Trim();
		if (sentences.TryGetValue(search, out JailSentence value))
		{
			return value;
		}
		return sentences.Values.FirstOrDefault((JailSentence x) => x.LastKnownName.Equals(search, StringComparison.OrdinalIgnoreCase));
	}

	private void Load()
	{
		try
		{
			byte[] data = api.WorldManager.SaveGame.GetData("newfiesserveressentials:jail-v1");
			if (data == null || data.Length == 0)
			{
				return;
			}
			JailDatabase jailDatabase = JsonSerializer.Deserialize<JailDatabase>(data);
			jail = jailDatabase?.Jail;
			sentences.Clear();
			foreach (JailSentence item in jailDatabase?.Sentences ?? new List<JailSentence>())
			{
				if (item != null && !string.IsNullOrWhiteSpace(item.PlayerUid))
				{
					sentences[item.PlayerUid] = item;
				}
			}
		}
		catch (Exception ex)
		{
			api.Logger.Error("[ServerEssentials/Jail] Failed to load jail state; jail system will start empty for safety: {0}", ex);
			jail = null;
			sentences.Clear();
		}
	}

	private void Save()
	{
		try
		{
			JailDatabase value = new JailDatabase
			{
				Jail = jail,
				Sentences = sentences.Values.OrderBy((JailSentence x) => x.LastKnownName, StringComparer.OrdinalIgnoreCase).ToList()
			};
			api.WorldManager.SaveGame.StoreData("newfiesserveressentials:jail-v1", JsonSerializer.SerializeToUtf8Bytes(value));
		}
		catch (Exception ex)
		{
			api.Logger.Error("[ServerEssentials/Jail] Failed to save jail state: {0}", ex);
		}
	}

	private static long NowMs()
	{
		return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
	}

	public void Dispose()
	{
		if (!disposed)
		{
			disposed = true;
			Save();
			api.Event.PlayerNowPlaying -= OnPlayerNowPlaying;
			api.Event.PlayerRespawn -= OnPlayerRespawn;
			api.Event.GameWorldSave -= Save;
			if (tickListenerId >= 0)
			{
				api.Event.UnregisterGameTickListener(tickListenerId);
			}
			tickListenerId = -1L;
		}
	}
}
