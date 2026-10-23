# NOS 0.9.0 preview + Suite 1.2.47 Network2

NOS 0.9.0 retains the previous physics packet list optimizations and adds independently switchable despawn packet projection. It patches the official 1.22.7 runtime in memory and does not rewrite server DLLs.

## Despawn packet projection

The official `ServerPackets.GetEntityDespawnPacket` allocates three LINQ iterators to project entity IDs, reasons and death damage sources. NOS performs those same three passes directly with list enumerators. It keeps the official packet ID, setters, field ordering, duplicate IDs, reason defaults and the distinction between `Block` for absent despawn data and `Unknown` for absent death damage source. Output arrays remain independently owned by each outgoing packet. Empty arrays retain the stock shared-empty behavior.

No packet pooling, entity-lifetime changes, send scheduling or network batching is introduced. Null input is passed to stock code to preserve argument exception details. Invalid entries propagate errors without retrying packet construction. Caller-owned input lists still require normal thread safety; this does not make concurrent mutation of the same list supported.

`EngineOptimizations.DespawnPacketProjection` defaults on and has its own Suite control, actual activation/conflict state and packet/entry counters. Master disable, Safe Mode, exact official engine hashes and foreign-patch checks apply. Older NOS versions leave the new Suite control disabled. Suite stays 1.2.47 with build suffix `ms11-optimizer-network2`.

## Component measurements

The actual unpatched official packet factory was compared with the Harmony-patched factory using synthetic lists and .NET 10 with tiered compilation disabled. Forty checks covered empty/single/large inputs, duplicate/negative/maximum IDs, reason and source defaults, all serialized array counts, independent packet ownership, null/invalid exceptions, independent disable and concurrent calls with separate inputs. Three alternating populated trials used 30,000 calls after warmup.

| Entries | Stock B/packet | NOS B/packet | Removed |
| --- | ---: | ---: | ---: |
| 1 | 936 | 720 | 216 B |
| 64 | 1936 | 1720 | 216 B |

Populated fixture median time was approximately 964 ns stock and 400 ns NOS. This small component benchmark excludes transports, authenticated players and whole-server work; it does not establish a TPS, bandwidth or GC-pause improvement. [Raw measurements](research/components-0.9.0/despawn-packet-measurements.json).

The 0.8.0 physics allocation results remain historical evidence for that unchanged implementation; see performance-0.8.0.md. Spawn writer reuse remains deferred because custom entity serialization owns behavior beyond a simple array projection. Path-node pooling and inventory scan shortcuts also remain deferred pending compatibility and lifetime validation.

## Full build and integration

Both compiled mods built with zero warnings/errors. All 18 optimizer suites passed (14 C# projects and four Python suites), including 40 despawn component checks and 39 engine lifecycle/conflict checks. Browser regression covers saving the new switch, older-NOS unsupported controls, read-only/Safe Mode restrictions, capture locks and mobile layout.

A copied official 1.22.7 server with Suite, NOS, the disposable terrain probe and the selected pack (25 ZIPs; 28 loaded mod records) passed 13 integration checks. Both physics temporary-list reuse and despawn projection independently activated, disabled and re-enabled through the authenticated Suite bridge. Existing capture protections, actual-terrain A* checks and settings restoration passed. [Sanitized result](research/components-0.9.0/suite-real-terrain-integration.json).

This server run establishes startup/control and sampled correctness, not real-player load, long-session behavior or transport throughput. The packet component fixture tests populated despawn data separately; the integration run does not prove production TPS/GC gains.
