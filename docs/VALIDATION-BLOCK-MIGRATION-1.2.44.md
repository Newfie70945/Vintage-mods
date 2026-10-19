# Suite 1.2.44 — load-time block migration validation

Build: `newfies-server-essentials-suite-1.2.44-ms11-migrationinventory1`.

## Verified

- Suite Release packaging completed with **zero compiler errors or warnings** against the official Vintage Story **1.22.7** assemblies and .NET 10.
- **135** codec, retained inventory/metadata, missing-source, mapping policy and preservation checks passed; **17** history/snapshot checks, **24** inventory option/payload/manifest/replay checks and **5** official VTML/chat checks passed.
- **22** general repository checks, **7** post-builder checks, module/editor consistency and `git diff --check` passed.
- Chromium checks cover the replacement migration page, per-ID destinations, healthy-source exclusion, selection retention across filtering, source-scoped search, explicit suggestions, reviewed-input binding, enable/disable states, exact-code skips, optional backup acknowledgement, safe-mode/owner restrictions, escaped labels and mobile width. Rule creation during a running scan, the visible rules table, edit/remove actions, and recovery acknowledgements/cutoff binding are covered. The old repair page and world-rewrite staging buttons are absent.

## Official-server integration

The opt-in test copies a disposable baseline save; it never edits the baseline or a production world. It seeds two distinct missing numeric IDs plus two occurrences sharing a third missing ID in an existing saved chunk. There are **zero authenticated game clients**; the looked-at/held adapter uses a detached administrator fixture with real official inventory classes. This is functional validation, not a player-load benchmark.

1. Completed the saved-map scan and configured two separate source/destination mappings through Owner-authenticated HTTP, with CSRF.
2. Confirmed changed backup options cannot stage an old preview.
3. Restarted and verified migration activation and an integrity-checked pre-activation world backup containing the original seeded chunk.
4. Requested the existing fixture column through the official loader; observed granite and basalt at the configured positions and two migrated references.
5. Started a whole-world scan, then exercised the actual looked-at/held adapter without waiting: it staged an ID-wide rule for the third ID, kept the two existing rules, left the held stack at seven blocks, and refused a healthy looked-at source. The in-game rules list included the saved mapping. No terrain changed immediately.
6. Restarted and loaded the fixture column normally. **Both occurrences of the third ID became the held replacement**, demonstrating that it is not a one-position replacement.
7. Disabled migration immediately, stopped the server normally, and checked the saved chunk against all **32,768** original solid cells. Only the four configured occurrences differed; healthy neighboring cells survived. Database integrity passed.
8. Restarted again and verified migration remained disabled. All test-server processes stopped gracefully.

Raw session result: `artifacts/validation/loaded-migration-1.2.44-1792373299/result.json` (ignored local artifact). Reproduce with:

```sh
# Set VINTAGE_STORY to your official 1.22.7 install, DOTNET_ROOT to .NET 10,
# and NSS_BATCH_TEST_BASELINE to a stopped disposable baseline data directory.
python tools/build.py --module suite
dotnet run --project modules/suite/tests/BlockRepairChecks -c Release
python modules/suite/tests/integration/test_loaded_migration.py
node modules/suite/tests/web/test_block_migration.cjs
```

The probe is packaged only inside the disposable integration server, never in the production mod ZIP. Temporary test credentials remain under ignored artifacts and are excluded from source archives.

## Optional inventory recovery integration

`modules/suite/tests/integration/test_migration_inventories.py` copies the same stopped disposable official 1.22.7 baseline and seeds one unresolved host ID plus compatible retained vanilla Bookshelf metadata. Preparation checks 4,648 saved chunk records without activating terrain. Each case uses Owner-authenticated/CSRF-protected preview and activation, a verified backup, an official loader probe, a normal world save, all 32,768 solid-cell comparisons and SQLite integrity checks. There are zero authenticated game clients; these are functional checks, not load benchmarks.

- **Recover empty:** one logged book was reconstructed in an empty shelf. Live recovery counters matched the result; missing book-text attributes stayed empty as documented.
- **Replace contents:** the occupied fixture was replaced with the reviewed logged book only after the separate overwrite acknowledgement. Omitting either acknowledgement and changing cutoff after review were refused.
- **Recover empty, occupied shelf:** the seven surviving items and their written attribute string stayed intact; no historical stack was inserted.
- Reusing the original payload after recovery was refused by the durable receipt, and changed retained inventory data was refused. Payload/receipt unit tests also cover world isolation, old snapshots across plans and atomic group reservations.
- Byte-codec checks ensure an unapproved neighbor sharing the same numeric source remains unchanged. Missing/incompatible metadata is never manufactured.

The test fixture keeps its column loaded until the normal save; forcibly dropping a column before saving is not a durability test. Test probe DLLs are never included in the compiled Suite.

Reproduce each inventory case with the environment above, for example:

```sh
NSS_INVENTORY_MODE=recover-empty NSS_INVENTORY_FILLED=0 python modules/suite/tests/integration/test_migration_inventories.py
NSS_INVENTORY_MODE=replace NSS_INVENTORY_FILLED=1 python modules/suite/tests/integration/test_migration_inventories.py
NSS_INVENTORY_MODE=recover-empty NSS_INVENTORY_FILLED=1 python modules/suite/tests/integration/test_migration_inventories.py
```

Raw ignored session directories: `inventory-migration-1.2.44-1792373147`, `inventory-migration-1.2.44-1792373455`, and `inventory-migration-1.2.44-1792373548`, under `artifacts/validation/`. The release bundle contains redacted result summaries, not private test configurations.

## Limits

No production world was supplied. These checks do not identify the originals of IDs whose saved codes were overwritten, recreate deleted block-entity metadata or missing historical item attributes, migrate item-stack IDs or establish performance gains. Compatible retained block-entity bytes are checked for byte preservation in official-format codec fixtures; arbitrary modded container conversion is refused. The migrator supports the inspected official 1.22.7 format and refuses unsupported runtimes/codecs and conflicting deserialization patches. Cached light/map data may require refreshing.

The root changelog and [migration guide](../modules/suite/guides/BLOCK-ID-MIGRATION.md) describe the replacement workflow. Historical repair validation documents remain historical evidence, not descriptions of the new user interface.
