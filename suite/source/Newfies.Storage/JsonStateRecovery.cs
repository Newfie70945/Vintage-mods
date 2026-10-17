using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace Newfies.Storage;

internal static class JsonStateRecovery
{
	public const long MaxStateBytes = 67108864L;

	public static T Read<T>(string path, JsonSerializerOptions options) where T : class
	{
		using FileStream fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
		if (fileStream.Length > 67108864)
		{
			throw new InvalidDataException("State file exceeds the 64 MiB safety limit; restore or archive it before retrying.");
		}
		return JsonSerializer.Deserialize<T>(fileStream, options) ?? throw new InvalidDataException("State file contains null.");
	}

	public static string Preserve(string path)
	{
		using FileStream fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
		if (fileStream.Length > 67108864)
		{
			throw new InvalidDataException("Oversized state file was left untouched; writes remain disabled.");
		}
		string text = Convert.ToHexString(SHA256.HashData(fileStream)).ToLowerInvariant();
		string text2 = Path.Combine(Path.GetDirectoryName(path), "recovery");
		Directory.CreateDirectory(text2);
		string text3 = Path.Combine(text2, Path.GetFileName(path) + "." + text + ".preserved");
		if (!File.Exists(text3))
		{
			fileStream.Position = 0L;
			using FileStream fileStream2 = new FileStream(text3, FileMode.CreateNew, FileAccess.Write, FileShare.None);
			fileStream.CopyTo(fileStream2);
			fileStream2.Flush(flushToDisk: true);
		}
		using FileStream source = File.OpenRead(text3);
		if (!Convert.ToHexString(SHA256.HashData(source)).Equals(text, StringComparison.OrdinalIgnoreCase))
		{
			throw new IOException("Recovery copy did not verify; original state was retained.");
		}
		return text3;
	}
}
