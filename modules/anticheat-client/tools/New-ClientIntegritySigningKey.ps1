param(
    [string]$OutputDirectory = "."
)

$ErrorActionPreference = "Stop"
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

$rsa = [System.Security.Cryptography.RSA]::Create(3072)
try {
    $privateBytes = $rsa.ExportPkcs8PrivateKey()
    $publicBytes = $rsa.ExportSubjectPublicKeyInfo()

    $privateB64 = [Convert]::ToBase64String($privateBytes, [System.Base64FormattingOptions]::InsertLineBreaks)
    $privatePem = "-----BEGIN PRIVATE KEY-----`n$privateB64`n-----END PRIVATE KEY-----`n"
    $privatePath = Join-Path $OutputDirectory "client-integrity-signing-private.pem"
    [IO.File]::WriteAllText($privatePath, $privatePem, [Text.UTF8Encoding]::new($false))

    $publicB64 = [Convert]::ToBase64String($publicBytes)
    $publicPath = Join-Path $OutputDirectory "client-integrity-signing-public.base64"
    [IO.File]::WriteAllText($publicPath, $publicB64 + "`n", [Text.UTF8Encoding]::new($false))

    Write-Host "Created signing key pair."
    Write-Host "PRIVATE: $privatePath"
    Write-Host "PUBLIC : $publicPath"
    Write-Warning "Keep the private PEM offline and NEVER commit it to GitHub or ship it inside the mod."
}
finally {
    $rsa.Dispose()
}
