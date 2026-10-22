# SpikeDetails2 validation

Suite 1.2.45, build ms11-spike-details2. Clicked spikes show the strongest ranked explanation and its supporting measurements, followed by bounded per-tick contributor details. Capture retains six leaf contributors per slow tick, up to 60 slow ticks per capture and eight matching ticks per incident. Ownership resolution remains deferred until capture completion. No always-on capture or new timer is introduced.

Full marker paths, call counts and measured work are retained in structured form. Total leaf-marker work and the nonnegative difference from tick duration describe instrumentation coverage. Nested/overlapping measurements can exceed wall time; neither leaf work nor GC interval totals are additive causal percentages. Old captures lack this detail and display that limitation.

A small marker no longer receives maximum attribution weight solely because its tick overlaps an incident. Existing archived rankings are reassessed once. Existing after-spike labels, save overlap, GC pause measurements and unknown-evidence handling remain.

Validation: official 1.22.7 SDK build succeeded with zero warnings/errors; 14 matcher checks, 16 shipping-assembly attribution checks (including tiny-marker versus significant GC pause), Chromium detail/mobile/escaping checks. This tests diagnostic correctness, not production performance or exact causal proof. Missing historical profiles cannot be reconstructed.
