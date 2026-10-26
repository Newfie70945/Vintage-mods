# Server action guards — 1.2.53 follow-up

Ingress checks run before native action handlers when PacketGuard ingress is active.
- Inventory activation, movement, swapping and open/close require their correct payload,
  an authenticated player and nonblank inventory IDs of at most 512 characters.
- Negative transfer quantities are rejected. Zero, full-stack and modded batch quantities
  remain native; no crafting batch throttling or source-stack quantity assumptions added.
- Creative item creation requires a supplied stack, valid inventory ID, nonnegative slot,
  creative mode and an existing server-known destination with an in-range slot. Missing
  or stale destinations are silently dropped, not treated as cheat evidence.
- Entity interactions reject negative/oversized selection indices and nonfinite or
  non-addressable hit coordinates before native events/custom entity code run. Hit
  coordinates intentionally do not use the small block-local bound (large creatures).

Native inventory CanPlayerModify checks, revision resync, tab selection, ground-slot
semantics and combat range/permission checks are retained. No new client claims grant
permissions. No new scan, chunk load or persistent per-action history is introduced.
Flight review remains advisory; mounted and authorized flight exemptions are unchanged.

Scope limits: these are ingress safety checks, not a complete item-duplication detector
or authoritative recipe/mining-time rewrite. Custom mod network channels and custom
inventory handlers still require separate audits. No live full-modpack test performed.

Native references reviewed: official 1.22.7 ServerSystemInventory,
InventoryNetworkUtil, InventoryBase.GetSlotsIfExists and
ServerSystemEntitySimulation.HandleEntityInteraction.

Validation: official 1.22.7 Release build passed with zero warnings/errors.
PacketGuard policy tests passed. Compiled native-runtime dispatch tests cover missing
inventory envelopes/IDs, negative transfers, unchanged full-size/special-slot requests,
entity selection validation, survival creative-spawn rejection and creative destination
bounds/existence. All 21 collision/flight runtime checks passed. Source consistency
and whitespace checks passed. No production server was modified.
