# NOS 0.20.20-preview — Suite 1.2.54 compatibility

Extends the narrowly scoped OreShield `BeforeSetBlocksPacket` compatibility rule
to the tested Suite 1.2.54 DLL, SHA-256:

`2917fb3b2955e528904a886bb4d2926fb6c3e3632ef25d5caccf6fc22fe961b3`

The previously audited Suite 1.2.53 binary remains accepted. All module, target,
patch-kind, owner, handler and exact-binary checks remain required. Other
unrecognized binaries and hooks remain conflicts. See
[0.20.19 compatibility](compatibility-0.20.19.md) for the unchanged ARL, Walkable
Entities and GeneLib compatibility rules and reproduction instructions.

Suite 1.2.54 adds an independent, default-off client seed privacy preview. NOS
does not enable it or modify seed packets. Read Suite's `SEED-PRIVACY.md` for its
coverage, restart-only configuration and client testing limits. This release does
not add an exception for Scaffolding's collision patch or fix native profiler
overflow attribution.
