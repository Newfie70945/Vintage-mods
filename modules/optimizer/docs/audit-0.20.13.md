# Code audit: Newfie's Server Optimizer 0.20.13-preview

Scope: everything under `src/NewfiesOptimizer` (registry, Suite bridge, profiler, export, and all 33 patch modules), checked against the config, the build script and the existing test harnesses.

**Limits.** The audit container had no .NET SDK and no official Vintage Story 1.22.7 binaries. Nothing was compiled, none of the 34 test projects were run, and no assumption about the shape of the stock IL was checked by decompiling the game. The findings come from reading the code. Where a finding depends on stock engine behaviour, it says so.

## Overall assessment

The design is careful and consistent. Every module:

- has its own switch;
- is guarded by an assembly hash, a contract check and a foreign-owner check;
- keeps state as a per-thread frame stack with epoch invalidation, so nested and disabled calls get a barrier;
- caps how much it retains;
- never replays partially completed stock work after an exception.

The once-per-second monitor, the hysteresis on nearby-player queries and the "remove only our own patch methods" unpatching are all sound. Suite input ranges match `OptimizerConfig.Normalize` for every numeric setting.

No crash-class or data-corruption defect was found. The items below are ranked by impact.

## Findings

### 1. Medium: disposal and Suite "Save and Apply" unpatch methods that were never patched

`EngineOptimizations.Dispose` (`EngineOptimizations.cs:515`) calls `Disable` on **every** entry. `Disable` (`EngineOptimizations.cs:446-447`) then calls `harmony.Unpatch(entry.TargetMethod, method)` whether or not the entry was ever installed. That covers entries that are `disabled`, `blocked` (SafeMode, master switch off, guard failed), `unsupported` or `conflict`.

In Harmony 2.x, `Unpatch(original, patch)` on a method with no patch info builds a fresh empty `PatchInfo`, regenerates the method through `UpdateWrapper` and installs the detour. The result:

- With default settings, 44 of the 60 registry entries are not installed, so roughly 40 engine methods are rewritten into empty Harmony wrappers on every server shutdown. Every Suite **Save and Apply** does the same, because `SuiteControl.cs:197` disposes and rebuilds the registry. That happens synchronously on the game thread.
- With `Compatibility.SafeMode=true` or `Enabled=false`, a Suite apply still detours every target. This contradicts "SafeMode … stock methods retained". `Harmony.GetAllPatchedMethods()` now lists these methods, which other mods and diagnostics will see.
- The existing test (`tests/EngineModuleTests/Program.cs:88`) only checks that no patch *owned by NOS* remains, so it cannot catch this.

**Suggested fix:** in `Disable`, only unpatch when `entry.Installed` is true. Add a test asserting that `Harmony.GetPatchInfo(target)` stays `null` for entries that were never installed, after both `Dispose` and a reapply.

### 2. Medium-low: the runtime engine guard does not verify VSSurvivalMod.dll

`tools/build.py` checks the hash of `Mods/VSSurvivalMod.dll`, but `EngineGuard` (`EngineOptimizations.cs:290-293`) only checks VintagestoryAPI, VintagestoryLib and VSEssentials. Several modules patch VSSurvivalMod types:

- `idleFirepitFastPath`
- `barrelIngredientPairing` and `barrelMatchCountGuard`
- `wildBushCallbackFlatten`
- `temperatureAnimationChecks`
- possibly the `InWorldContainer` traversals

The block-tick modules partly make up for this by hashing their target method bodies. They do not hash the dependencies their reasoning relies on, such as `enviromentTemperature`, `canSmelt` and `MatchStackToIngredient`. `temperatureAnimationChecks` relies on an IL call count alone. If a hotfixed VSSurvivalMod sits next to an unchanged core, those modules still install.

**Suggested fix:** check the VSSurvivalMod hash before any of these modules installs, and leave the core modules unaffected.

### 3. Low: static engine lookups run before the version guard

Some module types resolve engine members in static initializers:

- `BlockTickOptimizations.Match` and `MatchIngredient` (`BlockTickOptimizations.cs:17-18`)
- `AiTaskStartOptimizations.Filters` (`:14`)
- `EmotionSearchOptimizations` `Owner`, `Partition` and `Query` (`:19-26`)
- `RepulseDelegateOptimizations` `Walk` and `Range` (`EntityHotPathOptimizations.cs:52-53`)

The registry constructor calls `Configure(false)` on these types before `EngineGuard` runs. VS treats `"game": "1.22.7"` as a minimum version, so the mod also loads on later builds. If one of these members is missing there, the whole registry fails with `TypeInitializationException` and the log says "Engine modules unavailable". The clear per-module message "Unsupported game version" is never shown.

This is not a crash. `WorldgenRequestOptimizations` already avoids the problem with a lazily initialized nested `Accessors` class. Applying the same pattern to the other modules would fix it.

### 4. Low: exported profiles report the wrong optimizer version

`ProfileReport.OptimizerVersion` is hard-coded to `"0.9.0"` (`ProfileData.cs:46`). Every exported `profile-*.json` and `latest.json` therefore says it came from 0.9.0. The repo's performance documents are built from these exports, so the evidence is mislabelled.

