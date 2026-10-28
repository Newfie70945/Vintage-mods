# NOS 0.20.19-preview compatibility validation

Changes: narrowly audited compatibility for ARL firepit attribute redirection,
Walkable Entities collision/repulsion, Suite OreShield block updates and GeneLib
asset sends. Existing defaults and user switches are preserved; no foreign hooks
are removed. Suite remains 1.2.53 and the five companions are unchanged.

## Executed results

- Optimizer release build: zero warnings/errors; full 39-suite build/test run
  passed with `NOS_COMPAT_FIXTURES` pointing to actual public mod binaries.
- Focused compatibility runner: 84 checks each with ARL 3.2.0, 3.1.5 and 3.1.4,
  GeneLib 3.2.1, Walkable Entities 1.0.44 and the public Suite 1.2.53 DLL.
- Tests include matching owners with wrong handlers; matching owner/type/handler
  from an unaudited dynamic assembly; wrong patch kinds and unrelated modules;
  permitted startup/monitor activation; late additional conflicts under the same
  owner; foreign-hook preservation on stand-down/disposal; real ARL/native empty
  firepit equivalence; populated-firepit fallback; independent asset compression
  buffers; and a cached delegate observing a subsequently installed Harmony hook.
- Native entity-hot-path checks with the published Walkable repulsion prefix:
  40 checks passed, including bit-identical native push behavior.
- Native traversal checks with the published Walkable terrain postfix: 43 checks
  passed, including collision results/flags/hooks, dimensions, iterator disposal,
  nesting, custom inventory fallback, exceptions and allocation boundaries.
- Existing 169 engine lifecycle/compatibility checks and the other optimizer
  regression suites passed. Source/version consistency and whitespace checks pass.

## Official-server integration

Disposable run: `artifacts/validation/web-snapshot-1793158636/result.json`.

Loaded the seven Suite/NOS/companion packages plus actual GeneLib 3.2.1,
Walkable Entities 1.0.44 and ARL 3.2.0 ZIPs. The three external IDs were checked
in the native dependency list in addition to the integration's normal module
checks. OreShield recipient hooks were installed and all five requested NOS
modules reported active. The complete HTTP/protection/performance/optimizer
checks passed, followed by explicit world save and clean shutdown: exit code 0,
zero server errors, SQLite save integrity `ok`.

The installed NOS ZIP SHA-256 equals the final release ZIP/build manifest. That
manifest records 39 executed suites with `testsSkipped: false`. A temporary
build-only package allowed the native run to overlap the final test run; the
completed full build produced an identical ZIP and the final manifest records
the completed tests, not the temporary build-only manifest.

## Limits

The server fixture contains no authenticated gameplay clients. These checks do
not simulate players walking aboard moving boats/trains, establish full-modpack
compatibility or quantify live TPS gains. Older ARL versions were tested in the
focused native runner; the full server run used ARL 3.2.0. Changed/unknown DLLs
remain conflicts pending audit. Fingerprints and reproducible fixture setup are
documented in `modules/optimizer/docs/compatibility-0.20.19.md`.
