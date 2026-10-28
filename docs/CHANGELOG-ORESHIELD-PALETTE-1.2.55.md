# Suite 1.2.55 / NOS 0.20.21-preview

- Fix a confirmed OreShield information leak: compressed chunk palettes retained
  unused concealed ore IDs after their cells were replaced with host rock.
- Sanitize the private unpacked block array before constructing the outgoing
  layer so the fresh palette contains only the recipient's final view.
- Preserve authoritative world data, canonical packets, legitimate discoveries,
  player-placed ore, recipient isolation and existing cache behavior.
- Add native wire-palette checks and an identical-output check for concealed
  mineral substitutions sharing the same host.
- Extend NOS's exact-binary OreShield compatibility to Suite 1.2.55, retaining
  the previously audited Suite binaries. Optimization defaults are unchanged.

No Client Integrity companion update, configuration migration or world migration
is required. OreShield still needs to be enabled in your saved server settings.
Seed privacy remains a separate default-off client compatibility preview. No
combat/inventory shadow-mode, movement thresholds or consequence rules changed.

See [the audit and its limits](../modules/suite/guides/SERVER-AUTHORITY-AUDIT.md).
