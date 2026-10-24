# NOS 0.20.1 preview validation

The full build and validation manifest records the executed automated suites against the inspected official Vintage Story 1.22.7 assemblies. A new native-versus-patched barrel recipe mutation regression reproduces the pre-fix mismatch and passes with the fix. No optimizer scheduling defaults changed.

Six dedicated-server comparison arms ran with NOS 0.20.0, the recorded capture switches, Suite 1.2.49 and 25 mod ZIPs. All passed save and SQLite integrity checks. Four arms used 128 active native NPCs; two exploratory arms used 1,024. These are not authenticated player workloads and do not reproduce the full production pack. The narrow 0.20.1 compatibility fix was made after those timed comparisons; their timings are not relabelled as 0.20.1 measurements.

Measured allocation fell, but tick latency and CPU did not improve. No general smoother-server claim is supported. See [measurements and limitations](performance-0.20.1.md). Production foreign Harmony conflicts remain blocked; those guards were not bypassed.

Raw uploaded logs and private configuration are not included in source or release archives.
