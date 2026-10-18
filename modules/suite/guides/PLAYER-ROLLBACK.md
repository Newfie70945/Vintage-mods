# Single-block, container, player and area rollback — 1.2.44

This update reads the existing SQLite `events` and `snapshots` tables. It does not require new logging to recover information those tables already contain. It does not rewrite historical rows or add a recovery database schema. Existing logs must have captured the relevant actions.

This pass advances the Suite to 1.2.44; companion versions remain unchanged. The complete Suite builds against Vintage Story 1.22.7. Planner, command/API-double and SQLite fixtures passed; test the world adapter on a COPY of the damaged world before production recovery.

## One block or container

Look at the block/container and run:

```
/rollback block 2h
/rollback execute <token-shown-in-preview>
```

The first command previews how to undo logged changes at **exactly that block position**, in that dimension, back to two hours ago. Container contents are included automatically. All actors at that location are included; neighboring blocks and inventories are excluded. The preview lists the block code change and up to 12 changed inventory slots; the full report contains every slot. The command does not modify anything until you execute its token. Execution happens live, without a restart, for loaded compatible locations.

For a missing block, use absolute world coordinates (the saved coordinates shown in BlockLogger reports):

```
/rollback block 30m at 517489 224 517472
/rollback block 30m at 517489 224 517472 dimension 0
/rollback execute <token-shown-in-preview>
```

Coordinates default to your current dimension; a looked-at target uses its own dimension. `/rollback preview block 2h` is an equivalent spelling. Relative durations accept seconds/minutes/hours/days, from `1s` through `7d`. The existing `from ... to ...` UTC form works too. Coordinates and the time window stay fixed if you move or look elsewhere after preview.

Inventory recovery requires historical pre-change contents, logged item transfers and a matching final snapshot for surviving containers. Enable `LogContainerSnapshots` and `SnapshotOnSlotChange` in BlockLogger for future history; these settings cannot recreate missing past records. Recovery preserves matching live stack attributes, but reconstructed stacks have default attributes because old snapshots recorded codes/counts only. Items already taken elsewhere are not confiscated.

