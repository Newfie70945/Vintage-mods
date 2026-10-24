# NOS 0.20.9 / Suite 1.2.50 verification-worker pass

## Changes

Suite's Mod DB verification completion no longer serializes or writes its cache on the game thread. One coalesced background writer persists updates at five-second intervals using a temporary file and replacement. Failed writes preserve the last good cache and dirty state; later passes retry. Shutdown cancels new publication before a final serialized flush. Retention diagnostics expose pending updates, writer activity, last successful save and write errors.

Affected Anti-Cheat sessions refresh one per delayed callback, with one batch chain active at a time. Results arriving during a pass get a subsequent pass. Policy evaluation and consequences remain unchanged. A single session evaluation remains indivisible; this does not guarantee a strict millisecond cap.

NOS's opt-in `BarrelMatchCountGuard` now supports the audited Steep & Brew 0.7.1 `JarOnlyBarrelRecipePatch.Postfix`. That postfix only turns successful tea/decoction matches into failures; it still runs when Harmony skips the original. Compatibility requires the exact audited assembly SHA-256, owner, method and type, and only that foreign postfix. Foreign prefixes/transpilers/finalizers, other postfixes, unknown builds and patched dependencies remain conflicts. No foreign patch is removed. The guard's existing native-slot/recipe fallbacks remain.

## Scope and limits

The uploaded log establishes Suite verification callback stalls and barrel work, but does not identify exclusive CPU causes for every XSkills/body-temperature spike. This pass does not throttle those gameplay calculations, change crafting batch sizes, adjust XP, alter world-save data or claim to resolve every stall. Validate changes with comparable real-player captures. Automated fixtures and component measurements are not production TPS evidence.

The exact Steep & Brew DLL was downloaded from its public Mod DB release for private compatibility testing. Third-party binaries and decompiled sources are not distributed.
