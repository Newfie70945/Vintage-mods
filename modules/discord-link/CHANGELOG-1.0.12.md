# Discord Integration 1.0.12

Changes after public 1.0.11; consolidates linking memory cleanup and the feed/persistence improvement pass.

- Prune expired linking codes, issuance cooldown records and failed-attempt histories, including idle cleanup every minute.
- Limit pending codes to 1,024 and temporary issuance/failure identities to 4,096 while preserving active rate limits. Clear temporary linking state at shutdown.
- Cache MOTD feeds for 30 seconds and observe failed background refreshes with bounded retry delays.
- Suppress unchanged feed events and ignore late completion after disposal.
- Preserve unreadable linked-account and role synchronization stores. Skip role changes when persisted ownership state is unavailable.
- Roll back in-memory mappings on failed writes and consume one-time codes only after successful persistence.
- Align project, assembly, startup reporting and package names to 1.0.12, exactly one patch above the verified public release.

Persistent links and role mappings are preserved. Fixture checks send no real Discord messages. Original memory-cleanup evidence is retained in `CHANGELOG-1.0.12-MemoryAudit1.md`.