An unchanged existing bookshelf or modded container can have its inventory restored through `IBlockEntityContainer`, subject to slot/item/state checks. Recreating a destroyed container is limited to supported basic storage classes; missing bookshelf shape/material, machine state and other metadata are not invented. For a damaged block ID with **surviving saved bookshelf metadata**, use the separate [inventory restoration workflow](BLOCK-ID-REPAIR.md#automatic-inventory-restoration-suite-1244).

Claimed land is excluded by default. Add `--include-claims` when intentional, subject to your current game permissions. Back up the world, close the container and review the preview before execution. Live changes invalidate recovery. Empty previews offer no execution token. Existing expiry, durable receipt, cancellation and duplication protections also apply here.

Rollback replies are escaped as plain text and bounded before entering game chat, including help, historical values and exceptions. Private send failures do not leave recovery stuck. If an earlier build has already left broken markup in your chat history, use the client command `.clearchat` to discard that history; `.debug recomposechat` rebuilds the chat UI if needed.

## Start here

Use `/rollback help` (or `/rollback` with no arguments) for the in-game guide. Help is available while the receipt index initializes or another recovery is running, and does not replace or cancel a preview.

Keep the attacker banned and offline. Preserve a full world backup and Suite data before executing recovery.

```
/rollback preview player bossmann200 1d --inventories --replace-entities
/rollback execute <token-shown-in-preview>
```

The first command makes a report and makes no world changes. The second executes only the reviewed operations. The target may be an exact player name or an exact UID. An ambiguous name requires the UID. The relative duration ends at the moment the preview is requested; execution keeps that end time fixed.

For a known attack window, use UTC timestamps:

```
/rollback preview player <uid> from 2026-10-04T19:50:00Z to 2026-10-04T20:35:00Z --inventories --replace-entities
```

These timestamps are examples, not a determination of the actual attack window. `Z` means UTC. Convert local Newfoundland time to UTC before using this form. Duration units are s/m/h/d, from one second through seven days. Absolute windows may be no more than seven days apart and must end in the past.

Optional area restriction, measured in a 3D sphere around the administrator's position when preview is requested, in that dimension only:

```
/rollback preview player <uid> 1d --inventories radius 100
```

`/rollback cancel` cancels the owning administrator's active query/preview or stops execution at the next batch. Applied changes remain applied. Preview expiry is ten minutes. One operation runs at a time; a new preview replaces the previous preview. Permission: `newfies.rollback`, registered using the existing Suite administrator convention. Ordinary players should not have it. These are in-game administrator commands, not console commands.

## Area and time rollback (no player filter)

Stand at the desired center. The radius is a **3D sphere** in blocks around that position, in your current dimension. Your position and the time window are captured once during preview; moving afterward does not move the recovery area.

```
/rollback preview area 100 2h
/rollback execute <token-shown-in-preview>
```

This selects everyone's logged changes inside the sphere during the last two hours. It also includes records with an empty actor UID. It does not require knowing who caused the damage. It also selects legitimate work within that window: inspect the preview report before executing. The command does not change the world during preview.

For older damage, specify the actual UTC window rather than a duration ending now:

```
/rollback preview area 100 from 2026-10-04T19:50:00Z to 2026-10-04T20:35:00Z --inventories
/rollback execute <token-shown-in-preview>
```

These dates are examples, not the confirmed attack times. Radius must be 1..4096; durations and absolute windows retain the seven-day maximum. `--replace-entities` is also supported, but area mode includes natural creature deaths in the selected region/window as well as player-caused deaths. It remains opt-in, and produces replacement creatures with default state.

Area mode reverses mixed-player block chains together and combines their recorded inventory deltas. Any logged activity after the chosen end at an affected location is a conflict and that location is skipped. A post-window container snapshot is also a conservative conflict in area mode. The player mode keeps its existing actor checks.

Other online players must leave the selected sphere before execution. Entry is checked again before each execution batch; a new entrant stops recovery, leaving completed operations journaled. The owning administrator can remain at the center. Normal live-change checks, loaded-chunk requirements, receipts, expiry, cancellation and recovery limits apply to both modes.

Area selection occurs in the database before the row caps: unaffected locations outside the sphere do not consume the history limits. Selected cells retain later history and earlier inventory baselines, so a narrow sphere can still exceed a cap if it has a large history. Preview reports and execution intents include the mode, saved center, radius and UTC window.

## What historical data can recover

| Data | Recovery |
|---|---|
| Old/new block codes | Restore a contiguous chain of logged target block changes to its original block code. More normal block classes are supported than Rollback1, including orientations encoded in the block code. |
| Legacy break record with removed block code | Restore that block when the resulting air state and live world agree. A legacy placement without an overwritten block code remains unsupported. |
| Attacker-placed stateful/chiseled block | Remove/replace it with the recorded original ordinary block where later activity and the live state permit. Current block-entity data is saved to the recovery journal before removal. |
| Broken original chiseled block | Not recoverable from a code alone: old voxel geometry/material data was not captured in this database format. No replacement cube is invented. |
| Existing container snapshots plus quantity deltas | With `--inventories`, reconstruct saved contents before the selected attack and verify the recorded final contents against the live inventory. A final snapshot is required for an existing container. |
| Destroyed basic storage container plus snapshot | With `--inventories`, restore supported chest/basket/trunk/generic-container block codes and recorded contents. This is partial recovery: reinforcement, locks and other original block-entity metadata were not logged. Machines and arbitrary stateful blocks are excluded. |
| Attributed creature death record | With `--replace-entities`, create a NEW creature of the recorded type near its logged rounded location. This is replacement, not resurrection of the old animal. |
| Player deaths, aggregate spawns/despawns, unowned entity events | Not recreated. The old state or reliable per-entity attribution is insufficient. |

### Inventory fidelity

Historical snapshots contain slot number, item code and quantity. They do NOT contain item-stack attributes or the item-vs-block class. Durability, freshness, temperature, names, contents of carried containers, custom mod attributes and similar state cannot be restored exactly. Codes that ambiguously resolve to both an item and a block are skipped.

Existing live stacks with a matching code retain their current attributes while quantity is adjusted. Missing stacks are rebuilt with default attributes. Items already taken, transferred or dropped are not located or confiscated, so this can create additional copies in the wider world. Use the preview report to decide whether restoring those saved contents is appropriate. The `--inventories` flag explicitly includes these partial recoveries; omitting it limits recovery to eligible block codes.

### Creature fidelity

Death rows contain type, original entity ID, actor attribution and rounded position. They do not contain the original animal's generation, ownership, name, training, pregnancy, exact pose or watched attributes. Replacements receive default attributes. Only creature/agent types are eligible, in the overworld, with their original entity/corpse no longer loaded and their logged position unobstructed. Up to 500 replacements are eligible per preview. A blocked location can be revisited after terrain recovery and previewed again.

## Conflict checks and execution

- Read target-related locations rather than all world activity. Reads retain later activity by anyone, so later legitimate edits are checked even outside the selected attack window.
- Reverse target block transitions newest first, using database ID to resolve identical timestamps. Collapse each location to its original-to-final transition.
- Earlier changes by another player can remain intact; other-player activity after the earliest selected target mutation makes that location a conflict. Previous rollback barriers and target activity after the selected end also block recovery.
- Read the latest pre-window container snapshot and intermediate pre-window deltas, so legitimate earlier inventory removals are not restored as though the attacker made them.
- Reject missing/corrupt snapshots, invalid quantities, negative balances, snapshot/delta disagreement, missing codes, stack overflows and incompatible live inventories. Late consistent target snapshots may verify final state, but a later shared or inconsistent snapshot is a conflict.
- Preserve current-world and dimension checks, including legacy `overworld`/`0` names.
- Player mode requires the target offline; area mode requires other players outside the selected sphere. Require affected chunks already loaded; the command does not generate/load world terrain. Visit affected areas, then preview again for skipped unloaded locations.
- Observe live block interactions and container slot modifications after preview; compare current block-entity bytes before execution. Direct mod changes that escape logging cannot be reconstructed retrospectively.
- Re-read history before execution and compare the reviewed plan fingerprints. In Perf3, durable intent and receipt writes run on a background worker before each game-thread mutation. Recheck occupancy/live changes after those writes, dispatch one operation at a time, and yield 10 ms between applied operations. Preview validation has a cooperative 4 ms/64-operation batch limit. Progress and cancellation remain supported.

## Reports, journal and duplicate protection

These files are created next to the logger database:

- `<database>.recovery-preview-<token>.json`: eligible operations, source event IDs, selected UID/window, fidelity labels and all skipped-reason counts.
- `<database>.recovery-<token>.jsonl`: journal intent before mutation, per-operation execution skip reasons, and applied/completed phases. It includes the current block-entity bytes before a destructive change and new IDs for replacement creatures.
- `<database>.recovery-receipts.jsonl`: persistent world-scoped attempt/completion receipts. Keep this with the database and world backups.

An intent receipt is durably flushed BEFORE each mutation. If a crash or error leaves its outcome uncertain, subsequent runs skip that receipt instead of risking duplicate item/creature creation. Do not delete receipts to retry blindly; inspect the journal and world backup first. A corrupt receipt file disables recovery commands without stopping the rest of the Suite. Previous Rollback1 audit barriers are still respected.

Restoration is not one transaction across the world. A crash, cancellation, incompatible container initialization or audit failure may leave a partial recovery; the journal and chat counts identify what was attempted/applied. There is no automatic undo command. Preserve the pre-recovery world backup.

## Remaining limits

There is no reliable recovery for unlogged WorldEdit/explosions/fire, missing old blocks, deleted claims, privilege changes, lost sign text, machine state, chisel geometry or exact killed-entity state from this schema. Individual source events may also have been dropped or logging disabled during the attack. A valid current-world block code does not prove that all old custom metadata has been preserved.

Affected-location reads cap at 200,000 event rows and 50,000 snapshots. Exceeding either cap fails instead of silently truncating a recovery. A very old attack can encounter these limits because conflict checks need later history too. Request narrower time windows and preserve the database. In player mode, radius filtering occurs after the history read and does not reduce these SQL caps. Area mode filters affected-cell selection in SQL before reading history.

The earlier uploaded `archive-2026-10-04T160652-0400.tar.gz` contains server logs, not the Suite SQLite database. This package was built against the source's actual schema, not inspected against the attack's live DB contents. To inspect the actual attack records, obtain the database path from `/blocklogger path`; use a consistent SQLite backup or stop the server and include any associated WAL with a raw copy. Do not copy only the main SQLite file while live WAL writes are outstanding.

## Validation

Passed:

- Production planner compiled and ran under .NET 10: multi-step and same-timestamp block chains, earlier/later actor conflicts, post-window actions, inventory baselines/deltas, mismatched or absent final snapshots, destroyed containers, pre-window legitimate removals, consistent/mismatched late snapshots, legacy breaks, unknown legacy placements, prior rollback barriers, explicit creature replacement, exclusion of players, and stable/world-scoped receipts.
- Production chat parser and ChatDisplay handler compiled and ran with narrow API doubles: raw/prefixed slash commands bypass moderation, trusted nickname/role prefixes, ordinary chat, intentional blocking, channel/data preservation, and exception fallback. These doubles do not validate game ABI.
- Additional planner regressions: net-zero container block chains still inspect contents, final snapshot requirement after block replacement, inventory identity disagreement, and pre-window creature death exclusion.
- Actual SQL query fixtures: exact-name/UID resolution, injection-shaped input, current-world isolation, tie-break ordering, later conflicts, killer attribution, legacy dimensions and pre-window baseline/intermediate rows.
- Area SQL fixtures: all actors and empty UID, natural deaths, exact spherical boundary/corner exclusion, other dimensions/worlds, saved baselines, empty areas and retained later conflicts.
- Production planner area regressions: mixed-player chains/deltas, later edits and snapshots, empty actors, recovery barriers and shared receipts.
- Production command handler compiled with API/storage doubles: valid and invalid syntax, absolute/relative times, saved center, player mode isolation, preview non-mutation, cancellation, boundary occupants, other dimensions, mixed-player execution batching and stopping on new entrants. The doubles do not prove game binary compatibility or actual world persistence.
- C# syntax parsing and archive/change-scope checks.

The complete Suite is compiled against the game's assemblies. Planner and command tests isolate world mutations using doubles; they do not prove persistence, real container initialization or network synchronization in a running world.

Reproduce:

```
dotnet run --project tests/RollbackRecoveryTests.csproj
dotnet run --project tests/chat/ChatCommandBoundaryTests.csproj
dotnet run --project tests/commands/RecoveryCommandTests.csproj
python3 tests/test_recovery_sql.py
```

API references checked:
https://apidocs.vintagestory.at/api/Vintagestory.API.Common.IBlockEntityContainer.html
https://apidocs.vintagestory.at/api/Vintagestory.API.Common.IBlockAccessor.html
https://apidocs.vintagestory.at/api/Vintagestory.API.Common.Entities.Entity.html

## Claimed land in Polish1

Claimed land is excluded by default from recovery. For an intentional claimed-area recovery, add `--include-claims`, for example `/rollback preview area 30 2h --include-claims`. The preview reports whether claims are included and how many chunks are affected. Current game access is checked again while executing; permissions changed after preview cause the affected operation to be skipped. Existing durable receipts and conflict checks remain in force.


## QueryFix1: single-block query timeouts

The previous query could traverse unrelated world history while checking a single selected cell. QueryFix1 starts from affected cells and seeks their event/snapshot records through the existing position indexes. It still includes pre-window inventory baselines, pre-window transfers needed for reconstruction and post-window edits/snapshots needed to refuse unsafe recovery. The row limits, one-query admission, cancellation, writer barrier and 30-second native work deadline remain enforced. This does not raise the timeout or relax recovery validation, and does not require a new index build on a large database.

A timeout does not indicate that rollback edited the world. Preview is read-only; execution re-reads history before applying changes. If a timeout still occurs with QueryFix1, retain the exact command and server logs so a genuinely busy selected cell, a slow disk or database contention can be investigated separately. This fix was measured against synthetic histories and native SQLite fixtures, not the production server database.
