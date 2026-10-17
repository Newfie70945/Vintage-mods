using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using NewfiesAntiCheat.Shared.Packets;

namespace NewfiesAntiCheat.Shared;

public static class HeartbeatHasher
{
	public const int CurrentBindingVersion = 1;

	public static string Compute(HeartbeatPacket heartbeat, byte[]? nonce)
	{
		if (heartbeat == null)
		{
			return "";
		}
		string s = string.Join("|", new string[17]
		{
			"heartbeat-v1",
			heartbeat.ProtocolVersion.ToString(CultureInfo.InvariantCulture),
			heartbeat.Sequence.ToString(CultureInfo.InvariantCulture),
			heartbeat.ChallengeId ?? "",
			Convert.ToHexString(nonce ?? Array.Empty<byte>()),
			heartbeat.ClientTimestampUtc.ToString(CultureInfo.InvariantCulture),
			heartbeat.ClientVersion ?? "",
			heartbeat.RuntimeGeneration.ToString(CultureInfo.InvariantCulture),
			heartbeat.LoadedAssemblyCount.ToString(CultureInfo.InvariantCulture),
			heartbeat.DynamicAssemblyCount.ToString(CultureInfo.InvariantCulture),
			heartbeat.RuntimeDigest ?? "",
			heartbeat.SelfIntegrityDigest ?? "",
			heartbeat.HarmonyPatchDigest ?? "",
			heartbeat.HarmonyPatchCount.ToString(CultureInfo.InvariantCulture),
			heartbeat.SuspiciousHarmonyOwnerCount.ToString(CultureInfo.InvariantCulture),
			heartbeat.SelfAttestationDigest ?? "",
			heartbeat.SelfAttestationProbeCount.ToString(CultureInfo.InvariantCulture)
		});
		using SHA256 sHA = SHA256.Create();
		return Convert.ToHexString(sHA.ComputeHash(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
	}
}
