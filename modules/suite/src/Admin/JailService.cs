using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using BlockLogger.AntiCheat;
using BlockLogger.Afk;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace BlockLogger.Admin;

public sealed class JailPosition
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public int Dimension { get; set; }
    public float Yaw { get; set; }
    public float Pitch { get; set; }

    public EntityPos ToEntityPos()
    {
        var pos = new EntityPos(X, Y, Z, Yaw, Pitch);
        pos.Dimension = Dimension;
        return pos;
    }

    public static JailPosition From(EntityPos pos) => new()
    {
        X = pos.X,
        Y = pos.Y,
        Z = pos.Z,
        Dimension = pos.Dimension,
        Yaw = pos.Yaw,
        Pitch = pos.Pitch
    };
}

public sealed class JailSentence
{
    public string PlayerUid { get; set; } = "";
    public string LastKnownName { get; set; } = "";
    public string JailedBy { get; set; } = "";
    public string Reason { get; set; } = "";
    public long StartedAtUnixMilliseconds { get; set; }
    public long ReleaseAtUnixMilliseconds { get; set; }
    public bool ReleasePending { get; set; }
    public JailPosition ReturnPosition { get; set; } = new();
}

public sealed class JailDatabase
{
    public int SchemaVersion { get; set; } = 1;
    public JailPosition? Jail { get; set; }
    public List<JailSentence> Sentences { get; set; } = new();
}

/// <summary>
/// Server-authoritative jail enforcement. The cell and active sentences are world data,
/// not global ModConfig, because both positions and prisoners belong to one savegame.
/// </summary>
public sealed class JailService : IDisposable
{
    private const string SaveKey = "newfiesserveressentials:jail-v1";
    private const double EscapeRadius = 3.0;
    private readonly ICoreServerAPI api;
    private readonly Action<string, string, string, string, string, string> audit;
    private readonly Dictionary<string, JailSentence> sentences = new(StringComparer.OrdinalIgnoreCase);
    private JailPosition? jail;
    private long tickListenerId = -1;
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

        foreach (var player in api.World.AllOnlinePlayers.OfType<IServerPlayer>())
            EnforceJoinState(player);

