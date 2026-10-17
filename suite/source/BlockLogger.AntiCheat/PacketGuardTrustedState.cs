using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Vintagestory.API.Server;

namespace BlockLogger.AntiCheat;

internal static class PacketGuardTrustedState
{
	private sealed class Grant
	{
		public long UntilUtc;

		public string Reason = "trusted-transition";
	}

	private sealed class TeleportSourceUsage
	{
		public long Count;

		public long LastSeenUtc;

		public string LastPlayerName = "";

		public string LastPlayerUid = "";

		public string LastReason = "";
	}

	internal readonly record struct TeleportSourceTelemetry(string Code, long Count, long LastSeenUtc, string LastPlayerName, string LastPlayerUid, string LastReason);

	internal readonly record struct TeleportEventTelemetry(string Code, long Utc, string PlayerName, string PlayerUid, double FromX, double FromY, double FromZ, int FromDimension, double ToX, double ToY, double ToZ, int ToDimension, string Reason, string Verification);

	private sealed class SuppressionUsage
	{
		public long Count;

		public long LastSeenUtc;

		public string LastPlayerName = "";

		public string LastPlayerUid = "";

		public string LastDetail = "";
	}

	internal readonly record struct SuppressionTelemetry(string Code, long Count, long LastSeenUtc, string LastPlayerName, string LastPlayerUid, string LastDetail);

	private static readonly ConcurrentDictionary<string, ConcurrentDictionary<PacketGuardTrustScope, Grant>> Entries = new ConcurrentDictionary<string, ConcurrentDictionary<PacketGuardTrustScope, Grant>>(StringComparer.OrdinalIgnoreCase);

	private const int MaxRecentTeleportEventsPerSource = 16;

	private static readonly ConcurrentDictionary<string, TeleportSourceUsage> TeleportSources = new ConcurrentDictionary<string, TeleportSourceUsage>(StringComparer.OrdinalIgnoreCase);

	private static readonly ExpiringDiagnosticDedupe TeleportSourceDedupe = new ExpiringDiagnosticDedupe();

	private static readonly ConcurrentDictionary<string, ConcurrentQueue<TeleportEventTelemetry>> TeleportEvents = new ConcurrentDictionary<string, ConcurrentQueue<TeleportEventTelemetry>>(StringComparer.OrdinalIgnoreCase);

	private static readonly ExpiringDiagnosticDedupe TeleportEventDedupe = new ExpiringDiagnosticDedupe();

	private static readonly ConcurrentDictionary<string, SuppressionUsage> Suppressions = new ConcurrentDictionary<string, SuppressionUsage>(StringComparer.OrdinalIgnoreCase);

	private static readonly ExpiringDiagnosticDedupe SuppressionDedupe = new ExpiringDiagnosticDedupe();

	public static void Mark(IServerPlayer? player, string reason, int milliseconds = 2500, PacketGuardTrustScope scopes = PacketGuardTrustScope.All)
	{
		if (player == null || string.IsNullOrWhiteSpace(player.PlayerUID) || scopes == PacketGuardTrustScope.None)
		{
			return;
		}
		long num = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		long until = num + Math.Clamp(milliseconds, 250, 30000);
		string cleanReason = CleanReason(reason);
		RecordTeleportSource(player, cleanReason, num);
		ConcurrentDictionary<PacketGuardTrustScope, Grant> orAdd = Entries.GetOrAdd(player.PlayerUID, (string _) => new ConcurrentDictionary<PacketGuardTrustScope, Grant>());
		PacketGuardTrustScope[] array = new PacketGuardTrustScope[2]
		{
			PacketGuardTrustScope.Movement,
			PacketGuardTrustScope.Reach
		};
		foreach (PacketGuardTrustScope packetGuardTrustScope in array)
		{
			if ((scopes & packetGuardTrustScope) == 0)
			{
				continue;
			}
			orAdd.AddOrUpdate(packetGuardTrustScope, (PacketGuardTrustScope _) => new Grant
			{
				UntilUtc = until,
				Reason = cleanReason
			}, (PacketGuardTrustScope _, Grant current) =>
			{
				if (until >= current.UntilUtc)
				{
					current.UntilUtc = until;
					current.Reason = cleanReason;
				}
				return current;
			});
		}
	}

	public static bool IsActive(string? playerUid, long nowUtc, PacketGuardTrustScope scope)
	{
		string reason;
		return TryGetActiveReason(playerUid, nowUtc, scope, out reason);
	}

