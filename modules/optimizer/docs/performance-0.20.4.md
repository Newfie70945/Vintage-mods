# NOS 0.20.4 preview — avoid state allocation for protected entities

The optional distance policies previously created a weak-table scheduler state before checking basic entity exemptions. They now check those exemptions first. Entities already excluded by safety, type, server-side eligibility, always-active status, swimming/fire/lava, ground state or mounting do not require a new scheduler state.

If an entity already has state, the exempt path still clears its AI/wander/repulsion deadlines. Becoming eligible again therefore starts with no stale delay. Hurt grace state is retained. Other custom behavior/task checks and all proximity safety rules remain unchanged. Null/exception paths still fail open and record failures. No strong entity references, new caches or reduced simulation frequency were added.

Existing independent AI distance, distant wander, repulsion and safety controls apply. Defaults remain unchanged; scheduling policies remain opt-in. Suite controls require no protocol change.

## Focused allocation and behavior tests

Five first-encounter trials over 256 pre-created always-active native entities measured approximately 144.78 bytes per entity before this change and zero afterward. This is first-encounter scheduler overhead with the policies enabled, not recurring allocation every tick. Focused median first-encounter check times were approximately 245 ns before and 40 ns after; timing varied between processes and is not a server-wide result.

A regression test exercises all three protected prefix paths and checks that no scheduler state allocation occurs. Another establishes a distant entity delay, makes the entity always-active, verifies immediate stock behavior and then removes the exemption to verify that an old delay is not resurrected. Existing tests cover hurt grace, players, near/far transitions, mounts, domestication, custom behaviors/tasks, active tasks, dimension changes and native task-start injection.

This does not explain or resolve a multi-gigabyte production memory increase. At 3,000 first encounters, the measured allocation difference is roughly 0.41 MiB. No new production-like server timing comparison or authenticated-client run was performed for this narrow change. The earlier 3,000-animal benchmark had these policies disabled and is not evidence for this build's benefit.

[Focused measurements](research/exempt-entities-0.20.4/measurements.json). Full executed validation suites are recorded in the build manifest.
