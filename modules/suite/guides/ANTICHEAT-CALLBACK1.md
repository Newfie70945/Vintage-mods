# Suite 1.2.49 — Anti-Cheat callback pass

Build: ms11-anticheat-callback1. NOS remains 0.18.1-preview.

Completed Mod DB verification results share a queued game-thread refresh. Sessions matching multiple pending results are refreshed once per batch, using current cached verification data. This reduces repeated scans/recomputations during bursts; it does not move game state access off-thread or eliminate all verification costs.

Verification-cache persistence ignores clean caches and observes a five-second minimum interval outside forced shutdown saves. Dirty changes arriving during a write remain dirty for a later save. Cache persistence remains synchronous; this change limits repeated writes rather than claiming disk waits are eliminated. A crash may lose recent cache results, which can be fetched again.

Successful consequence kicks/temporary bans establish a five-second pending-disconnect window. Further Kick rules do not queue another kick during that window. Log rules and stronger TempBan rules remain eligible; unsuccessful actions do not establish the window. Connection initialization resets it. This affects consequence rules, not every disconnect path in the game.

Suite Timing Hotspots now separates verification cache persistence, refresh scheduling (the session-refresh row), and actual queued batch refresh. Wall times include waits/GC and are not pure CPU time. These are aggregate timings, not retroactive reconstruction of earlier stalls.

Regression checks exercise the actual consequence method extracted from current source with stubbed actions: initial success, cross-pass duplicate suppression, retained logging, stronger ban, expiry, failed action and subsequent retry. Run DOTNET_ROOT=<sdk> python3 modules/suite/tests/AntiCheatConsequenceChecks/run.py. They do not simulate actual game disconnections or external Mod DB responses.

The supplied native log measured 258 ms across five AntiCheat callbacks in one 294 ms tick. That is the target of this pass, not a production before/after result. The separate 7.39-second tick has approximately 7.18 seconds outside its displayed top-level breakdown. No culprit or fix for that gap is established here. Barrel, simulation and ore-crushing internals are unchanged by this build.

Validation: copied official server with 25 mod ZIPs passed 91 integration checks for batching/persistence changes. A subsequent return-value correction to the pending-kick guard passed the actual-method regression harness; that final assembly builds with zero warnings/errors. No production before/after performance claim or reproduced real-network kick benchmark. Module/source consistency checks pass.
