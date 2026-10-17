using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BlockLogger.AntiCheat;

internal static class SignedClientBuildManifestVerifier
{
	public const string ManifestFileName = "client-integrity.manifest.json";

	public const string SignatureFileName = "client-integrity.manifest.sig";

	public static SignedClientBuildManifestStatus Verify(string? packagePath, string publicKeyBase64, string expectedModId, string expectedVersion, int expectedProtocol, string expectedAssemblyFile, string expectedAssemblySha256, long expectedAssemblyBytes, string expectedMvid)
	{
		try
		{
			byte[] array = ReadEntry(packagePath, "client-integrity.manifest.json");
			byte[] array2 = ReadEntry(packagePath, "client-integrity.manifest.sig");
			if (array.Length == 0)
			{
				return new SignedClientBuildManifestStatus
				{
					Present = false,
					Message = "No signed Client Integrity build manifest is installed."
				};
			}
			string manifestSha = Hash(array);
			string signatureSha = Hash(array2);
			using JsonDocument jsonDocument = JsonDocument.Parse(array);
			JsonElement rootElement = jsonDocument.RootElement;
			int num = GetInt(rootElement, "schema");
			string a = GetString(rootElement, "modId");
			string a2 = GetString(rootElement, "version");
			string path = GetString(rootElement, "assemblyFile");
			string a3 = GetString(rootElement, "assemblySha256").ToLowerInvariant();
			string a4 = GetString(rootElement, "moduleVersionId");
			string text = GetString(rootElement, "buildId");
			int num2 = GetInt(rootElement, "protocol");
			long num3 = GetLong(rootElement, "assemblyBytes");
			int bytesRead;
			int num4;
			if (num == 1 && string.Equals(a, expectedModId, StringComparison.OrdinalIgnoreCase) && string.Equals(a2, expectedVersion, StringComparison.OrdinalIgnoreCase) && num2 == expectedProtocol && string.Equals(Path.GetFileName(path), Path.GetFileName(expectedAssemblyFile), StringComparison.OrdinalIgnoreCase) && string.Equals(a3, expectedAssemblySha256, StringComparison.OrdinalIgnoreCase) && num3 == expectedAssemblyBytes && string.Equals(a4, expectedMvid, StringComparison.OrdinalIgnoreCase))
			{
				bytesRead = text.Length;
				num4 = ((bytesRead > 0 && bytesRead <= 96) ? 1 : 0);
			}
			else
			{
				num4 = 0;
			}
			bool flag = (byte)num4 != 0;
			bool flag2 = array2.Length != 0;
			bool flag3 = false;
			string text2 = (publicKeyBase64 ?? "").Trim();
			if (flag2 && text2.Length > 0)
			{
				byte[] signature = ParseSignature(array2);
				byte[] array3 = Convert.FromBase64String(RemoveWhitespace(text2));
				using RSA rSA = RSA.Create();
				rSA.ImportSubjectPublicKeyInfo(array3, out bytesRead);
				flag3 = rSA.VerifyData(array, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
			}
			string message;
			if (flag)
			{
				if (flag3)
				{
					message = "Signed build manifest verified (build " + text + ").";
				}
				else if (text2.Length != 0)
				{
					message = (flag2 ? ("Build manifest signature did not verify (build " + text + ").") : ("Build manifest is unsigned (build " + text + ")."));
				}
				else
				{
					message = "Build manifest present (build " + text + "); publisher signature enforcement is not configured.";
				}
			}
			else
			{
				message = "The signed build manifest does not describe the Client Integrity companion actually installed on the server.";
			}
			return new SignedClientBuildManifestStatus
			{
				Present = true,
				SignaturePresent = flag2,
				SignatureVerified = flag3,
				ClaimsMatchBaseline = flag,
				ManifestSha256 = manifestSha,
				SignatureSha256 = signatureSha,
				BuildId = text,
				Message = message
			};
		}
		catch (Exception ex)
		{
			return new SignedClientBuildManifestStatus
			{
				Present = true,
				Message = "Build manifest verification error: " + ex.Message
			};
		}
	}

	private static string GetString(JsonElement root, string name)
	{
		JsonElement value;
		return (root.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.String) ? (value.GetString() ?? "").Trim() : "";
	}

	private static int GetInt(JsonElement root, string name)
	{
		JsonElement value;
		int value2;
		return (root.TryGetProperty(name, out value) && value.TryGetInt32(out value2)) ? value2 : 0;
	}

	private static long GetLong(JsonElement root, string name)
	{
		JsonElement value;
		long value2;
		return (root.TryGetProperty(name, out value) && value.TryGetInt64(out value2)) ? value2 : 0;
	}

	private static byte[] ReadEntry(string? packagePath, string fileName)
	{
		if (string.IsNullOrWhiteSpace(packagePath))
		{
			return Array.Empty<byte>();
		}
		if (Directory.Exists(packagePath))
		{
			string path = Path.Combine(packagePath, fileName);
			return File.Exists(path) ? File.ReadAllBytes(path) : Array.Empty<byte>();
		}
		if (File.Exists(packagePath) && Path.GetExtension(packagePath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
		{
			using (FileStream stream = File.OpenRead(packagePath))
			{
				using ZipArchive zipArchive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
				ZipArchiveEntry zipArchiveEntry = zipArchive.Entries.FirstOrDefault((ZipArchiveEntry e) => string.Equals(e.FullName.Replace('\\', '/').TrimStart('/'), fileName, StringComparison.OrdinalIgnoreCase));
				if (zipArchiveEntry != null && zipArchiveEntry.Length >= 0 && zipArchiveEntry.Length <= 262144)
				{
					using Stream stream2 = zipArchiveEntry.Open();
					using MemoryStream memoryStream = new MemoryStream();
					stream2.CopyTo(memoryStream);
					return memoryStream.ToArray();
				}
				return Array.Empty<byte>();
			}
		}
		if (File.Exists(packagePath))
		{
			string path2 = Path.Combine(Path.GetDirectoryName(packagePath) ?? "", fileName);
			return File.Exists(path2) ? File.ReadAllBytes(path2) : Array.Empty<byte>();
		}
		return Array.Empty<byte>();
	}

	private static byte[] ParseSignature(byte[] bytes)
	{
		if (bytes.Length == 0)
		{
			return Array.Empty<byte>();
		}
		string text;
		try
		{
			text = Encoding.UTF8.GetString(bytes).Trim();
		}
		catch
		{
			return bytes;
		}
		if (text.Length == 0)
		{
			return Array.Empty<byte>();
		}
		try
		{
			return Convert.FromBase64String(RemoveWhitespace(text));
		}
		catch
		{
			return bytes;
		}
	}

	private static string Hash(byte[] bytes)
	{
		if (bytes == null || bytes.Length == 0)
		{
			return "";
		}
		using SHA256 sHA = SHA256.Create();
		return Convert.ToHexString(sHA.ComputeHash(bytes)).ToLowerInvariant();
	}

	private static string RemoveWhitespace(string value)
	{
		return new string(value.Where((char c) => !char.IsWhiteSpace(c)).ToArray());
	}
}
