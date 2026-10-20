# Suite 1.2.44 — MigrationContainers1

## Bookshelf/container reconnect

Hold the installed replacement bookshelf, target the missing block, and run:

```
/nseblockmigrate replaceheld reconnect confirm-inventory-risk no-backup manual-copy-confirmed
```

Reconnect checks compatible surviving metadata, container class and bookshelf shape/material/slot layout. It preserves contents as they are, including an empty inventory. It neither queries historical snapshots nor invents a container or items. It does not require a whole-world preparation scan. Safe loaded containers keep their block entity, inventory and ItemStack instances; saved reconnection rewrites only the block identity and verified solid cell. Missing/unverifiable metadata is skipped and reported, not replaced with a fresh empty container. Unresolved retained item IDs are refused so removed item mods do not silently lose contents.

Use `recover-empty confirm-inventory-risk` instead when you explicitly want logged items restored into an empty compatible container. That separate mode needs matching retained metadata and BlockLogger history; it keeps existing historical preparation and replay protection. Neither mode can recreate already-deleted shape/item attributes. `replace` additionally requires `confirm-overwrite`.

Skipping automatic backup requires both `no-backup manual-copy-confirmed`; make a manual world copy first. The tool is experimental. Omitting those flags retains the automatic-backup default. These flags apply to the entire saved rule set, as does its chosen inventory mode.

## Chiseled materials

```
/nseblockmigrate inspect
/nseblockmigrate replaceheld no-backup manual-copy-confirmed
```

A vanilla chiseled block is a registered host containing numeric material IDs. Targeting it selects the missing material rather than attempting to replace its healthy host. Hold a registered solid non-container replacement material. When multiple materials are missing:

```
/nseblockmigrate replaceheld source=13594 no-backup manual-copy-confirmed
```

For an unselectable but already-loaded position, substitute its actual absolute X/local Y/Z coordinates:

```
/nseblockmigrate inspect at=100,120,200 dimension=0
/nseblockmigrate replaceheld at=100,120,200 dimension=0 source=13594 no-backup manual-copy-confirmed
```

A rule affects all missing references to the chosen numeric ID. It preserves the chiseled host, voxel cuboids, healthy materials, rotation, custom name and unrelated saved bytes. Saved material-only damage is processed even when the chunk palette contains no missing host IDs. Live processing uses the existing bounded loaded-terrain pass. Air/fluid/block-entity targets are skipped for materials; healthy material IDs and air are not treated as damage. Unknown/custom chiseled formats remain unchanged and are diagnosed. The generic terrain scan does not enumerate every nested chiseled material; `inspect` and loaded/saved processing provide that path.

Check `/nseblockmigrate rules` and `/nseblockmigrate status`, or the webpanel's migration diagnostics. Material references have separate counters. Claims do not gate migration. Disabling/clearing rules stops further processing; prior changes persist through normal saves.

## Validation scope

Validation uses official Vintage Story 1.22.7 serialization and disposable official-server worlds. Tests include byte preservation of chiseled geometry/name/rotation/healthy materials, rejected invalid targets and ambiguity, coordinate-based held selection, live/saved material-only processing, and compatible bookshelf reconnection without history. Server fixtures use a detached administrator object; no authenticated game clients or production worlds are used. Accompanying result files distinguish completed runs and their artifact hashes.

Completed checks: 146 codec/restore/preservation checks, 17 history checks, 24 inventory payload/replay checks, 8 backup checks, 5 chat parser cases and Chromium migration UI checks. The four final official-server runs cover saved and live occupied/empty reconnection, coordinate/ambiguous chisel selection and live/saved-prefix byte preservation, plus historical recovery/replay/changed-data refusal. All four runs exercised the same shipping ZIP. [Recorded results and hashes](validation/migrationcontainers1-server.json).
