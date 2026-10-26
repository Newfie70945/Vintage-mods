# Server Essentials Suite 1.2.53

Changes since the released 1.2.52 build:

- Translocator markers now start hidden every time the map opens, including for browsers that previously saved the layer as enabled. Players can enable it for the current visit; other layer preferences and shared discoveries remain intact.
- Reduced temporary image allocations when rebuilding map zoom tiles by decoding into reusable buffers.
- Improved hillshading at tile edges on continuous slopes. Previously cached images pick up the change when rendered again.
- Added WebPanel controls for the optimizer's optional barrel recipe-scan reduction and forced-rescan interval. Unsupported optimizer builds keep these controls disabled.
- Added missing route policy entries for automatic profiling and mod-update compatibility settings.
- Added checks for map cache-version consistency, configuration cloning, release-version consistency and registered mutating routes.
- Preserved loaded-only dirty updates, no-world-loader automatic map behavior and undiscovered-translocator destination privacy from 1.2.52.

# Newfie's Server Optimizer 0.20.15 preview

Changes since the author-reported released 0.20.14 build:

- Added an optional reduction in repeated failed recipe searches for unchanged storage barrels.
- Spread forced rescans by barrel position to reduce synchronized rescan bursts.
- Added independent settings, native/foreign-patch compatibility guards, execution/failure counters and Suite controls.
- Failures in either a normal scan or the skipped unseal/dirty remainder stand down the module without replaying the operation.
- Default remains **off**. Default forced rescan: every 10 checks (about 30 seconds). Attribute-only and world/mod-state-only recipe changes can therefore be recognized later; this option changes recognition timing.
- Added native behavior/failure/equivalence tests and synthetic barrel measurements. No production TPS improvement is claimed.

Other companion mods are unchanged. Sailing is excluded at the author's request. See the audit report for withheld changes and test scope.
