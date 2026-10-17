# DEVELOPMENT PROMPT: NEWFIE'S SERVER OPTIMIZER
## Vintage Story 1.22.7 — Advanced Server Performance Optimization & Real Bot Stress Testing

### PROJECT OBJECTIVE

Develop a complete, production-quality, server-side optimization mod named **Newfie's Server Optimizer** for Vintage Story 1.22.7.

The primary objective is to dramatically improve multiplayer server performance, increase sustainable player capacity, reduce CPU and memory pressure, eliminate unnecessary processing, improve entity and chunk performance, and minimize server lag spikes.

This project must include a separate, fully functional automated stress-testing framework capable of connecting multiple real Vintage Story clients or protocol-compatible simulated players to a dedicated test server.

**Do not make theoretical performance claims. Every optimization must be benchmarked against an unmodified baseline and validated under realistic multiplayer workloads.**

## 1. CORE DEVELOPMENT REQUIREMENTS

- Target Vintage Story 1.22.7.
- Use C# and the appropriate .NET runtime.
- Reference the official Vintage Story API: https://apidocs.vintagestory.at/
- Build as a server-side mod wherever possible.
- Require no client mod for ordinary players.
- Maintain vanilla multiplayer protocol compatibility.
- Preserve existing gameplay mechanics and world persistence.
- Ensure compatibility with heavily modded servers.
- Avoid unnecessary Harmony patches.
- Never invent API methods or assume engine internals without inspecting the target-version assemblies.
- Deliver a complete compilable mod, configuration files, source code, documentation, and benchmark tools.

Evaluate engine-level patches only when profiling proves they are necessary and the implementation is verifiably safe.

## 2. SERVER PERFORMANCE PROFILER

Create an integrated low-overhead profiler capable of identifying exactly what is consuming server resources.

Measure:

- Server tick duration and effective TPS
- Main game-thread utilization
- CPU usage by process and thread
- Managed heap and working-set memory
- Garbage collection frequency and pause duration
- Entity processing costs
- Entity AI and pathfinding costs
- Loaded chunks and chunk-processing time
- Chunk generation and loading times
- Block entity processing
- Network throughput and packet processing
- Player synchronization overhead
- Autosave duration and disk I/O
- Event-handler and registered callback costs
- Performance of identifiable third-party mod systems

Calculate average, median, P95, P99, and worst-case measurements where meaningful.

Use sampling, bounded buffers, and adjustable profiling intervals to prevent the profiler itself from becoming a significant source of lag.

Include commands:

`/optimizer status`

`/optimizer profile start`

`/optimizer profile stop`

`/optimizer report`

`/optimizer hotspots`

`/optimizer entities`

`/optimizer chunks`

`/optimizer network`

`/optimizer gc`

`/optimizer benchmark`

Provide JSON and CSV exports, plus an optional standalone HTML report.

Measure profiler overhead with instrumentation enabled and disabled.

## 3. ENTITY AND MOB OPTIMIZATION

Investigate entity ticking, AI updates, pathfinding, targeting, movement, collisions, and synchronization.

Implement configurable optimizations where safe:

- Reduce redundant entity calculations.
- Cache repeated queries with correct invalidation.
- Spread non-critical AI updates across ticks.
- Avoid synchronized spikes from large groups of entities.
- Use distance-aware scheduling for eligible AI tasks.
- Reduce unnecessary path recalculation.
- Reuse allocations and temporary buffers where appropriate.
- Optimize repeated entity discovery and proximity checks.
- Investigate distant, inactive entities and unnecessary update work.
- Optimize large groups of Drifters, Bowtorns, and modded creatures.

Do not incorrectly freeze mobs, break combat, delay critical damage calculations, or create exploitable differences between players.

Critical entity behavior must remain responsive.

All AI optimizations must be independently configurable and benchmarked.

## 4. CHUNK AND WORLD OPTIMIZATION

Investigate:

- Chunk generation
- Chunk loading and unloading
- Chunk serialization and disk reads
- Chunk-to-player transmission
- Duplicate chunk requests
- Chunk tracking
- Chunk update batching
- World generation task scheduling
- Unnecessary chunk scanning
- Chunk-related allocation spikes

Potential optimizations:

- Deduplicate eligible work.
- Introduce bounded queues for non-critical tasks.
- Prioritize work near active players where appropriate.
- Reduce repeated expensive scans.
- Coalesce redundant state notifications while preserving correctness.
- Improve caching with explicit invalidation and size limits.
- Optimize batch processing without delaying critical world changes.