        api.Logger.Notification(
            "[ServerEssentials/Jail] Started. jailConfigured={0}, activeOrPending={1}.",
            jail != null,
            sentences.Count);
    }

    public bool IsJailed(string? playerUid)
    {
        if (string.IsNullOrWhiteSpace(playerUid)) return false;
        if (!sentences.TryGetValue(playerUid, out var sentence) || sentence.ReleasePending) return false;
        if (sentence.ReleaseAtUnixMilliseconds > 0 && sentence.ReleaseAtUnixMilliseconds <= NowMs()) return false;
        return true;
    }

    public long GetReleaseAt(string? playerUid)
    {
        if (string.IsNullOrWhiteSpace(playerUid)) return 0;
        return sentences.TryGetValue(playerUid, out var sentence) && !sentence.ReleasePending
            ? sentence.ReleaseAtUnixMilliseconds
            : 0;
    }

    public string SetJailPosition(IServerPlayer actor, string source = "ingame")
    {
        var pos = EntityRuntimeCompat.GetPos(actor.Entity)?.Copy();
        if (pos == null) return "Unable to read your current position.";
        jail = JailPosition.From(pos);
        Save();
        audit(actor.PlayerName, "setjail", actor.PlayerUID, actor.PlayerName,
            $"Set jail cell to {pos.X:0.##},{pos.Y:0.##},{pos.Z:0.##} dim {pos.Dimension}", source);
        return $"Jail position set to {pos.X:0.#}, {pos.Y:0.#}, {pos.Z:0.#} (dimension {pos.Dimension}).";
    }

    public string JailPlayer(string actor, string targetName, string duration, string reason = "", string source = "ingame")
    {
        if (jail == null) return "No jail position is configured. Stand in the jail cell and use /setjail first.";
        var target = FindOnline(targetName);
        if (target?.Entity == null) return "Player must be online to begin a jail sentence so their return position can be saved.";
        if (!AdminService.TryParseDuration(duration, out long durationMs, out bool permanent))
            return "Invalid duration. Use values such as 10m, 2h, 3d, 1w, or permanent.";

        // AFK locks a player to its own saved position. Disable it before taking the
        // pre-jail return position so AFK and Jail never fight over the entity transform.
        var afk = NewfiesAfkModSystem.Current;
        if (afk?.IsAfk(target.PlayerUID) == true)
            afk.ToggleForPlayer(target);

        long now = NowMs();
        if (!sentences.TryGetValue(target.PlayerUID, out var sentence) || sentence.ReleasePending)
        {
            var origin = EntityRuntimeCompat.GetPos(target.Entity)?.Copy();
            if (origin == null) return "Unable to read the player's current position; jail was not applied.";
            sentence = new JailSentence
            {
                PlayerUid = target.PlayerUID,
                LastKnownName = target.PlayerName,
                ReturnPosition = JailPosition.From(origin),
                StartedAtUnixMilliseconds = now
            };
        }

        sentence.LastKnownName = target.PlayerName;
        sentence.JailedBy = actor;
        sentence.Reason = string.IsNullOrWhiteSpace(reason) ? "Jailed by staff" : reason.Trim();
        sentence.ReleaseAtUnixMilliseconds = permanent ? 0 : now + durationMs;
        sentence.ReleasePending = false;
        sentences[target.PlayerUID] = sentence;
        Save();

        TeleportToJail(target, "suite-jail");
        string timeText = permanent ? "permanently" : $"for {AdminService.FormatDuration(durationMs)}";
        target.SendMessage(GlobalConstants.GeneralChatGroup,
            $"You have been jailed {timeText}. Reason: {sentence.Reason}", EnumChatType.Notification);
        audit(actor, "jail", target.PlayerUID, target.PlayerName,
            $"Jailed {timeText}. Reason: {sentence.Reason}", source);
        return $"Jailed {target.PlayerName} {timeText}.";
    }

    public string UnjailPlayer(string actor, string targetNameOrUid, string reason = "", string source = "ingame")
    {
        var sentence = ResolveSentence(targetNameOrUid);
        if (sentence == null) return "That player is not jailed.";
        string detailReason = string.IsNullOrWhiteSpace(reason) ? "Released by staff" : reason.Trim();
        var online = FindOnline(sentence.PlayerUid);
        if (online?.Entity != null)
        {
            ReleaseOnline(online, sentence, detailReason, actor, source);
            return $"Released {sentence.LastKnownName} from jail.";
        }

        sentence.ReleasePending = true;
        sentence.ReleaseAtUnixMilliseconds = NowMs();
        Save();
        audit(actor, "unjail", sentence.PlayerUid, sentence.LastKnownName,
            $"Released while offline; return teleport pending next login. Reason: {detailReason}", source);
        return $"Released {sentence.LastKnownName}. Their return teleport will occur when they next join.";
    }

    public string GetInfo(string targetNameOrUid)
    {
        var sentence = ResolveSentence(targetNameOrUid);
        if (sentence == null) return "That player is not jailed.";
        if (sentence.ReleasePending) return $"{sentence.LastKnownName} is released; return teleport is pending next login.";
        long remaining = sentence.ReleaseAtUnixMilliseconds <= 0 ? -1 : Math.Max(0, sentence.ReleaseAtUnixMilliseconds - NowMs());
        string duration = remaining < 0 ? "permanent" : AdminService.FormatDuration(remaining);
        return $"{sentence.LastKnownName} is jailed. Remaining: {duration}. By: {sentence.JailedBy}. Reason: {sentence.Reason}";
    }

    private void OnTick(float dt)
    {
        if (disposed || sentences.Count == 0) return;
        long now = NowMs();
        foreach (var sentence in sentences.Values.ToArray())
        {
            var player = FindOnline(sentence.PlayerUid);
            if (sentence.ReleasePending || (sentence.ReleaseAtUnixMilliseconds > 0 && sentence.ReleaseAtUnixMilliseconds <= now))
            {
                if (player?.Entity != null)
                    ReleaseOnline(player, sentence, "Sentence expired", "system", "system");
                else if (!sentence.ReleasePending)
                {
                    sentence.ReleasePending = true;
                    Save();
                }
                continue;
            }

            if (player?.Entity == null || jail == null) continue;
            var pos = EntityRuntimeCompat.GetPos(player.Entity);
            if (pos == null) continue;
            bool wrongDimension = pos.Dimension != jail.Dimension;
            double dx = pos.X - jail.X;
            double dy = pos.Y - jail.Y;
            double dz = pos.Z - jail.Z;
            if (wrongDimension || dx * dx + dy * dy + dz * dz > EscapeRadius * EscapeRadius)
                TeleportToJail(player, "suite-jail-return");
        }
    }

    private void OnPlayerNowPlaying(IServerPlayer player) => EnforceJoinState(player);

    private void OnPlayerRespawn(IServerPlayer player)
    {
        if (!IsJailed(player.PlayerUID)) return;
        api.Event.EnqueueMainThreadTask(() =>
        {
            if (IsJailed(player.PlayerUID)) TeleportToJail(player, "suite-jail-respawn");
        }, "nse-jail-respawn");
    }

    private void EnforceJoinState(IServerPlayer player)
    {
        if (!sentences.TryGetValue(player.PlayerUID, out var sentence)) return;
        if (sentence.ReleasePending || (sentence.ReleaseAtUnixMilliseconds > 0 && sentence.ReleaseAtUnixMilliseconds <= NowMs()))
        {
            ReleaseOnline(player, sentence, "Sentence expired while offline", "system", "system");
            return;
        }
        TeleportToJail(player, "suite-jail-login");
        string remaining = sentence.ReleaseAtUnixMilliseconds <= 0
            ? "permanent"
            : AdminService.FormatDuration(Math.Max(0, sentence.ReleaseAtUnixMilliseconds - NowMs()));
        player.SendMessage(GlobalConstants.GeneralChatGroup,
            $"You are still jailed. Remaining: {remaining}. Reason: {sentence.Reason}", EnumChatType.Notification);
    }

    private void ReleaseOnline(IServerPlayer player, JailSentence sentence, string reason, string actor, string source)
    {
        try
        {
            var afk = NewfiesAfkModSystem.Current;
            if (afk?.IsAfk(player.PlayerUID) == true)
                afk.ToggleForPlayer(player);

            PacketGuardTrustedState.Mark(player, "suite-unjail", 5_000, PacketGuardTrustScope.All);
            player.Entity.TeleportTo(sentence.ReturnPosition.ToEntityPos());
            sentences.Remove(sentence.PlayerUid);
            Save();
            player.SendMessage(GlobalConstants.GeneralChatGroup,
                $"You have been released from jail. {reason}", EnumChatType.Notification);
            audit(actor, "unjail", sentence.PlayerUid, player.PlayerName,
                $"Released and returned to pre-jail position. Reason: {reason}", source);
        }
        catch (Exception ex)
        {
            sentence.ReleasePending = true;
            sentence.ReleaseAtUnixMilliseconds = NowMs();
            sentences[sentence.PlayerUid] = sentence;
            Save();
            api.Logger.Warning("[ServerEssentials/Jail] Return teleport failed for {0}; release remains pending for retry: {1}", player.PlayerName, ex.Message);
            player.SendMessage(GlobalConstants.GeneralChatGroup,
                "Your jail sentence is over, but the return teleport failed. Server Essentials will retry automatically.", EnumChatType.CommandError);
            audit(actor, "unjail-return-pending", sentence.PlayerUid, player.PlayerName,
                $"Sentence released but return teleport failed; retry pending. Reason: {reason}", source);
        }
    }

    private void TeleportToJail(IServerPlayer player, string reason)
    {
        if (jail == null || player.Entity == null) return;
        PacketGuardTrustedState.Mark(player, reason, 5_000, PacketGuardTrustScope.All);
        player.Entity.TeleportTo(jail.ToEntityPos());
        var pos = EntityRuntimeCompat.GetPos(player.Entity);
        if (pos != null) pos.Motion.Set(0, 0, 0);
    }

    private IServerPlayer? FindOnline(string playerNameOrUid)
    {
        string search = (playerNameOrUid ?? "").Trim();
        return api.World.AllOnlinePlayers.OfType<IServerPlayer>().FirstOrDefault(p =>
            p.PlayerUID.Equals(search, StringComparison.OrdinalIgnoreCase) ||
            p.PlayerName.Equals(search, StringComparison.OrdinalIgnoreCase));
    }

    private JailSentence? ResolveSentence(string playerNameOrUid)
    {
        string search = (playerNameOrUid ?? "").Trim();
        if (sentences.TryGetValue(search, out var direct)) return direct;
        return sentences.Values.FirstOrDefault(x => x.LastKnownName.Equals(search, StringComparison.OrdinalIgnoreCase));
    }

    private void Load()
    {
        try
        {
            byte[]? bytes = api.WorldManager.SaveGame.GetData(SaveKey);
            if (bytes == null || bytes.Length == 0) return;
            var db = JsonSerializer.Deserialize<JailDatabase>(bytes);
            jail = db?.Jail;
            sentences.Clear();
            foreach (var sentence in db?.Sentences ?? new List<JailSentence>())
            {
                if (sentence == null || string.IsNullOrWhiteSpace(sentence.PlayerUid)) continue;
                sentences[sentence.PlayerUid] = sentence;
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
            var db = new JailDatabase
            {
                Jail = jail,
                Sentences = sentences.Values.OrderBy(x => x.LastKnownName, StringComparer.OrdinalIgnoreCase).ToList()
            };
            api.WorldManager.SaveGame.StoreData(SaveKey, JsonSerializer.SerializeToUtf8Bytes(db));
        }
        catch (Exception ex)
        {
            api.Logger.Error("[ServerEssentials/Jail] Failed to save jail state: {0}", ex);
        }
    }

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Save();
        api.Event.PlayerNowPlaying -= OnPlayerNowPlaying;
        api.Event.PlayerRespawn -= OnPlayerRespawn;
        api.Event.GameWorldSave -= Save;
        if (tickListenerId >= 0) api.Event.UnregisterGameTickListener(tickListenerId);
        tickListenerId = -1;
    }
}
