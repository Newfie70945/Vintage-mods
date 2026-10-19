# Suite 1.2.44 — automatic live migration validation

Build: `newfies-server-essentials-suite-1.2.44-ms11-livemigration1`, official Vintage Story 1.22.7 / .NET 10.

## Checks

- Release compilation: zero warnings/errors against the inspected official assemblies.
- 135 codec/policy/preservation checks, 17 exact-position history checks, 24 inventory payload/options/manifest/receipt checks, 8 online-backup checks and 5 official VTML/chat checks.
- Online-backup checks use a WAL database with a concurrent writer: the snapshot preserves transaction consistency, all fixture pages and SQLite integrity. Cancellation removes incomplete private backup files; existing complete backups cannot be overwritten, and interrupted private journals are cleaned before retrying.
- Chromium checks cover saving rules during scanning, visible rule management, automatic backing-up-to-active polling, the Enable Migration Now control, recovery/cutoff acknowledgements, protected healthy sources, escaped evidence, Owner/read-only/safe-mode behavior and mobile width.
- General repository, post-builder, source/editor consistency and whitespace checks passed. Test probe DLLs are excluded from the compiled Suite ZIP.

## Official-server integration

These opt-in tests copy a stopped disposable baseline and use Owner-authenticated HTTP with CSRF plus privileged test-only loader probes. They never edit the baseline or a production world. There are **zero authenticated game clients**; the held adapter uses a detached administrator fixture with official inventory classes. These are functional tests, not player-load benchmarks or production performance evidence.

`modules/suite/tests/integration/test_loaded_migration.py`:

1. Review two missing-ID rules, request activation and wait for the verified background world backup. The same server process becomes active without restarting.
2. Load the existing fixture column through the official loader and observe both chosen destinations.
3. Start a whole-world scan, then save a third ID-wide rule through the actual looked-at/held adapter. Existing rules and the held stack remain intact; healthy looked-at sources are refused, and the in-game rules list shows the new mapping.
4. Wait for automatic activation in the same process. Save the fixture through the official autosave, then explicitly reload only that disposable column. Both occurrences of the held source become the target, with no server restart.
5. Disable immediately. A test deliberately queues a live activation callback and disables migration before that callback runs; it cannot re-enable the disabled setting.
6. Stop normally and compare all **32,768** solid cells against the original fixture. Only configured references differ; healthy neighbors survive. SQLite integrity passes, and disabled state survives a later restart.

`modules/suite/tests/integration/test_migration_inventories.py`:

- **Recover empty / direct rule:** saving a rule starts evidence preparation, backup and activation automatically, without a separate enable operation or restart. One logged book is reconstructed from its exact-position snapshot.
- **Replace contents:** the occupied fixture is replaced only after the separate overwrite acknowledgement. Changed cutoff/options after review are refused.
- **Recover empty / occupied:** seven retained items and their surviving text attribute remain intact; historical contents are not inserted.
- Prepared evidence scans 4,648 saved chunk records without activating terrain. Loader probes confirm real inventory initialization; durable receipts block replay and changed retained data is refused. Verified backups, normal saves, all 32,768 solid-cell comparisons and database integrity checks cover each case.

The fixture stays loaded until normal saving. Its explicit reload is a test operation; production activation does not force-unload chunks near players. Already loaded terrain uses changed rules when it next reloads.

## Reproduce

Set `VINTAGE_STORY` to the official 1.22.7 directory, `DOTNET_ROOT` to .NET 10, and `NSS_BATCH_TEST_BASELINE` to a stopped disposable baseline data directory. Keep private test credentials/configuration under ignored artifacts.

```sh
python tools/build.py --module suite
dotnet run --project modules/suite/tests/BlockRepairChecks -c Release
node modules/suite/tests/web/test_block_migration.cjs
python modules/suite/tests/integration/test_loaded_migration.py
NSS_INVENTORY_MODE=recover-empty NSS_INVENTORY_FILLED=0 NSS_INVENTORY_DIRECT=1 python modules/suite/tests/integration/test_migration_inventories.py
NSS_INVENTORY_MODE=replace NSS_INVENTORY_FILLED=1 python modules/suite/tests/integration/test_migration_inventories.py
NSS_INVENTORY_MODE=recover-empty NSS_INVENTORY_FILLED=1 python modules/suite/tests/integration/test_migration_inventories.py
```

Redacted per-session results are included in the release bundle; full disposable server data and probe binaries stay under ignored `artifacts/validation/`.

## Limits

No production world was supplied. This does not identify originals whose saved codes were overwritten, recreate deleted container metadata or missing historical attributes, migrate item-stack IDs, or establish performance gains. Snapshots restore logged item codes/quantities with defaults; retained bytes preserve surviving attributes. Unsupported codecs/runtimes, conflicting deserialization patches and healthy source IDs remain protected. Backups cover saved database state, not unsaved loaded-chunk changes; disk capacity and I/O still limit backup completion. See the [migration guide](../modules/suite/guides/BLOCK-ID-MIGRATION.md) for safeguards, cancellation and receipt/crash limits.
