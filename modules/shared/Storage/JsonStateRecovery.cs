using System.Security.Cryptography;
using System.Text.Json;

namespace Newfies.Storage;

/// <summary>Bounded startup reads and verified, deduplicated preservation of damaged state.</summary>
internal static class JsonStateRecovery
{
    public const long MaxStateBytes = 64 * 1024 * 1024;
    public static T Read<T>(string path, JsonSerializerOptions options) where T : class
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaxStateBytes) throw new InvalidDataException("State file exceeds the 64 MiB safety limit; restore or archive it before retrying.");
        return JsonSerializer.Deserialize<T>(stream, options) ?? throw new InvalidDataException("State file contains null.");
    }

    public static string Preserve(string path)
    {
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (source.Length > MaxStateBytes) throw new InvalidDataException("Oversized state file was left untouched; writes remain disabled.");
        string hash = Convert.ToHexString(SHA256.HashData(source)).ToLowerInvariant();
        string root = Path.Combine(Path.GetDirectoryName(path)!, "recovery");
        Directory.CreateDirectory(root);
        string copy = Path.Combine(root, Path.GetFileName(path) + "." + hash + ".preserved");
        if (!File.Exists(copy))
        {
            source.Position = 0;
            using var target = new FileStream(copy, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            source.CopyTo(target);
            target.Flush(true);
        }
        using var check = File.OpenRead(copy);
        if (!Convert.ToHexString(SHA256.HashData(check)).Equals(hash, StringComparison.OrdinalIgnoreCase)) throw new IOException("Recovery copy did not verify; original state was retained.");
        return copy;
    }
}
