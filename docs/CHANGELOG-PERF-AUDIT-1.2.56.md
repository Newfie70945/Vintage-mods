# Suite 1.2.56 / NOS 0.20.22-preview

- Reduce Log Intelligence's temporary allocations by reusing cleared read/decode
  buffers and constructing emitted text directly in one final string.
- Keep the 256 KiB per-pass read ceiling, UTF-8 continuation, incomplete lines,
  exception-context carry and same-path log replacement detection.
- Add a common one-chunk OreShield sync path that avoids temporary grouping and
  range-copy allocations. Keep independently owned packet/retry arrays, 128-cell
  caps, authoritative masking and registration-before-send for synchronous ACKs.
- Retain the original mixed-chunk grouping path and all detection/reveal settings.
- Extend NOS's exact-binary compatibility entry to Suite 1.2.56. NOS optimization
  behavior and defaults are unchanged; other companion versions are unchanged.
- Add compiled before/after allocation fixtures, native sync boundary/dimension/
  masking tests and a performance audit with raw measurement results.

Measured log-reader allocation falls about 53% in the supplied 64/256 KiB fixtures;
the same-chunk 64-cell sync fixture saves 2,120 bytes per call. These figures do
not establish production TPS gains or reduced retained server RAM. Read
[the performance audit](PERFORMANCE-AUDIT-1.2.56.md) for scope and tradeoffs.
