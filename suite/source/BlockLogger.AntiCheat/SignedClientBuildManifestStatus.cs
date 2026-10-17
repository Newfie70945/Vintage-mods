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