**Suggested fix:** use the assembly's informational version, as `RuntimeProfiler.Status()` already does.

### 5. Low: the profiler treats NOS's own engine patches as foreign

The profiler's Harmony ID (`newfies.serveroptimizer.profiling.0.9.0`) is different from the engine's. `InstallHook` (`RuntimeProfiler.cs:196-199`) counts any other owner as a conflict. Two overlaps follow:

- `phaseTimings` hooks `ServerSystemEntitySimulation.TickEntities` (`EngineHookTargets.cs:27`), which `entityTickCleanupLists` also patches.
- `systemTimings` hooks every `OnServerTick`, including `ServerSystemBlockSimulation.OnServerTick`, which `blockSimulationBudget` patches.

When either of those modules is active, the matching profiler group switches itself off entirely and reports a "conflict" with `newfies.serveroptimizer.engine.0.20.13`. Detailed timings are therefore unavailable exactly when you want to measure those modules.

**Suggested fix:** treat `EngineOptimizations.HarmonyId` as a known owner in the profiler.

### 6. Low: path assistance reads the shutdown flag only once

`PathQueueOptimizations.MainPrefix` takes `bool ___isShuttingDown` by value (`PathQueueOptimizations.cs:63`), so the loop condition at `:77` never sees a change made during the loop. The loop is bounded (at most 8 searches and the time budget), so the effect is negligible. Taking the flag as `ref bool` would make the check work as intended.

### 7. Low, needs verification: the collision-generation replacement ignores `dimension`

`CollisionGenerationOptimizations.Run` reimplements `GenerateCollisionBoxList` for both `CollisionTester` and the `CachingCollisionTester` override, and drops the `int dimension` parameter (`CollisionGenerationOptimizations.cs:57-59`). The contract check only covers the signature and a single `WalkBlocks` call. There is no IL hash, so it does not prove the two stock bodies are identical.

Confirm against the decompiled 1.22.7 source that neither stock body uses `dimension` (for example to set `minPos.dimension`) and that the caching override adds no state. Otherwise collision boxes may be wrong in mini-dimensions. The module is off by default.

### 8. Info: `MainThreadCpu` on older libc

`ProcessPrefix` P/Invokes `libc!gettid` (`RuntimeProfiler.cs:396,477`). glibc older than 2.30, and some musl builds, do not export it. In that case the call throws on every tick, the thread ID never gets set, and `tickMetrics` is marked failed even though tick capture still works. This only matters with the opt-in `MainThreadCpu` setting. A fallback to `syscall(SYS_gettid)`, or caching the failure, would avoid it.

### 9. Info: design choices worth knowing

- **Shared failure counters.** The two lifecycle-list entries share one failure counter, the four distance-policy entries share another, and so do the two waypoint entries. One failure stands down all of its siblings. This is conservative and intended.
- **Broad finalizers.** Finalizers on broad targets (`AnimationManager.OnServerTick`, `TickEntities`, emotion search, temperature) count *any* exception that escapes the native body as a module failure. That includes exceptions from other mods' code. An unrelated mod that throws can therefore switch off these modules. This is safe but can be noisy.
- **No authentication on the Suite event.** The optimizer does not authenticate the Suite control event (`newfiesse:optimizer-control-v1`). Any in-process mod can change settings. That is acceptable, because in-process mods are already fully trusted. Note that "authenticated" in the comment at `SuiteControl.cs:111` refers to Suite's own checks.

## Fix status

The branch was subsequently reviewed and compiled against the official Vintage Story 1.22.7 SDK on .NET 10. Engine lifecycle/conflict, path-queue and profiler regression tests pass. A dedicated observer test verifies that NOS profiling ownership does not block engine patches or get removed during engine disposal. These are component/contract checks, not a multiplayer capacity benchmark.

| # | Status | Change |
| --- | --- | --- |
| 1 | Fixed | `Disable` only unpatches installed entries. `EngineModuleTests` now asserts that no method is patched after disposing blocked or disabled registries. |
| 2 | Fixed | Before installing, any entry whose target or dependencies live in VSSurvivalMod is checked against the official hash (`d67b48a3…`, the same value `build.py` uses). If the hash differs, the entry is `blocked`. Core modules are unaffected. |
| 3 | Fixed | The lookups in BlockTick, AiTaskStart, EmotionSearch and RepulseDelegate moved to lazily initialized `Native` holders or properties, so `Configure(false)` no longer resolves engine members. |
| 4 | Fixed | `ProfileReport.OptimizerVersion` now comes from the assembly version. |
| 5 | Fixed | The profiler and the engine registry now recognise each other's Harmony IDs. Timing observers can therefore wrap active engine modules, and engine modules do not stand down because a profiler observer is present. |
| 6 | Fixed | `MainPrefix` takes `ref bool ___isShuttingDown`. |
| 7 | Verified for official 1.22.7 | Both inspected native `GenerateCollisionBoxList` bodies leave the `dimension` argument unused; dimensions are set on the owner positions elsewhere. The caching override uses the same bounds/walk behavior. No collision algorithm or defaults were changed. |
| 8 | Fixed | A failed thread-ID lookup is cached as `-1`. It reports `mainThreadCpu: failed` and is not retried on every tick. |
