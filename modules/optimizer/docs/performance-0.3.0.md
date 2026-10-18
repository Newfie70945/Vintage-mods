# NOS 0.3.0 validation

Official Vintage Story 1.22.7, .NET 10, Linux. No authenticated clients or production player workload was available. These checks establish component behavior and Suite integration, not server capacity.

## Compression component

Actual official compression methods with Harmony prefix/postfix installed, called through a delegate to prevent pre-patch JIT inlining from bypassing the patch. Tests explicitly require a cache hit. Five alternating-order trials, 4,000 measured calls per mode per scenario after warmup.

| Synthetic payload | Median stock compression | Median eligible cache hit |
| --- | ---: | ---: |
| Sparse-byte 128 KiB | 0.320 ms | 0.00475 ms |
| Repeated JSON, 1 MiB | 0.0870 ms | 0.0256 ms |

These are repeated warm-cache component calls, not joins, ticks or TPS. First misses still perform stock compression and copy source/result bytes. An earlier mixed-data experiment showed regressions for weak compression, so the final module bypasses cache probes for a buffer after an over-budget or less-than-25%-size-reduction result. Buffer replacement or reapplying settings allows a new probe.

Raw measurements: [asset-cache-measurements.json](research/components-0.3.0/asset-cache-measurements.json).

## Correctness and integration

- Official compression output, decompression, source changes, independent framing bytes, changed lengths, untracked buffers, over-size buffers, low-compressibility bypass, disable cleanup and parallel callers tested.
- Independent master/safe-mode switches, target ownership conflicts, compatibility monitor failure, late patch removal and disposal tested against actual Harmony methods.
- Browser checks cover draft preservation on refresh/save failure, settings application, module/conflict text escaping, capture locks, Suite Safe Diagnostics Mode, Read Only accounts, absent optimizer and mobile layout.
- A test-only mod calls the actual Suite bridge in a disposable official server loaded with both shipping mods: master-off, safe mode, invalid values/keys, restoration, capture start/stop and persistence checks. It is excluded from downloadable mod ZIPs.
- Startup, save, clean shutdown and save integrity checked. No production TPS improvement is claimed.
