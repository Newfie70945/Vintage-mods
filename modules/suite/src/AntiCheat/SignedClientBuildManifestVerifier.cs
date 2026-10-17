using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BlockLogger.AntiCheat;

internal sealed class SignedClientBuildManifestStatus
{
    public bool Present { get; init; }
    public bool SignaturePresent { get; init; }
    public bool SignatureVerified { get; init; }
    public bool ClaimsMatchBaseline { get; init; }
    public string ManifestSha256 { get; init; } = "";
    public string SignatureSha256 { get; init; } = "";
    public string BuildId { get; init; } = "";
    public string Message { get; init; } = "";
}

internal static class SignedClientBuildManifestVerifier
{
    public const string ManifestFileName = "client-integrity.manifest.json";
    public const string SignatureFileName = "client-integrity.manifest.sig";

    public static SignedClientBuildManifestStatus Verify(
        string? packagePath,
        string publicKeyBase64,
        string expectedModId,
        string expectedVersion,
        int expectedProtocol,
        string expectedAssemblyFile,
        string expectedAssemblySha256,
        long expectedAssemblyBytes,
        string expectedMvid)
    {
        try
        {
            byte[] manifestBytes = ReadEntry(packagePath, ManifestFileName);
            byte[] signatureFileBytes = ReadEntry(packagePath, SignatureFileName);
            if (manifestBytes.Length == 0)
                return new() { Present = false, Message = "No signed Client Integrity build manifest is installed." };

            string manifestSha = Hash(manifestBytes);
            string sigSha = Hash(signatureFileBytes);
            using var doc = JsonDocument.Parse(manifestBytes);
            var root = doc.RootElement;
            int schema = GetInt(root, "schema");
            string modId = GetString(root, "modId");
            string version = GetString(root, "version");
            string assemblyFile = GetString(root, "assemblyFile");
            string assemblySha = GetString(root, "assemblySha256").ToLowerInvariant();
            string mvid = GetString(root, "moduleVersionId");
            string buildId = GetString(root, "buildId");
            int protocol = GetInt(root, "protocol");
            long assemblyBytes = GetLong(root, "assemblyBytes");

            bool claimsMatch =
                schema == 1 &&
                string.Equals(modId, expectedModId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(version, expectedVersion, StringComparison.OrdinalIgnoreCase) &&
                protocol == expectedProtocol &&
                string.Equals(Path.GetFileName(assemblyFile), Path.GetFileName(expectedAssemblyFile), StringComparison.OrdinalIgnoreCase) &&
                string.Equals(assemblySha, expectedAssemblySha256, StringComparison.OrdinalIgnoreCase) &&
                assemblyBytes == expectedAssemblyBytes &&
                string.Equals(mvid, expectedMvid, StringComparison.OrdinalIgnoreCase) &&
                buildId.Length is > 0 and <= 96;

            bool signaturePresent = signatureFileBytes.Length > 0;
            bool signatureVerified = false;
            string key = (publicKeyBase64 ?? "").Trim();
            if (signaturePresent && key.Length > 0)
            {
                byte[] signature = ParseSignature(signatureFileBytes);
                byte[] spki = Convert.FromBase64String(RemoveWhitespace(key));
                using RSA rsa = RSA.Create();
                rsa.ImportSubjectPublicKeyInfo(spki, out _);
                signatureVerified = rsa.VerifyData(manifestBytes, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            }

            string message = !claimsMatch
                ? "The signed build manifest does not describe the Client Integrity companion actually installed on the server."
                : signatureVerified
                    ? $"Signed build manifest verified (build {buildId})."
                    : key.Length == 0
                        ? $"Build manifest present (build {buildId}); publisher signature enforcement is not configured."
                        : signaturePresent
                            ? $"Build manifest signature did not verify (build {buildId})."
                            : $"Build manifest is unsigned (build {buildId}).";

            return new()
            {
                Present = true,
                SignaturePresent = signaturePresent,
                SignatureVerified = signatureVerified,
                ClaimsMatchBaseline = claimsMatch,
                ManifestSha256 = manifestSha,
                SignatureSha256 = sigSha,
                BuildId = buildId,
                Message = message
            };
        }
        catch (Exception ex)
        {
            return new() { Present = true, Message = "Build manifest verification error: " + ex.Message };
        }
    }

    private static string GetString(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? (value.GetString() ?? "").Trim() : "";
    private static int GetInt(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.TryGetInt32(out int parsed) ? parsed : 0;
    private static long GetLong(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.TryGetInt64(out long parsed) ? parsed : 0L;

    private static byte[] ReadEntry(string? packagePath, string fileName)
    {
        if (string.IsNullOrWhiteSpace(packagePath)) return Array.Empty<byte>();
        if (Directory.Exists(packagePath))
        {
            string path = Path.Combine(packagePath, fileName);
            return File.Exists(path) ? File.ReadAllBytes(path) : Array.Empty<byte>();
        }
        if (File.Exists(packagePath) && Path.GetExtension(packagePath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var fs = File.OpenRead(packagePath);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read, false);
            var entry = zip.Entries.FirstOrDefault(e => string.Equals(e.FullName.Replace('\\', '/').TrimStart('/'), fileName, StringComparison.OrdinalIgnoreCase));
            if (entry == null || entry.Length < 0 || entry.Length > 256 * 1024) return Array.Empty<byte>();
            using var input = entry.Open();
            using var ms = new MemoryStream();
            input.CopyTo(ms);
            return ms.ToArray();
        }
        if (File.Exists(packagePath))
        {
            string sibling = Path.Combine(Path.GetDirectoryName(packagePath) ?? "", fileName);
            return File.Exists(sibling) ? File.ReadAllBytes(sibling) : Array.Empty<byte>();
        }
        return Array.Empty<byte>();
    }

    private static byte[] ParseSignature(byte[] bytes)
    {
        if (bytes.Length == 0) return Array.Empty<byte>();
        string text;
        try { text = Encoding.UTF8.GetString(bytes).Trim(); }
        catch { return bytes; }
        if (text.Length == 0) return Array.Empty<byte>();
        try { return Convert.FromBase64String(RemoveWhitespace(text)); }
        catch { return bytes; }
    }

    private static string Hash(byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0) return "";
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(bytes)).ToLowerInvariant();
    }

    private static string RemoveWhitespace(string value) => new string(value.Where(c => !char.IsWhiteSpace(c)).ToArray());
}
