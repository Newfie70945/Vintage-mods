# Client Integrity signed build manifests

Protocol 13 can optionally require an RSA-SHA256 publisher signature for the Client Integrity build installed on the server.

1. On a trusted/offline machine running PowerShell 7+, run `New-ClientIntegritySigningKey.ps1` once.
2. Keep `client-integrity-signing-private.pem` private and outside the Git repository, server package, backups exposed to players, and release ZIPs.
3. After compiling Client Integrity, run `Sign-ClientIntegrityManifest.ps1 -PackageDirectory <compiled mod folder> -PrivateKeyPem <private pem>` before creating the final mod ZIP.
4. Put the contents of `client-integrity-signing-public.base64` into `ClientBuildManifestPublicKeyBase64` in `NewfiesAntiCheat.json`.
5. Set `RequireSignedClientBuildManifest` to `true` only after the server-installed Client Integrity package contains both `client-integrity.manifest.json` and `client-integrity.manifest.sig` and the startup log says the signature verified.

The public key may be committed or distributed. The private key must never be committed or shipped.
