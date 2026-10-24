# NOS 0.20.3 preview — single-subscriber AI dispatch

AI filter and task-start veto helpers now directly invoke an immutable local delegate when it has a single target. This removes invocation-list enumeration on that path. Multi-subscriber delegates retain the non-short-circuit loop: every subscriber runs in order even after a veto. Null checks, callback arguments, exceptions and disabled-module behavior remain unchanged. There is no subscriber/result cache, reduced AI frequency or skipped task execution.

The existing independent AiFilterDispatch and AiTaskStartDispatch controls apply. No new defaults or Suite protocol changes are required. Existing foreign-Harmony checks and lifecycle controls remain in place.

## Focused official-method measurements

Each run invokes the actual official method through its Harmony patch with a synthetic subscriber. Five warmed trials were taken before and after the candidate change in separate processes on the same two-core-quota environment. Reported medians include helper/target dispatch overhead and vary with JIT and scheduling.

| One subscriber | Previous NOS median | Candidate median |
|---|---:|---:|
| AI filter | 64.20 ns/call | 35.57 ns/call |
| Task-start veto | 60.81 ns/call | 32.43 ns/call |

These are narrow method timings, not an estimated server-wide improvement. The existing implementation already avoids invocation-list array allocation; this change targets CPU work and retains effectively zero allocation on the eligible path. No new 3,000-animal or authenticated-client benchmark was run for 0.20.3, and no production TPS or player-capacity claim is made.

Tests compare native and optimized single-subscriber replacement during dispatch, first/next-call return values and callback ordering. Existing cases cover multiple subscribers, subscription removal, vetoes, exceptions, disabled helpers and actual task transitions. The full validation manifest records all executed suites.

[Raw focused measurements](research/single-ai-0.20.3/measurements.json). Previous mixed-server results and their limitations remain in [0.20.2](performance-0.20.2.md); those measurements used 0.20.1, not this build.

## Same-process tradeoff check

A separate alternating helper fixture with tiered compilation disabled compared the prior enumeration loop against the new helper in the same process (seven trials per subscriber count). Single-filter medians were 8.39 versus 4.60 ns; single-task-start medians were 9.09 versus 5.05 ns. With 2/8/32 subscribers, the new branch added approximately 2–12 ns per invocation in these fixtures. This is a real tradeoff, not a blanket improvement for every subscriber layout. The reference loop is an isolated helper, not a second official server method.

[Controlled trial data](research/single-ai-0.20.3/controlled.json). Both existing module switches can still disable the corresponding replacement. No production subscriber distribution was measured. All 29 automated validation suites passed, including the new native-versus-patched single-subscription mutation checks.