	public static bool TryGetActiveReason(string? playerUid, long nowUtc, PacketGuardTrustScope scope, out string reason)
	{
		reason = "";
		if (string.IsNullOrWhiteSpace(playerUid) || scope == PacketGuardTrustScope.None)
		{
			return false;
		}
		if (!Entries.TryGetValue(playerUid, out ConcurrentDictionary<PacketGuardTrustScope, Grant> value))
		{
			return false;
		}
		if (!value.TryGetValue(scope, out var value2))
		{
			return false;
		}
		if (nowUtc <= value2.UntilUtc)
		{
			reason = value2.Reason;
			return true;
		}
		value.TryRemove(scope, out var _);
		if (value.IsEmpty)
		{
			Entries.TryRemove(playerUid, out ConcurrentDictionary<PacketGuardTrustScope, Grant> _);
		}
		return false;
	}

	public static void Clear(string? playerUid)
	{
		if (!string.IsNullOrWhiteSpace(playerUid))
		{
			Entries.TryRemove(playerUid, out ConcurrentDictionary<PacketGuardTrustScope, Grant> _);
			TeleportSourceDedupe.RemovePlayer(playerUid);
			TeleportEventDedupe.RemovePlayer(playerUid);
			SuppressionDedupe.RemovePlayer(playerUid);
		}
	}

	public static void ClearAll()
	{
		Entries.Clear();
		TeleportSourceDedupe.Clear();
		TeleportEventDedupe.Clear();
		SuppressionDedupe.Clear();
		TeleportSources.Clear();
		TeleportEvents.Clear();
		Suppressions.Clear();
	}

	public static IReadOnlyList<TeleportSourceTelemetry> GetTeleportSourceTelemetry()
	{
		return (from x in TeleportSources.Select((KeyValuePair<string, TeleportSourceUsage> pair) =>
			{
				TeleportSourceUsage value = pair.Value;
				return new TeleportSourceTelemetry(pair.Key, Interlocked.Read(in value.Count), Interlocked.Read(in value.LastSeenUtc), value.LastPlayerName, value.LastPlayerUid, value.LastReason);
			})
			orderby x.LastSeenUtc descending
			select x).ThenBy((TeleportSourceTelemetry x) => x.Code, StringComparer.OrdinalIgnoreCase).ToList();
	}

	public static IReadOnlyList<TeleportEventTelemetry> GetRecentTeleportEvents(string? sourceCode)
	{
		if (string.IsNullOrWhiteSpace(sourceCode) || !TeleportEvents.TryGetValue(sourceCode, out ConcurrentQueue<TeleportEventTelemetry> value))
		{
			return Array.Empty<TeleportEventTelemetry>();
		}
		return (from x in value.ToArray()
			orderby x.Utc descending
			select x).Take(16).ToList();
	}

	public static void RecordTeleportEvent(IServerPlayer? player, string reason, double fromX, double fromY, double fromZ, int fromDimension, double toX, double toY, double toZ, int toDimension, string verification, long nowUtc = 0L)
	{
		if (player == null || string.IsNullOrWhiteSpace(player.PlayerUID))
		{
			return;
		}
		string reason2 = CleanReason(reason);
		string text = CanonicalTeleportSource(reason2);
		if (string.IsNullOrWhiteSpace(text))
		{
			return;
		}
		if (nowUtc <= 0)
		{
			nowUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		}
		if (!TeleportEventDedupe.IsDuplicate(text, player.PlayerUID, nowUtc, 1750))
		{
			TeleportEventTelemetry item = new TeleportEventTelemetry(text, nowUtc, player.PlayerName ?? "", player.PlayerUID ?? "", fromX, fromY, fromZ, fromDimension, toX, toY, toZ, toDimension, reason2, CleanDiagnosticText(verification, 180));
			ConcurrentQueue<TeleportEventTelemetry> orAdd = TeleportEvents.GetOrAdd(text, (string _) => new ConcurrentQueue<TeleportEventTelemetry>());
			orAdd.Enqueue(item);
			TeleportEventTelemetry result;
			while (orAdd.Count > 16 && orAdd.TryDequeue(out result))
			{
			}
		}
	}

