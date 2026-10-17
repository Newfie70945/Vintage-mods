using System;
using System.Security.Cryptography;
using System.Text;
using NewfiesAntiCheat.Shared.Packets;

namespace NewfiesAntiCheat.Shared;

public static class HeartbeatHasher
{
    public const int CurrentBindingVersion = 1;

    public static string Compute(HeartbeatPacket heartbeat, byte[]? nonce)
    {
        if (heartbeat == null) return "";
        string raw = string.Join("|", new[]
        {
            "heartbeat-v1",
            heartbeat.ProtocolVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            heartbeat.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
            heartbeat.ChallengeId ?? "",
            Convert.ToHexString(nonce ?? Array.Empty<byte>()),
            heartbeat.ClientTimestampUtc.ToString(System.Globalization.CultureInfo.InvariantCulture),
            heartbeat.ClientVersion ?? "",
            heartbeat.RuntimeGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture),
            heartbeat.LoadedAssemblyCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            heartbeat.DynamicAssemblyCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            heartbeat.RuntimeDigest ?? "",
            heartbeat.SelfIntegrityDigest ?? "",
            heartbeat.HarmonyPatchDigest ?? "",
            heartbeat.HarmonyPatchCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            heartbeat.SuspiciousHarmonyOwnerCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            heartbeat.SelfAttestationDigest ?? "",
            heartbeat.SelfAttestationProbeCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
        });
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
    }
}