Never modify game-world data concurrently unless the engine explicitly supports that operation.

Never sacrifice chunk correctness or save integrity for speed.

## 5. BLOCK ENTITY AND WORLD-SYSTEM OPTIMIZATION

Profile and optimize expensive systems such as:

- Containers and inventories
- Mechanical power
- Firepits and furnaces
- Food spoilage calculations
- Temperature calculations
- Farming and crop growth
- Animal simulation
- Liquid interactions
- Block entity tick listeners
- Automation and production machinery
- Modded machines
- Claim and permission queries

Identify duplicate calculations, excessive allocations, ineffective caching, and avoidable work on unloaded or inactive areas.

Use event-driven processing instead of repeated polling where it is genuinely equivalent.

Do not alter production rates, spoilage outcomes, crop growth, or machine behavior unintentionally.

## 6. NETWORK OPTIMIZATION

Investigate multiplayer networking costs under high player populations.

Focus on:

- Per-player outbound bandwidth
- Incoming packet processing
- Entity tracking and updates
- Chunk transmission
- Repeated state synchronization
- Player movement replication
- Unnecessary mod network messages
- Burst traffic during login and teleportation
- Network queue depth and processing latency

Implement safe improvements such as eliminating duplicate updates and batching compatible messages.

Preserve ordering, reliability, anti-cheat visibility, and player synchronization.

Do not simply drop important packets to obtain better metrics.

## 7. MEMORY AND GARBAGE COLLECTION

Reduce memory allocations in frequently executed code paths.

Investigate:

- LINQ usage in hot loops
- Temporary collections
- Repeated object creation
- String allocations
- Per-tick buffers
- Event subscriptions
- Long-lived caches
- Unbounded queues
- Memory leaks and retained references

Use appropriate pooling and reusable buffers only where ownership and lifetime are clear.

Measure allocation rates, memory retention, and GC pauses before and after each change.

Ensure caches cannot grow indefinitely.

## 8. TICK SCHEDULING AND WORK DISTRIBUTION

Create an optional adaptive scheduler for non-critical mod-owned work.

Features:

- Configurable per-tick work budgets
- Priority queues
- Bounded background work
- Deferred low-priority tasks
- Load-aware scheduling
- Fairness and starvation prevention
- Backpressure when queues become overloaded
- Detailed scheduling telemetry

Never postpone authoritative gameplay actions without explicitly demonstrating that the delay is safe.

Do not modify the global tick interval as a substitute for optimizing expensive work.

Report changes in effective TPS separately from reductions in processing cost.

## 9. MOD COMPATIBILITY AND INTEGRATION

The optimizer must work on servers running dozens or hundreds of mods.

Test compatibility with:

- Vanilla Vintage Story systems
- xSkills and class mods
- Modded creatures and AI
- Claim management mods
- Economy and shop mods
- Discord integrations
- Server-side administration tools
- World-map integrations
- Player-list systems
- Anti-cheat systems
- OreShield and hidden-resource synchronization

Newfie's Server Suite must remain functional.

Particular care must be taken not to interfere with Client Integrity / Anti-Cheat, OreShield concealment, block-interaction logging, claim enforcement, or player permissions.

All optimizations should have individual enable/disable switches.

A compatibility-safe operating mode must be included.

## 10. REAL MULTIPLAYER BOT TESTING FRAMEWORK

Build a separate application called **Newfie's VS Stress Tester**.

Its purpose is to load-test a real dedicated Vintage Story server using multiple concurrent, authenticated player sessions.

A bot must be an actual connected player session, not merely an in-server entity pretending to be a player.

Preferred implementations:

1. Official Vintage Story clients controlled through an automation harness.
2. A headless client built using inspected and legally usable client or protocol components, if feasible.
3. A protocol-compatible bot harness that implements the actual required authentication, session, networking, and gameplay messages.

Investigate these options and explain feasibility before choosing an architecture.

Never claim an NPC, fake player object, or direct server API simulation is equivalent to a real multiplayer network client.

If implementing an authentic headless client is blocked, create a working official-client automation alternative and clearly document limitations.

Use properly authorized game accounts and test environments. Never bypass authentication or licensing.

### Bot capabilities

Bots must be able to:

