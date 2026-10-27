# NOS 0.20.16-preview audit

## Fixed: incomplete asset-cache installation rollback

`Activate` installed the Harmony prefix/postfix, then initialized asset tracking, and only afterwards marked the entry installed. A failure reading server world/assets in that interval left the entry marked failed without removing its own patches. The disabled gate limited the impact, but the cleanup contract was wrong.

Ownership is now recorded immediately after Harmony installation succeeds. Subsequent initialization failure uses the normal per-entry rollback, leaving unrelated modules active and removing only this entry's prefix/postfix. A regression test injects a failing API World accessor: it fails on 0.20.15 and passes with this fix.

## Fixed: late alternative optimizer/runtime detection

The startup guard rejected known Stratum, Synergy and Tungsten assemblies, but the repeating monitor only inspected patch owners and failures. A known incompatible optimizer loaded later could escape the assembly-name guard if it did not overlap an inspected target.

A loader event now records known alternative runtime names. The existing once-per-second server monitor then disables and removes active NOS modules and blocks standby modules. Loader callbacks do not perform Harmony changes. Subscribing followed by one assembly scan closes the subscription gap; no recurring assembly-array scan was added. Disposal removes the event subscription. Startup blocks and foreign patch detection still apply independently.

This uses known assembly names; it is a compatibility guard, not a way to identify every renamed or unknown optimizer. Stand-down occurs on the next monitor callback, so it is not instantaneous. Do not stack these optimizers intentionally.

## Performance audit findings

- The all-modules compatibility monitor measured around 0.17 ms per check in this audit container. It runs once per second. Its Harmony metadata inspection still allocates; disabling guards to save those allocations is not justified by the supplied multi-hundred-ms stalls.
- Reviewed entity/query/crafting scratch ownership clears entity, filter and recipe references on completion and bounds retained capacity in the existing implementations. This review is not a whole-heap leak proof or a full-modpack compatibility certification.
- The supplied tick excerpt points to native queued block simulation as the largest measured contributor in 10 of 14 detailed ticks, including 1321.80 ms inside a 1407.49 ms tick. Current optional simulation budgeting covers this native loop. Further CPU reduction needs the costly callback block code/type and algorithm, not another blanket AI throttle. A soft budget cannot interrupt one long callback or eliminate sustained queue overload.
- Synchronous map-region saving accounts for 607.93 ms in one supplied save tick. Completed-save snapshot release targets retained references, not that region-save stage. Region serialization/compression/database timing should precede invasive save scheduling changes.
- Barrel callbacks reach 201.25 ms for 1257 invocations. Existing optional failed-recipe memoization is the relevant candidate, with its documented delayed-rescan tradeoff. Wild fruiting bush callbacks also reach 99.41 ms; allocation reduction does not remove actual growth calculations.
- Saving unchanged Suite settings currently rebuilds optimizer patches. A future no-op path must preserve explicit retries of failed/conflicted modules, detect removed patches and leave profiling/persistence semantics intact; blindly skipping every identical apply would break recovery behavior.

No new scheduling policies, forced collections, world-format changes or default-on experimental modules were added. Suite and companion versions are unchanged.

## Validation

The native Harmony module suite now contains 168 checks, including injected initialization failure, isolation of other modules, late-runtime stand-down of active and standby modules, and subscription disposal. Full optimizer build/test results are included with the release package. These checks and synthetic component timings are not production TPS or player-capacity claims.
