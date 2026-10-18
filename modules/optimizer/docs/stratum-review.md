# Stratum review for NOS 0.3.0

Reviewed 18 October 2026: [recent releases](https://github.com/StratumServer/Stratum/releases) and merged pull requests. NOS patches the inspected official 1.22.7 server in memory; Stratum builds a modified runtime. These are independent implementations with different compatibility constraints.

| Change reviewed | NOS decision |
| --- | --- |
| [#395: compress server assets once](https://github.com/StratumServer/Stratum/pull/395) | Implemented the reusable-compression idea through a guarded compression prefix/postfix. Vanilla still sends packets. Unlike an indefinitely immutable asset assumption, NOS compares complete source bytes before hits, tracks compressor identity, returns independent output arrays, and bounds retained cache memory. |
| [#352: AllOnlinePlayers shared-state correction](https://github.com/StratumServer/Stratum/pull/352) | Existing NOS player snapshot module already returns a fresh array to each caller; no shared mutable returned player array. Retained this behavior. |
| [#398: inventory flush broadcast budget](https://github.com/StratumServer/Stratum/pull/398) | Deferred. Requires fair dirty-inventory scheduling, persistence of deferred work and modded inventory tests; delaying only packet sends risks stale client state. |
| [#306: inventory privacy](https://github.com/StratumServer/Stratum/pull/306) | Deferred. Affects access checks, display containers, backpack representation and client prediction. Not a safe generic packet filter. |
| [#291: lazy server pose matrices](https://github.com/StratumServer/Stratum/pull/291) | Deferred. Requires intercepting pose readers and supporting physics threads and modded attachment/animation consumers. |
| [#273: scheduling allocations](https://github.com/StratumServer/Stratum/pull/273) | Reviewed, but intertwined with Stratum-specific worldgen stages, pinning and unload coordination. No speculative chunk scheduler replacement. |
| [#344: skip empty tick-state removals](https://github.com/StratumServer/Stratum/pull/344) | Applies to Stratum's entity-throttle state map, which the official server and NOS do not have. No corresponding patch to install. |

Validation separates component speed from whole-server behavior. Reusing compression benefits repeated joins with an eligible asset buffer; it does not improve every tick or reduce the network payload itself. Worldgen, save I/O, entity activation, AI semantics and packet ordering are unchanged. Source compatibility and conflicts are checked independently for each installed module.

Low-compressibility or over-budget asset buffers use stock compression after the initial probe, avoiding repeated cache-copy overhead. A new asset buffer or reapplying settings permits a fresh probe. Cache hits require at least 25% size reduction on that probe.
