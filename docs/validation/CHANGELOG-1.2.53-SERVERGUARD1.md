# Suite 1.2.53 — server-side protection follow-up

- Reduced flood false positives by requiring observations spread across time instead of counting one large queued burst as sustained evidence.
- Added accepted/dropped traffic counters and configured budget details to flood findings.
- Bound delayed findings to the original player session; anonymous observations cannot be assigned to a later login.
- Tightened reach-history validity for clock changes, entity replacement and dimension changes without reducing configured reach allowances.
- Improved unmounted flight review resets and position/geometry validation. Flight findings remain advisory-only.
- Made noclip checks skip lag/warmup, stale positions, unloaded terrain and invalid collision shapes.
- Require complete loaded paths and clear endpoints; escaping an existing overlap is not treated as a new wall crossing.
- Added elapsed-time confirmation for repeated endpoint overlap, with resets for stale/same-time observations and dimension changes.
- Made noclip correction-off a true review mode: observations do not add noclip risk or qualify for automatic punishment.
- Clarified collision findings so they do not claim a rollback succeeded merely because one was configured.

Existing enablement settings and packet limits remain unchanged. Other mods are unchanged.
