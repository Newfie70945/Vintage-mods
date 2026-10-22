# Incident evidence — Suite 1.2.45

Build: `ms11-incident-evidence1`. Official build target: Vintage Story 1.22.7 / .NET 10.

## Behavior

Performance incidents now record the runtime's cumulative GC pause-duration delta alongside collection counts. A falling heap can indicate collection, but neither the graph nor a Gen2 count establishes the pause's contribution. Pause scoring is reduced when it is small relative to the stall. Samples are interval measurements, not exact event traces; markers are observed when the sampling callback resumes.

The latest completed engine capture retains up to 60 slow ticks. Incident matching compares observed tick intervals with the incident sampling interval and retains at most eight matching ticks. Leading marker work and likely owner are reported as contributors with separate ownership confidence. Profiling precision, overlapping work and uninstrumented code prevent claims of sole causation. An automatic capture started after a spike is explicitly labelled as subsequent work.

Archived incidents are enriched after captures complete, without replacing measured evidence with unrelated profiles. Legacy assessments are recalculated once from available evidence and retain their prior label. Old records without GC pause timing display Not recorded. Missing historical measurements cannot be recovered.

Rolling database latency remains context, not causal evidence. Web request waits and queue time are distinguished from actual main-thread work. Insufficient evidence is an expected result when no capture covers a spike.

No additional always-on profile, sampling timer, database query or automatic profiling opt-in was introduced. Existing capture limits and cooldowns remain. Incidental Gen2 counts no longer suppress an otherwise eligible unknown-stall capture. Open Tick Debugging and capture during recurring slowdown to obtain executing-code evidence; viewing the page does not start profiling.

## Validation

- Official SDK build: zero warnings and errors.
- Pure matcher and GC weighting checks: 14 passed.
- Shipping-assembly incident archive/scoring checks: 15 passed, including late capture enrichment, legacy reassessment, unrelated-profile retention, wait-only requests and global database latency.
- Chromium evidence UI checks: passed, including escaped markers, mobile layout, historical timing availability and after-spike/running-capture states.
- Tick contributor and performance timeline browser regression checks: passed.

These validate attribution behavior, not production TPS improvements or proof of a memory leak. No live player workload or allocation trace was collected in this pass. All preceding 1.2.45 changes are retained; companion modules are unchanged.
