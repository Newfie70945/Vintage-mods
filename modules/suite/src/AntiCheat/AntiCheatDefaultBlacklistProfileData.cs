using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace BlockLogger.AntiCheat;

/// <summary>
/// Exact built-in blacklist baseline supplied by Newfie.
/// The payload contains the original ForbiddenModIds and all supplied ForbiddenFingerprints,
/// including package/content/assembly hashes, release history, structural signatures and manual entries.
/// </summary>
internal static class AntiCheatDefaultBlacklistProfileData
{
    public const string ProfileId = "newfies-default-unfair-mods";
    public const string ProfileName = "Newfie's Unfair Mods";
    public const string SourceConfigSha256 = "da06fee1516e0ebdcfdfdf6c1d332a1e2084e2165a7a05f2cd9f520fbf752931";

    private static readonly string CompressedPayloadBase64 =
        "H4sIAAAAAAAC/+y9a5Odx3Wl+Vcm8PmIkfeLv8lyq7tnmrZCsj0R0+FQ5FXCiAAYACi37Oj/3uvJokiQOFUEiQJFSMekRaDqnPfNy8691t65L//55NMX87/P" +
        "V0/+7n8++fzli1efP33+uyeXJ//rZftT/+zF+IP+fP776sUf18vP2p/09/ZsP30+10v98cXL9e+/f/rq8/OX8/U1XusJr0Z7rp/87umz9jlPePpyvlp/Wvrj" +
        "qy/668/W5+3l66fjs/Xq88++eKWfPt/+9euX7fkrvaq9fvHy7gXj2dTv+HN7/tVonr2Yz9bzLxjHF69ftNev2/nx0Nj2i5fP1svPX65X4w8v+ObLtV6+aM9G" +
        "e3a+2L8a96tnT1//XuN81sbLF3P1L5jzq9fr8y8+f/3vL+4mrJ/8x9Pnf9D7eBHP/+Lz2V6vJ/92efJLfZc3PX3+mpX7z7tFfPJ3b4zuNy++eDmWfvRpe/5F" +
        "+0w/+fV69eKzP675r+vlq6cvnutXb/zwl08/W//Ynq1v/vRfXo8nf2dzKSn6bHIx4fLkV5pw+936ze+bi+nu47948fz1ev76zR/9/NWr9ax/9ieeyRD/7esf" +
        "3X3sWz/89I9P57c/9/rlF+P1Fy/X1S/8/fp9++PTFy+/8Ustw2/+pHV89s9/+ny98epfr89We/Xnv92tyH95/vrl03W3fGftdl3ehlHSMDW0bWoKZvhu1uxr" +
        "7o4s/I/W12es6Yv56d0yf72Y9hP3iX3C3ny1kl9/7B9fvOZVTz79+f/9T7/+v/7bz3/x/zx5eyXrrnmYuLwfY7RufOh5tbhKS80103oxY6TVp88h2tRKciXr" +
        "/1c3odSSruxEX67uGVvdo+XunE9lTqMnTptW3T2aPvPabecx8ljJb1vbKilH41aI6cm1bXsyy9Iz6qi+tdW3zXbpkWunXIJ12Q9rei29BrtDmS7k1LJ1cbZa" +
        "Y7DNPbmy8U9cLyPmFn/WY94/C724n5We9s+y/j5rS6bb+uRh8XjUgV0VsF+8XDqC3z4XufrLk385p/PKr/73v/3vy9cn9Jqi+Pqsvpj/8PdXj6r9xH9irp/X" +
        "r5/4yX88/fy+0+tqSFdOb9xhurGmNa6b6JwdKW1ns292FtP23ivWNn21WlKXcw8t2OB6njO7GdcVmas7S6BCqyGvWrT8ozsJ2l5G+6mtiauHFI1k0tgRUw96" +
        "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";


    public static AntiCheatBlacklistProfile CreateProfile()
    {
        var payload = ReadPayload();
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return new AntiCheatBlacklistProfile
        {
            Id = ProfileId,
            Name = ProfileName,
            ModIds = payload.ModIds ?? new(),
            Fingerprints = payload.Fingerprints ?? new(),
            CreatedUtc = now,
            UpdatedUtc = now
        };
    }

    public static System.Collections.Generic.List<AntiCheatForbiddenFingerprint> CloneFingerprints(System.Collections.Generic.IEnumerable<AntiCheatForbiddenFingerprint> source)
    {
        string json = JsonSerializer.Serialize(source ?? Array.Empty<AntiCheatForbiddenFingerprint>());
        return JsonSerializer.Deserialize<System.Collections.Generic.List<AntiCheatForbiddenFingerprint>>(json) ?? new();
    }

    private static Payload ReadPayload()
    {
        byte[] compressed = Convert.FromBase64String(CompressedPayloadBase64);
        using var input = new MemoryStream(compressed);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        string json = reader.ReadToEnd();
        return JsonSerializer.Deserialize<Payload>(json) ?? new Payload();
    }

    private sealed class Payload
    {
        public System.Collections.Generic.List<string> ModIds { get; set; } = new();
        public System.Collections.Generic.List<AntiCheatForbiddenFingerprint> Fingerprints { get; set; } = new();
    }
}
