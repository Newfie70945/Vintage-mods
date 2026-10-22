# EvidenceQuality2 — Suite 1.2.46

This pass separates measured work/GC pause activity from pressure counters and temporal overlap. Correlation scores are bounded below strong measured work; incidental queue, entity, CPU or save signals cannot be presented as direct measurements of blocking code. Assessments explicitly distinguish one measured contributor, multiple measured contributors, correlation only and insufficient evidence.

Work weights scale to the stall duration, with absolute lower bounds. A 20ms marker cannot explain a 5s stall. Weak owner candidates are suppressed; ownership remains separate from causation. Measured GC pauses still do not identify allocating code. Save overlap still cannot distinguish serialization, disk writes or event listeners.

Clicked spikes show evidence gaps: no overlapping capture, unavailable historical pause timing, incomplete leaf-marker coverage and save subphase ambiguity. Repeated marker counts and maxima are limited to the retained eight slow ticks and six contributors per tick; duplicate paths within one tick count once. Counts are not workload-wide frequency or proof of a shared cause. Copied summaries include the assessment and evidence gaps.

Existing archived verdicts are reassessed once. No historical data is fabricated, new capture enabled, extra polling introduced or scan run. These changes use existing bounded measurements. GC totals and leaf times may overlap and must not be summed into causal percentages.

Validation: official 1.22.7/.NET 10 build; 17 matcher/weight checks; 22 shipping-assembly attribution checks including mixed contributors, pressure-only and save-only evidence, missing evidence and small-marker/large-stall cases; Chromium detail/escaping/repeated-marker checks, navigation/race checks and timeline regression. No production player workload or allocation trace was captured; no performance or exact-causality claim is made.
