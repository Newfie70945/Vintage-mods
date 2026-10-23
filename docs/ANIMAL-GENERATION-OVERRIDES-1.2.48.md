# Animal Limiter: per-rule generation protection

Suite 1.2.48 adds generation overrides to existing population rules. This changes which animals qualify for the population cap; it does not stop mating or change breeding generation values.

For wild-breeding raccoon dogs:

1. Open Animal Limiter and run **Scan Population**. Filter the results for the animal's registered code. If unsure of its code, look at one in-game and use `/animallimit target 20 3` to create an exact-type rule, then inspect `/animallimit list` or the web rule list.
2. Click **Create rule** beside the entity type (or **Edit rule** for an exact existing rule). This fills the editor; nothing is saved by clicking it.
3. Choose a population cap and radius. The default 20/3 example is not a recommendation for every species. Radius 0 means one chunk; 3 means a 7×7 chunk region. Male/female/age variants may have distinct codes; use a carefully checked wildcard to cover a species, or configure exact types individually.
4. Set **Generation protection → Ignore generation protection**, then **Save new rule / Update Rule**. The population cap can now include wild-born offspring even when the global generation and farm/bred protection switches are on.
5. Refresh/scan to inspect eligibility. The limiter must be enabled and the area loaded for normal limiting. Owned/tamed pets, named animals, exclusions and nearby-player protections still apply according to their existing switches. Generation overrides do not override those safeguards.

Do not add the animal to Exclusions: exclusions protect it from the limiter entirely. Ignoring generation instead makes it eligible for the configured population cap.

## Custom generation protection

Choose **Protect generation N and above** and enter a positive whole number. With N=5, generations 0–4 may qualify for the population cap; generation 5+ is protected. Numeric generation attributes are read across supported integer/long/float/double types. Legacy boolean/string generation flags count only as generation 1. Unknown/nonfinite values do not satisfy a numeric threshold. Tamed/owned protection remains separate.

**Inherit global protections** removes the override. Existing rules use this behavior automatically. The most-specific winning population rule supplies the policy; an exact rule without an override does not inherit a broader wildcard rule's custom policy. Rule removal clears its generation override too. Old GUI/API updates that only change cap/radius preserve a saved override.

## Commands

After creating a population rule:

```
/animallimit generationrule <stored-rule-pattern> off
/animallimit generationrule <stored-rule-pattern> protectfrom 5
/animallimit generationrule <stored-rule-pattern> inherit
/animallimit list
```

Replace the placeholder with the actual stored pattern, including its mod namespace. No raccoon-dog namespace or population rule is preinstalled by this update.

Settings are stored with the Suite's Animal Limiter configuration (`ModConfig/NewfiesServerEssentialsSuite/NewfiesAnimalLimiter.json`). They persist after restart. The upgrade adds optional settings without changing existing global protections or enabling the limiter.

Population totals scroll within a 320 px panel; regional detail is collapsed by default and scrolls within 260 px when opened. Entity filtering and Create/Edit actions use the completed scan; no additional world scan is started by those actions.

## Validation

The initial animal-feature validation capture retained below used a pre-release candidate labelled 1.2.47. The feature ships in 1.2.48; the original raw capture is preserved.

- Twenty-seven checks invoke the actual compiled limiter protection method with synthetic entities: legacy behavior, ignoring both generation/farm protection, owned/named/exclusion safety, threshold boundaries and attribute types, invalid values, exact-rule precedence and config roundtrip. They do not remove animals.
- Browser regression covers 120 entity types, scroll bounds and filtering, Create/Edit without saving, policy save/edit/reset, radius zero and mobile layout.
- A copied official 1.22.7 server with the Suite, unchanged NOS 0.9.0 and a selected modpack passed 17 integration checks, including API/disk policy persistence, preservation by older updates, invalid-input rejection, reset/deletion, existing NOS controls and real-terrain A* checks. New API rules used an unregistered fixture namespace rather than modpack animals. [Sanitized result](validation/animal-generation-1.2.47.json).

These checks do not establish real-world raccoon-dog population tuning or compatibility with every custom animal attribute scheme. Inspect actual registered codes and protection reasons before choosing production caps.