- Connect and authenticate
- Join the server
- Spawn normally
- Move through the world
- Sprint and jump
- Explore new terrain
- Load new chunks
- Mine blocks
- Place blocks
- Open and manipulate inventories
- Interact with containers
- Perform combat
- Trigger mob AI
- Teleport through permitted test commands
- Disconnect and reconnect
- Remain online for extended periods
- Report connection and interaction failures

The stress tester must support configurable scenarios and controlled, seeded movement patterns.

### Bot population tests

Test at the following concurrent client counts:

- 1 bot
- 5 bots
- 10 bots
- 20 bots
- 30 bots
- 50 bots
- 75 bots
- 100 bots
- 150 bots
- 200 bots

These are targets, not assumed capabilities. Execute a level only when sufficient authentic client sessions, resources, and authorization are available.

Report the maximum number actually tested.

Separate bot-generator resource usage from server resource usage so the load generator does not invalidate the results.

## 11. REQUIRED STRESS TEST SCENARIOS

### Scenario A — Idle Multiplayer

Connect many players in one area.

Measure player presence, connection maintenance, and synchronization overhead.

### Scenario B — Exploration

Have bots move in different directions, generating and loading unexplored chunks.

Measure world-generation throughput, tick spikes, CPU usage, and chunk-loading latency.

### Scenario C — Concentrated Players

Place many bots in one settlement.

Simulate normal movement and interactions.

Measure entity tracking and network replication.

### Scenario D — Mob Horde

Spawn controlled populations of Drifters, Bowtorns, and modded mobs.

Test 100, 250, 500, and 1,000 active mobs when feasible.

Measure AI, pathfinding, collisions, and combat overhead.

### Scenario E — Heavy Automation

Create a large controlled test base containing mechanical equipment, containers, heating equipment, and ticking block entities.

Measure simulation and block-entity performance.

### Scenario F — Combat

Have multiple bots fight mobs simultaneously.

Measure combat responsiveness, damage handling, AI, and network synchronization.

### Scenario G — Rapid Exploration and Teleportation

Move groups between distant world locations.

Measure chunk loading, unloading, network bursts, and synchronization correctness.

### Scenario H — Heavily Modded Server

Run the actual production mod collection on a cloned test server.

Identify which mod systems contribute most to tick time and memory allocation.

### Scenario I — Extended Endurance Test

Run a 2–6 hour workload, with a configurable longer test option.

Measure memory retention, GC behavior, growing queues, performance degradation, and stability.

### Scenario J — Fractured Night Stress Test

For compatible custom servers, simulate a Fractured Night event with high-tier enemies, many players, particle effects, and elevated spawn populations.

Compare normal-night performance against event-night performance.

Preserve gameplay and event mechanics.

## 12. CONTROLLED BENCHMARK METHODOLOGY

Create a repeatable performance evaluation framework.

Every optimization must be tested against:

**Baseline A:** Unmodified server with matching game version and mod pack.

**Baseline B:** Server with the profiler installed but optimizations disabled.

**Candidate C:** Server with one optimization enabled.

**Candidate D:** Server with the complete validated optimization set enabled.

Use the same world snapshot, game configuration, mod versions, hardware allocation, workload script, bot count, and test duration.

Use separate warmup and measurement periods.

Repeat benchmarks at least three times where practical, randomize trial order where appropriate, and report run-to-run variation.

Record actual environmental differences.

Measure server health alongside performance: errors, disconnects, interaction failures, queue accumulation, simulation timing, and save integrity.

A lower tick time only counts as an improvement when the same correct workload is completed.

Include throughput and correctness metrics, not just raw CPU utilization.

## 13. PERFORMANCE TARGETS

Use the following as aspirational engineering targets, never guaranteed results:

- 20–40% reduction in main-thread processing time in bottlenecked scenarios
- 25–50% reduction in mod-controlled hot-path allocations
- 20–35% improvement in P99 tick time under relevant loads
- Fewer excessive GC pauses
- Reduced unnecessary entity-processing overhead
- Reduced duplicate network and chunk work
- Increased maximum sustainable concurrent player count

A successful result must preserve gameplay and deliver reproducible improvements.

If gains are smaller, report the measured outcome accurately.

## 14. AUTOMATED OPTIMIZATION CYCLE

Implement a disciplined development loop:

1. Establish an untouched baseline.
2. Run all applicable stress scenarios.
3. Identify the most expensive measurable bottleneck.
4. Propose a minimal, targeted optimization.
5. Implement the optimization behind a feature flag.
6. Run focused correctness tests.
7. Repeat the baseline workload.
8. Compare performance measurements.
9. Run regression and compatibility tests.
10. Keep the optimization only if the measured benefits justify its complexity and risks.

