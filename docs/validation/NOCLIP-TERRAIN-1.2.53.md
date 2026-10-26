# Noclip terrain review — 1.2.53

Noclip findings are always advisory, including legacy findings carrying enforcement
metadata. They do not add noclip movement risk or enter automatic punishment paths.
This does not disable separate speed, packet or other server-side protections.

When configured, position correction now requires three independently spaced compatible
observations spanning at least 1.5 seconds. Observations are separated by at least 500ms;
a gap over 5s or an episode exceeding 15s resets confirmation. First detections do not
correct. Trusted transitions, mount handling, identity changes and unreliable timing
reset the relevant confirmation state.

Each qualifying observation includes distinct intersected block positions and block-type
counts, up to eight block codes, complex-geometry status, evidence truncation status and
correction confirmation. These counts describe body intersections in that sampled path
or endpoint, not a lifetime count or a proven count of walls fully traversed. One wall
can contain multiple blocks and one block can intersect many samples.

Doors/chiseled codes, custom block classes and non-unit-cube/multiple-box geometry remain
review evidence and cannot establish correction eligibility. Full known geometry is still
required. Existing loaded-only probes and bounded path sampling remain. Terrain evidence
is capped at 128 distinct positions per observation and is not retained world-wide.

Compatibility: the conservative geometry filter intentionally also excludes many ordinary
modded blocks from correction. Standing within a block does not prove cheating. No live
modpack false-positive study or guarantee of detecting every noclip technique is claimed.

Client report audit: current scan validation already checks challenge consumption/expiry,
ID, nonce, required scanner payloads, report bounds and consistency. Bound heartbeats
check challenge and increasing sequence. These prevent some malformed/replayed reports;
they cannot prove the truth of an inventory fabricated by an in-process Harmony patch.
No hidden-mod detector or tamper-proof client attestation was added in this pass.

Validation: official 1.22.7 build passed (zero warnings/errors); PacketGuard policy
suite and 24 compiled collision/flight checks passed. Native ingress regression suite
also passed during this pass. Source consistency and whitespace checks passed.
No live door/chiseled-block or Draconis multiplayer session was run.