	public static void RecordSuppression(IServerPlayer? player, string code, string detail, int dedupeMs = 750, long nowUtc = 0L)
	{
		if (player == null || string.IsNullOrWhiteSpace(player.PlayerUID) || string.IsNullOrWhiteSpace(code))
		{
			return;
		}
		if (nowUtc <= 0)
		{
			nowUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		}
		string text = code.Trim().ToLowerInvariant();
		if (!SuppressionDedupe.IsDuplicate(text, player.PlayerUID, nowUtc, Math.Clamp(dedupeMs, 100, 10000)))
		{
			SuppressionUsage orAdd = Suppressions.GetOrAdd(text, (string _) => new SuppressionUsage());
			Interlocked.Increment(ref orAdd.Count);
			Interlocked.Exchange(ref orAdd.LastSeenUtc, nowUtc);
			orAdd.LastPlayerName = player.PlayerName ?? "";
			orAdd.LastPlayerUid = player.PlayerUID ?? "";
			orAdd.LastDetail = CleanDiagnosticText(detail, 180);
		}
	}

	public static IReadOnlyList<SuppressionTelemetry> GetSuppressionTelemetry()
	{
		return (from x in Suppressions.Select((KeyValuePair<string, SuppressionUsage> pair) =>
			{
				SuppressionUsage value = pair.Value;
				return new SuppressionTelemetry(pair.Key, Interlocked.Read(in value.Count), Interlocked.Read(in value.LastSeenUtc), value.LastPlayerName, value.LastPlayerUid, value.LastDetail);
			})
			orderby x.Count descending
			select x).ThenBy((SuppressionTelemetry x) => x.Code, StringComparer.OrdinalIgnoreCase).ToList();
	}

	private static void RecordTeleportSource(IServerPlayer player, string reason, long nowUtc)
	{
		string text = CanonicalTeleportSource(reason);
		if (string.IsNullOrWhiteSpace(text))
		{
			return;
		}
		if (TeleportSourceDedupe.IsDuplicate(text, player.PlayerUID, nowUtc, 1750))
		{
			TeleportSourceUsage orAdd = TeleportSources.GetOrAdd(text, (string _) => new TeleportSourceUsage());
			Interlocked.Exchange(ref orAdd.LastSeenUtc, nowUtc);
			orAdd.LastPlayerName = player.PlayerName ?? "";
			orAdd.LastPlayerUid = player.PlayerUID ?? "";
			orAdd.LastReason = reason;
			return;
		}
		TeleportSourceUsage orAdd2 = TeleportSources.GetOrAdd(text, (string _) => new TeleportSourceUsage());
		Interlocked.Increment(ref orAdd2.Count);
		Interlocked.Exchange(ref orAdd2.LastSeenUtc, nowUtc);
		orAdd2.LastPlayerName = player.PlayerName ?? "";
		orAdd2.LastPlayerUid = player.PlayerUID ?? "";
		orAdd2.LastReason = reason;
	}

	private static string CanonicalTeleportSource(string reason)
	{
		string text = reason.ToLowerInvariant();
		if (1 == 0)
		{
		}
		string result;
		switch (text)
		{
		case "server-teleport-call":
		case "engine-teleport":
			result = "server-engine-teleport";
			break;
		case "vanilla-translocator-linked-direct":
		case "vanilla-translocator-linked-target":
		case "vanilla-translocator-preauthorized":
		case "vanilla-translocator":
			result = "vanilla-translocator";
			break;
		case "suite-map-teleport":
		case "suite-admin-teleport":
			result = "suite-admin-teleport";
			break;
		case "dashboard-teleport":
			result = "dashboard-teleport";
			break;
		case "admin-teleport-command":
			result = "admin-teleport-command";
			break;
		case "th3essentials-spawn":
			result = "th3essentials-spawn";
			break;
		case "tp-runes":
			result = "tp-runes";
			break;
		case "spawn":
			result = "spawn-arrival";
			break;
		case "dimension-change":
			result = "dimension-change";
			break;
		default:
			result = "";
			break;
		}
		if (1 == 0)
		{
		}
		return result;
	}

	internal static string CleanDiagnosticText(string? value, int maxLength)
	{
		string text = (value ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
		return (text.Length <= maxLength) ? text : text.Substring(0, maxLength);
	}

	private static string CleanReason(string? reason)
	{
		string text = (reason ?? "trusted-transition").Trim();
		return (text.Length == 0) ? "trusted-transition" : text.Substring(0, Math.Min(text.Length, 80));
	}
}
