# Suite 1.2.44 ModpackStability1 + NOS 0.6.0 preview

## Changes

- Suite automatic engine profiling is independently opt-in and off by default. It is still a full engine profile while active; reading its output every 100 ms does not remove the engine's per-tick instrumentation cost.
- Profiler attribution computes normalized marker text at most once per match and caches normalized catalog tokens. An old/new built-assembly comparison produces identical attribution signatures and character-normalization results, including Unicode and long markers.
- Cancelling an automatic capture leaves manual captures alone. Generation-tagged callbacks prevent an obsolete timer from finishing a later capture.
- Downloaded manifests accept case-insensitive field names, JSON comments, UTF-8 BOM and trailing commas as accepted by the game's loader. Ambiguous duplicate keys and dependencies, unsafe archives, wrong IDs and wrong versions remain rejected.
- The optional earlier-game-patch update policy queries only earlier stable patches of the same major/minor version through the official install index. It chooses the highest parseable recommended version, keeps the selected attachment paired with that version, validates dependencies before staging, and refuses downgrades. The option is off by default.
- NOS gains a separate guarded explicit-AI-task-request enumeration patch and Suite checkbox. No AI range, interval, priority, pathfinding, physics, terrain algorithm or save transaction is changed.

## Targeted validation

Built against official Vintage Story 1.22.7 / .NET 10. Suite remains 1.2.44; companion versions are unchanged. NOS is 0.6.0-preview.

The official-method AI test checks all-handler vetoes, task/previous-task arguments, successful cancellation/start events and active slots, null subscriptions, subscriber mutations, exceptions, invalid slots, disabled behavior and changed-IL rejection. Registry tests also check independent controls, foreign Harmony owners, late conflicts and cleanup.

The runtime Suite updater test checks manifest casing/BOM/comments, duplicate identity/dependencies, wrong ID/version, traversal, exact compatibility default, highest-version same-minor fallback, attachment pairing, foreign download hosts, retractions and default-off profiling. Optional real-package validation covers all 22 pack ZIPs. Live read/download-only checks resolved and validated Floral Zones Caribbean 1.0.29 → 1.0.30, Forgotten Armory Gothic 2.1.1 → 2.1.2 and Hussar 1.1.5 → 1.1.6; Hussar required the explicit earlier-patch option. These checks do not install or mutate production files.

Browser checks cover the new optimizer checkbox and live settings, older optimizer compatibility, read-only and Safe Mode locks, failed-save draft preservation, automatic capture configuration and mobile fit.

## Server comparison

See [NOS modpack measurements](../modules/optimizer/docs/performance-0.6.0.md) for final run data, method and limitations. Trials use a disposable stopped test-world copy and all six Suite mods, NOS and the listed third-party pack. No player authentication is bypassed and no production server/world is contacted or modified.

Forced-active and locally tracked NPCs, pass-through AI subscribers, explicit saves, capture windows and test GCs are laboratory controls only. No fixture is included in production mod ZIPs. Discord is loaded without a bot token; webpanel/world-map polling and online-player interactions are not exercised by the NPC workload.

Production lag and player capacity require captures on the owner's actual world and player workload. A ModDB game tag or a passing headless test is not proof of complete gameplay compatibility.

The headless fixture explicitly marks its tagged animals as locally tracked after the stock tracking calculation, so the ordinary physics worker moves them even without clients. It does not create client/session records, bypass authentication or send fake player traffic. This controlled physics/AI workload is not a multiplayer simulation. Setup trials without moving NPCs were rejected rather than counted as successful performance evidence.

Four measured runs are complete (switches off/on/on/off): allocation was about 15% lower with NOS defaults in this fixture, but tick tails and process RSS did not improve. The current profiler's paired capture windows added measurable work, supporting the default-off setting. No general performance or production-memory claim follows. The report retains the first-run terminal-check correction and fourth-launch environment interruption, including unchanged original measurements.
