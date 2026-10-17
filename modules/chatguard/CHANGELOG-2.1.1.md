# Newfie's ChatGuard 2.1.1

## Server Suite integration parity
- Extended the event-bus control surface so Server Suite can manage permanent mutes as well as temporary mutes.
- Added event-bus warning clearing so Suite WebPanel controls match `/cgclearwarnings`.
- Suite-originated unmute actions now preserve and audit the supplied reason.
- Suite-originated mute actions now increment ChatGuard mute statistics and use the same 30-day temporary-mute ceiling as standalone commands.
- New Suite mutes still require the target to be online so sanctions bind to the authoritative PlayerUID.
- Existing standalone commands and ChatGuard 2.1.0 behavior remain compatible.


## Retained admin interface polish

## Added
- Optional staff-only standalone in-game admin GUI on Shift+G and `/chatguard gui`.
- Overview, Moderation, Rule Tester, Rules, and Activity views.
- Safe dry-run message testing using the live ChatGuard rule engine without sanctions, audit writes, or counter mutations.
- Per-rule session match counters.
- Online-player context in the integration snapshot for exemptions and live spam/repeat testing.
- Server Suite WebPanel support for the Rule Tester, rule-hit telemetry, protection posture, online player suggestions, and activity filtering.

## Changed
- Mod metadata is Universal with `requiredOnClient: false` and `requiredOnServer: true`; server moderation still works for players who do not install the client component.
- All standalone GUI requests are re-authorized on the server with staff privileges.

## Compatibility
- Vintage Story 1.22.7.
- Server Suite integration is compatible with 1.2.34.
