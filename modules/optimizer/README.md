# Newfie's Server Optimizer

**0.1.0 engineering preview — Vintage Story 1.22.7 / .NET 10**

A separate server-side profiling mod and reproducible multiplayer test toolkit.
Ordinary players need no client mod. This preview observes the official server;
it does not replace or rewrite server binaries.

The previous NOS development source was lost with the earlier workspace. This
is a new implementation, not a recovered or validated continuation of NOS 0.6.0.
The public Essentials Suite baseline remains **1.2.43**, unchanged.

## Install the profiler on a test server

1. Back up the test world's save and configuration using a stopped server.
2. Put `NewfiesServerOptimizer-0.1.0-preview.zip` in that server's `Mods` folder.
3. Start the official 1.22.7 server. Inspect its startup diagnostics.
4. As an administrator, use `/optimizer status`, `/optimizer profile start`,
   run a representative workload, then `/optimizer profile stop` and
   `/optimizer report`. See the configuration guide for automatic capture and
   export settings.

The mod writes bounded diagnostic data under the server's data directory. It
does not expose a public web listener. Review configuration and limitations
before enabling optional hooks, session evidence or longer captures.

## Build from source

Install the .NET 10 SDK and obtain the official 1.22.7 server separately. Game
binaries and game assets are not redistributed in this repository or package.

```sh
python3 tools/build.py --game-dir /path/to/vintagestory-server --dotnet dotnet
```

This builds the mod, runs its executable regression tests and the Python tool
tests, then creates the mod ZIP and a dependency-hash manifest in `artifacts/`.
On Windows use `py` instead of `python3` where appropriate; the official-client
automation backend currently targets Linux X11.

## Test tools

- `tools/stress-tester/`: automation of actual, authenticated official client
  windows, seeded action schedules and separate server-evidence accounting.
- `tools/benchmark/`: controlled trial planning, world/config identity checks,
  per-run records and report generation.
- `tools/dashboard/`: a separate local dashboard for exported measurements.

Read each tool's guide before running it. A key press does not prove a block was
mined or a transaction succeeded. Client connection and action evidence must
be collected and checked separately. Dry-run tests are tooling tests only.

## What this release demonstrates

The delivered validation report records builds, automated tests and isolated
local-server checks. It separates observed measurements from missing workloads.
**No authenticated multiplayer sessions were available here. No multiplayer
speedup, higher player limit or production compatibility is claimed.**

The requested development sequence requires real workload profiling before
retaining engine optimizations. AI scheduling, world-generation replacement,
chunk-I/O rewrites, network coalescing and incremental autosave remain deferred
until their value and correctness can be demonstrated. Their absence is
intentional; this preview does not implement arbitrary tick throttles.

See `docs/implementation-status.md`, `docs/engine-audit.md`,
`docs/configuration.md`, `docs/client-feasibility.md`,
`docs/stress-testing.md`, `docs/benchmark-methodology.md` and
`docs/limitations.md`.

