# NOS 0.20.14-preview — reviewed compatibility and capture tools

This is an unpublished experimental preview for the official Vintage Story 1.22.7 server. There is no verified public Mod DB optimizer release to use as a changelog baseline. Existing independent module defaults are unchanged.

## Runtime corrections

- Remove only Harmony hooks this optimizer installed, preserving unrelated stock methods and observer patches during shutdown/reconfiguration.
- Check the official Survival module identity before dependent patches bind. Defer guarded reflection/delegate resolution so incompatible targets can fall back safely.
- Recognize NOS engine/profiler and Suite Harmony owners for observational timings; unknown owner conflicts remain visible.
- Use the loaded assembly version in diagnostics, observe shutdown by reference, and cache an unavailable native thread-ID lookup.
- During an explicitly started capture with `Profiling.PhaseTimings=true` and Safe Mode off, time main-thread chunk-column completion, chunk unload and entity-despawn send. Each available target is attempted independently. These are inclusive observations, not optimizations of chunk I/O or world generation.

## Capture tools

Python 3 scripts are supplied in the reviewed download's `tools/optimizer` folder and the source repository's `modules/optimizer/tools` directory. They operate offline on exported JSON and do not change a server or world.

```
python3 profile_findings.py latest.json
python3 compare_captures.py --baseline off1.json off2.json --candidate on1.json on2.json
```

The analyzer ranks measured work, GC windows and module errors. It cannot establish a specific mod as the cause of an individual stall without aligned evidence. A/B comparisons reject short/incomparable captures or missing workload counts; GC deltas are normalized using actual sample intervals. Overwritten history is treated as a retained window rather than a complete capture. Missing GC samples remain unavailable. Adjacent samples are correlated; statistical output does not prove a production performance gain.

The proposed Suite burst timer, live log reader, process-GC-to-tick attribution and public translocator layer were withheld after review. See the repository's `docs/validation/CLAUDE-RELEASE-REVIEW-1.2.52.md`.