Never add an optimization simply because it sounds faster.

Never silently remove gameplay features, despawn legitimate entities, skip scheduled saves, or manipulate TPS reporting.

If a change causes regressions, disable or revert it.

## 15. PERFORMANCE DASHBOARD

Create a lightweight web-based dashboard, hosted separately from the game server where practical.

Display:

- Current and historical effective TPS
- Tick duration percentiles
- CPU and RAM usage
- GC activity
- Connected players
- Active entities
- Loaded chunks
- Network traffic
- Save duration
- Bot count
- Active benchmark scenario
- Highest-cost measured systems
- Optimization status
- Before/after benchmark comparisons

Include interactive historical graphs and exportable reports.

Dashboard collection must be rate-limited and must not become another performance bottleneck.

## 16. CONFIGURATION

Provide a human-readable `optimizer.json` configuration with sections for:

- Profiling
- Entity optimization
- Chunk optimization
- Block entities
- Memory management
- Networking
- Scheduling
- Logging
- Dashboard
- Compatibility
- Benchmarking

Use safe defaults.

Expose experimental features separately from proven optimizations.

Document any options that intentionally trade simulation fidelity for resource savings.

All risky optimizations must be disabled by default.

## 17. VALIDATION AND SAFETY

Required validation includes:

- Clean dedicated-server startup and shutdown
- World save/reload integrity
- Entity and block persistence
- Inventory and container transaction correctness
- Claims and permissions
- Combat correctness
- Player movement consistency
- Compatibility with server-side anti-cheat
- Compatibility with OreShield
- No uncontrolled memory growth
- No queue starvation
- No thread-safety violations
- No unauthorized packets or authentication bypasses
- Graceful operation if profiling is unavailable

Maintain server-authoritative behavior at all times.

Benchmark only on dedicated test servers with permission from the owner.

Never stress-test arbitrary public servers.

## 18. REQUIRED PROJECT DELIVERABLES

Deliver:

1. Complete C# source code for Newfie's Server Optimizer.
2. A compiled server-side mod ZIP.
3. Correct `modinfo.json` and project files.
4. Complete configuration with documentation.
5. A standalone multiplayer bot load-testing project.
6. Executable bot scenarios and setup instructions.
7. Automated benchmark runner.
8. Performance report generator.
9. Optional web dashboard.
10. Tests for correctness and regression prevention.
11. Baseline versus optimized benchmark results.
12. Documentation covering limitations, incompatibilities, and rollback procedures.

Do not include placeholder implementations and label them complete.

Do not fabricate benchmarks or performance metrics.

If actual binaries, authentication assets, hardware, or game installations are unavailable, provide the working components that can be built, explain what could not be executed, and separate demonstrated results from untested hypotheses.

## 19. IMPLEMENTATION ORDER

**Phase 1 — Research and profiling**

Inspect Vintage Story 1.22.7 assemblies, official APIs, existing diagnostics, and server execution architecture.

Build a reliable performance profiler.

**Phase 2 — Real bot framework**

Build and verify real connected test clients before making optimization claims.

**Phase 3 — Establish baseline**

Benchmark vanilla and heavily modded server scenarios.

**Phase 4 — Highest-impact improvements**

Prioritize the most expensive validated bottlenecks: usually candidate areas such as AI, chunk processing, block entity updates, allocations, and networking, but let measurements determine actual priority.

**Phase 5 — Advanced optimization**

Evaluate safe scheduling, caching, work deduplication, and carefully scoped engine hooks.

**Phase 6 — Compatibility testing**

Validate real-world gameplay, persistence, anti-cheat, and third-party mods.

**Phase 7 — Long-duration tests**

Run sustained stress testing and analyze gradual degradation.

**Phase 8 — Release**

Deliver a stable build with measured results, documentation, and safe configuration defaults.

## FINAL INSTRUCTION

Build a genuinely effective Vintage Story server optimization system, not a collection of arbitrary tick throttles.

**The primary success metric is how much more legitimate multiplayer gameplay the server can process while maintaining responsiveness, world correctness, and stability.**

Use real connected clients, representative workloads, detailed profiling, repeatable benchmarks, and evidence-based optimization.

Start by investigating the server architecture and building the profiler and bot-test feasibility prototype. Then prioritize development based on measured bottlenecks.

Provide actual code, reproducible tests, measurable results, and honest technical limitations throughout the project.