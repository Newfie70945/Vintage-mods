using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace BlockLogger.AntiCheat;

internal static class AntiCheatLocalModHasher
{
	private const long MaxEntryBytes = 67108864L;

	private const long MaxTotalBytes = 536870912L;

	public static (long PackageBytes, int DllCount, long DllBytes, List<AntiCheatCanonicalDllFingerprint> Assemblies) GetPackageStats(string? path)
	{
		List<AntiCheatCanonicalDllFingerprint> list = new List<AntiCheatCanonicalDllFingerprint>();
		if (string.IsNullOrWhiteSpace(path))
		{
			return (PackageBytes: 0L, DllCount: 0, DllBytes: 0L, Assemblies: list);
		}
		try
		{
			if (File.Exists(path))
			{
				long length = new FileInfo(path).Length;
				if (Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase))
				{
					using (FileStream stream = File.OpenRead(path))
					{
						using ZipArchive zipArchive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
						long num = 0L;
						foreach (ZipArchiveEntry item in zipArchive.Entries.Where((ZipArchiveEntry e) => !string.IsNullOrEmpty(e.Name) && e.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).OrderBy((ZipArchiveEntry e) => e.FullName, StringComparer.Ordinal).Take(128))
						{
							if (item.Length >= 0 && item.Length <= 67108864)
							{
								num += item.Length;
								if (num > 536870912)
								{
									break;
								}
								using Stream stream2 = item.Open();
								list.Add(new AntiCheatCanonicalDllFingerprint
								{
									FileName = Path.GetFileName(item.Name),
									FileSizeBytes = item.Length,
									Sha256 = HashStream(stream2)
								});
							}
						}
						return (PackageBytes: length, DllCount: list.Count, DllBytes: num, Assemblies: list);
					}
				}
				bool flag = Path.GetExtension(path).Equals(".dll", StringComparison.OrdinalIgnoreCase);
				if (flag)
				{
					list.Add(new AntiCheatCanonicalDllFingerprint
					{
						FileName = Path.GetFileName(path),
						FileSizeBytes = length,
						Sha256 = HashFile(path)
					});
				}
				return (PackageBytes: length, DllCount: list.Count, DllBytes: flag ? length : 0, Assemblies: list);
			}
			if (Directory.Exists(path))
			{
				long num2 = 0L;
				long num3 = 0L;
				foreach (string item2 in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).OrderBy((string x) => x, StringComparer.OrdinalIgnoreCase).Take(4096))
				{
					long length2;
					try
					{
						length2 = new FileInfo(item2).Length;
					}
					catch
					{
						continue;
					}
					if (length2 >= 0 && length2 <= 67108864)
					{
						num2 += length2;
						if (Path.GetExtension(item2).Equals(".dll", StringComparison.OrdinalIgnoreCase) && list.Count < 128)
						{
							num3 += length2;
							list.Add(new AntiCheatCanonicalDllFingerprint
							{
								FileName = Path.GetFileName(item2),
								FileSizeBytes = length2,
								Sha256 = HashFile(item2)
							});
						}
						if (num2 > 536870912)
						{
							break;
						}
					}
				}
				return (PackageBytes: num2, DllCount: list.Count, DllBytes: num3, Assemblies: list);
			}
		}
		catch
		{
		}
		return (PackageBytes: 0L, DllCount: 0, DllBytes: 0L, Assemblies: list);
	}

	public static string HashFileSha256(string? path)
	{
		if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
		{
			return "";
		}
		try
		{
			return HashFile(path);
		}
		catch
		{
			return "";
		}
	}

	public static string HashPackage(string? path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return "";
		}
		try
		{
			if (File.Exists(path))
			{
				return HashFile(path);
			}
			if (!Directory.Exists(path))
			{
				return "";
			}
			using SHA256 sHA = SHA256.Create();
			foreach (string item in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).OrderBy((string x) => x, StringComparer.OrdinalIgnoreCase))
			{
				byte[] bytes = Encoding.UTF8.GetBytes(Path.GetRelativePath(path, item).Replace('\\', '/'));
				sHA.TransformBlock(bytes, 0, bytes.Length, null, 0);
				using FileStream input = File.OpenRead(item);
				TransformStream(sHA, input);
			}
			sHA.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
			return Hex(sHA.Hash);
		}
		catch
		{
			return "";
		}
	}

	public static string HashNormalizedContent(string? path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return "";
		}
		try
		{
			if (File.Exists(path) && Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase))
			{
				using (FileStream stream = File.OpenRead(path))
				{
					using ZipArchive zipArchive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
					StringBuilder stringBuilder = new StringBuilder();
					long num = 0L;
					foreach (ZipArchiveEntry item in zipArchive.Entries.Where((ZipArchiveEntry e) => !string.IsNullOrEmpty(e.Name) && !string.Equals(Normalize(e.FullName), "modinfo.json", StringComparison.OrdinalIgnoreCase)).OrderBy((ZipArchiveEntry e) => e.FullName, StringComparer.Ordinal))
					{
						if (item.Length < 0 || item.Length > 67108864)
						{
							return "";
						}
						num += item.Length;
						if (num > 536870912)
						{
							return "";
						}
						using Stream stream2 = item.Open();
						stringBuilder.Append(Normalize(item.FullName)).Append('|').Append(item.Length)
							.Append('|')
							.Append(HashStream(stream2))
							.Append('\n');
					}
					return HashBytes(Encoding.UTF8.GetBytes(stringBuilder.ToString()));
				}
			}
			if (Directory.Exists(path))
			{
				StringBuilder stringBuilder2 = new StringBuilder();
				long num2 = 0L;
				foreach (string item2 in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).OrderBy((string x) => Path.GetRelativePath(path, x).Replace('\\', '/'), StringComparer.Ordinal))
				{
					string text = Normalize(Path.GetRelativePath(path, item2));
					if (!string.Equals(text, "modinfo.json", StringComparison.OrdinalIgnoreCase))
					{
						FileInfo fileInfo = new FileInfo(item2);
						if (fileInfo.Length < 0 || fileInfo.Length > 67108864)
						{
							return "";
						}
						num2 += fileInfo.Length;
						if (num2 > 536870912)
						{
							return "";
						}
						using FileStream stream3 = File.OpenRead(item2);
						stringBuilder2.Append(text).Append('|').Append(fileInfo.Length)
							.Append('|')
							.Append(HashStream(stream3))
							.Append('\n');
					}
				}
				return HashBytes(Encoding.UTF8.GetBytes(stringBuilder2.ToString()));
			}
			return File.Exists(path) ? HashFile(path) : "";
		}
		catch
		{
			return "";
		}
	}

	private static string HashFile(string path)
	{
		using FileStream stream = File.OpenRead(path);
		return HashStream(stream);
	}

	private static string HashStream(Stream stream)
	{
		using SHA256 sHA = SHA256.Create();
		return Hex(sHA.ComputeHash(stream));
	}

	private static string HashBytes(byte[] bytes)
	{
		return Hex(SHA256.HashData(bytes));
	}

	private static void TransformStream(HashAlgorithm sha, Stream input)
	{
		byte[] array = new byte[65536];
		int inputCount;
		while ((inputCount = input.Read(array, 0, array.Length)) > 0)
		{
			sha.TransformBlock(array, 0, inputCount, null, 0);
		}
	}

	private static string Normalize(string entry)
	{
		return (entry ?? "").Replace('\\', '/').TrimStart('/');
	}

	private static string Hex(byte[]? bytes)
	{
		return Convert.ToHexString(bytes ?? Array.Empty<byte>()).ToLowerInvariant();
	}
}
