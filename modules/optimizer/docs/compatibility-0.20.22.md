# NOS 0.20.22-preview — Suite 1.2.56 compatibility

Extends the exact-binary OreShield compatibility rule to Suite 1.2.56:
`7543c94595f4a1826e1e8a6db00dacb77a327ceee16e5373af2d71c1719f350c`.
Previously audited Suite 1.2.53–1.2.55 binaries remain accepted. Target, owner,
handler and patch-kind checks remain required. Unknown hooks remain conflicts.

Suite 1.2.56 reduces temporary log-reader and same-chunk OreShield sync allocations.
NOS optimization behavior/defaults and the ARL, Walkable Entities and GeneLib rules
are unchanged. This does not add Scaffolding compatibility. See the original
compatibility-0.20.19.md audit for those external hook boundaries.
