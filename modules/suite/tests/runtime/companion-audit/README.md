# Companion audit checks

Set `VINTAGE_STORY`, `CHATGUARD_ASSEMBLY` and `MOTD_ASSEMBLY` to the official game folder and built companion DLLs. Build `CompanionAuditChecks.csproj`, then execute its DLL using .NET 10.

Checks use production methods with temporary files: immutable moderation snapshots, inactive/active history expiry, identity memory ceiling, audit rotation/overflow telemetry, damaged-state preservation, PNG signature/cache invalidation and oversized-file allocation guard. Also checks failed ChatGuard settings persistence leaves the active config unchanged, autosave does not wait on a deliberately stalled writer, and PNG dimensions/decoded pixel counts are rejected before native decoding. No network connection or external messages are used.
