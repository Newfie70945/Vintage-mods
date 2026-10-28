# NOS 0.20.21-preview — Suite 1.2.55 compatibility

Extends the exact-binary OreShield patch compatibility rule to Suite 1.2.55:
`50ab5f35dca8e120334016061a188a5431284cabbe9e38c39ba2e789c7d7d3f8`.
Previously audited Suite 1.2.53 and 1.2.54 binaries remain accepted. Target, patch
kind, owner and handler checks are unchanged; other unknown hooks remain conflicts.

Suite 1.2.55 sanitizes its private block array before constructing a fresh outgoing
chunk palette. This removes unused concealed ore IDs from compressed metadata.
NOS does not itself mask ore or enable OreShield/seed privacy. Optimization defaults
and the ARL, Walkable Entities and GeneLib rules are unchanged. Scaffolding is not
covered. See compatibility-0.20.19.md for the original audit boundaries.
