# Suite 1.2.44 — MigrationLive1

Look at a genuinely missing block while holding a registered replacement block:

```
/nseblockmigrate replaceheld no-backup manual-copy-confirmed
/nseblockmigrate rules
/nseblockmigrate status
```

`manual-copy-confirmed` acknowledges the warning to make a manual world copy. Without both flags the command retains its automatic-backup default; `no-backup` alone is refused before saving. This experimental feature can lose data if misused. A rule affects every missing occurrence with the selected numeric ID, not just the looked-at cell. Healthy registered IDs and air remain protected. Claims are not consulted by migration; rollback has separate claim checks.

Saving activates the rules after any required preparation. A snapshot of already-loaded chunk keys is processed on the server thread, with a 2 ms soft time budget, at most 4096 cell reads, 32 writes and 16 chunk starts per 50 ms callback. No terrain is loaded by this pass and no chunk references are retained between callbacks. It stops on completion, disable, clearing/reconfiguration or disposal. This budget bounds batches, not the cost of an individual engine call.

Live solid replacements preserve compatible instantiated block entities with ExchangeBlock. Historical inventory recovery is not performed by this pass; missing/incompatible retained metadata, fluids and historical inventory sources are skipped. These require the saved-chunk processor and its evidence checks, rather than forcing replacements. Normal saves persist edits; no block drops are spawned. The saved loader remains responsible for unloaded terrain, fluid and decor processing.

Skipped chunk loads count attempts and can repeat the same chunk; they are not a count of distinct damaged chunks. The uploaded production diagnostic reported the former retained-entity verification limit for source 13594 → game:planks-aged-ud. Verification now accepts up to 32768 records (one per cell), 32 MiB total retained payload and 2 MiB per record, with duplicate-position, full parsing and identity checks retained. A 600-record fixture verifies unrelated records remain byte-exact and source-hosted/duplicate records remain refused. Only compact identity/position summaries are retained while checking each record.

Counters distinguish live solid changes/skipped cells/pending chunks from saved-chunk changes/refusals. A refused saved chunk may contain safe as well as unsafe occurrences. The latest refusal includes a retained-block position when available; it does not prove every block in that chunk is broken. A visible question-mark texture alone does not prove a missing ID.

Validation uses official 1.22.7 serialization and a disposable official-server fixture with a detached administrator object. It is not a test of the user's production world or authenticated-client gameplay. See the build's accompanying result file for completed checks.

Completed checks for this build:

- Official 1.22.7 serialization: 139 block-ID codec/restore/preservation assertions, including dense unrelated metadata, plus 17 history/recommendation, 24 inventory/replay, 8 online backup/cancellation and 5 chat-parser checks.
- Chromium: migration save/acknowledgement, inventory review, loaded/saved counters and reasons, read-only/Safe Mode, escaped output and mobile layout.
- Disposable official-server integration: normal load/save/restart, held no-backup acknowledgement, no backup creation, both loaded matching occurrences migrated without a reload, healthy-cell preservation and disable/cancel checks. See the accompanying JSON for final execution results. No production world was modified.
