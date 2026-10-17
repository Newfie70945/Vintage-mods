param(
    [Parameter(Mandatory=$true)][string]$PackageDirectory,
    [Parameter(Mandatory=$true)][string]$PrivateKeyPem,
    [string]$BuildId = ""
)

$ErrorActionPreference = "Stop"
$package = (Resolve-Path $PackageDirectory).Path
$dllPath = Join-Path $package "NewfiesAntiCheatClient.dll"
if (-not (Test-Path $dllPath)) { throw "NewfiesAntiCheatClient.dll was not found in $package" }
if (-not (Test-Path $PrivateKeyPem)) { throw "Private signing key was not found: $PrivateKeyPem" }

if ([string]::IsNullOrWhiteSpace($BuildId)) {
    $BuildId = "ci-1.1.27-" + [DateTimeOffset]::UtcNow.ToString("yyyyMMddHHmmss")
}
if ($BuildId.Length -gt 96) { throw "BuildId must be 96 characters or fewer." }

$assembly = [System.Reflection.Assembly]::LoadFile($dllPath)
$mvid = $assembly.ManifestModule.ModuleVersionId.ToString("D")
$dllBytes = [IO.File]::ReadAllBytes($dllPath)
$sha = [System.Security.Cryptography.SHA256]::HashData($dllBytes)
$shaHex = [Convert]::ToHexString($sha).ToLowerInvariant()
$size = (Get-Item $dllPath).Length

$manifest = [ordered]@{
    schema = 1
    modId = "newfiesanticheatclient"
    version = "1.1.27"
    protocol = 13
    assemblyFile = "NewfiesAntiCheatClient.dll"
    assemblySha256 = $shaHex
    assemblyBytes = $size
    moduleVersionId = $mvid
    buildId = $BuildId
}
$json = $manifest | ConvertTo-Json -Compress
$manifestPath = Join-Path $package "client-integrity.manifest.json"
[IO.File]::WriteAllText($manifestPath, $json, [Text.UTF8Encoding]::new($false))
$manifestBytes = [IO.File]::ReadAllBytes($manifestPath)

$rsa = [System.Security.Cryptography.RSA]::Create()
try {
    $pem = [IO.File]::ReadAllText((Resolve-Path $PrivateKeyPem).Path)
    $rsa.ImportFromPem($pem)
    $signature = $rsa.SignData($manifestBytes, [System.Security.Cryptography.HashAlgorithmName]::SHA256, [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $sigPath = Join-Path $package "client-integrity.manifest.sig"
    [IO.File]::WriteAllText($sigPath, [Convert]::ToBase64String($signature) + "`n", [Text.UTF8Encoding]::new($false))
    Write-Host "Signed Client Integrity build manifest: $manifestPath"
    Write-Host "Signature: $sigPath"
    Write-Host "Build ID: $BuildId"
    Write-Host "DLL SHA-256: $shaHex"
}
finally {
    $rsa.Dispose()
}
