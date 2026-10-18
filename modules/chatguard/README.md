# Newfie's ChatGuard 2.1.4

Current fixes: [current release notes](../../CHANGELOG.md).
Standalone chat moderation for Vintage Story 1.22.7. The moderation engine remains server-authoritative; the optional client side adds a staff-only in-game GUI and is not required for normal players.

ChatGuard does **not** require Newfie's Server Suite. When Server Suite is installed, ChatGuard exposes its status, configuration, recent actions, active mutes and warning state to the Suite through the Vintage Story event bus; there is no hard assembly dependency between the projects.

## Included protections

- flood/spam detection
- repeated-message detection
- excessive-caps detection
- link/domain filtering with allowlist
- mention-spam detection
- message-length limits
- configurable blocked words/phrases
- warning escalation
- temporary and permanent chat mutes
- optional kick actions
- staff/player exemptions
- rotating JSONL moderation audit with background writes

## Modes

- `Enforce` — configured actions are applied.
- `LogOnly` — rule matches are recorded but automatic moderation actions are not applied.

Manual staff moderation remains explicit even while automatic rules are in LogOnly mode.

## Staff commands

All current staff commands require the `controlserver` privilege.

- `/chatguard status`
- `/chatguard gui` — opens the optional Shift+G standalone staff GUI when the client component is installed
- `/chatguard reload`
- `/chatguard mode enforce|logonly`
- `/chatguard recent [count]`
- `/chatguard help`
- `/cgmute <player> <duration|perm> [reason]`
- `/cgunmute <player|uid> [reason]`
- `/cgwarn <player> [reason]`
- `/cgwarnings <player|uid>`
- `/cgclearwarnings <player|uid> [reason]`
- `/cgmutes [count]`
- `/cginfo <player|uid>`

### Human-friendly mute durations

`/cgmute` accepts plain minutes for backward compatibility, or compact durations:

- `30m` — 30 minutes
- `2h` — 2 hours
- `3d` — 3 days
- `1w` — 1 week
- `perm`, `permanent`, or `forever` — permanent mute

Temporary manual mutes are capped at 30 days. Use `perm` for a permanent sanction. New mutes require the target to be online so ChatGuard can bind the record to the authoritative PlayerUID. Existing moderation records can later be inspected/cleared by known player name or UID.


## 2.1.1 admin interface upgrade

- ChatGuard is now a Universal mod with `requiredOnClient: false` and `requiredOnServer: true`: the server moderation engine remains mandatory on the server, while ordinary players do not need the client side installed.
- Added the optional standalone **Shift+G** staff GUI with Overview, Moderation, Rule Tester, Rules, and Activity views.
- Added `/chatguard gui` as an alternate GUI launcher.
- Every GUI action is re-authorized server-side with `controlserver`/root; the client is never trusted to grant moderation authority.
- Added a dry-run Rule Tester that evaluates the live rule configuration without warnings, mutes, kicks, blocks, audit writes, or counter changes.
- Added per-rule session hit counters and online-player context for spam/repeat/exemption testing.
- Extended the Server Suite event-bus surface with rule telemetry, online-player context, and dry-run test results so the Suite WebPanel uses the same ChatGuard engine.

## 2.1.1 Server Suite integration parity

- Extended the event-bus control surface for permanent mutes, warning clearing, reasoned unmute, and the full 30-day temporary mute ceiling.
- Server Suite can now mirror the standalone 2.1 moderation workflow without bypassing ChatGuard's authoritative PlayerUID binding.
- Suite-originated manual mutes now contribute to ChatGuard mute counters consistently.

## 2.1.0 moderation improvements

- Added human-friendly mute durations and permanent mutes.
- Added `/cgmutes` to list active mutes with remaining duration, actor and reason.
- Added `/cgwarnings` to inspect a player's active warning escalation state.
- Added `/cgclearwarnings` with an audited reason.
- Added `/cginfo` for a compact mute/warning summary.
- `/cgunmute` now accepts an optional reason and can target persisted records by name or UID.
- Manual mutes now contribute to ChatGuard mute counters consistently.
- ChatGuard event-bus snapshots now expose warning state (`warningsJson`) for Suite/in-game GUI integration.
- Permanent mute state is persisted explicitly and survives restarts.

## Files

- `VintagestoryData/ModConfig/NewfiesChatGuard.json` — configuration
- `VintagestoryData/ModConfig/NewfiesChatGuardState.json` — active mutes and warning escalation state
- `VintagestoryData/ModData/NewfiesChatGuard/audit.jsonl` — moderation audit log

The audit message preview length is configurable and can be set to `0` to avoid storing message text in the audit file.


## Command/chat isolation

Slash commands are excluded from ChatGuard message moderation so command traffic does not count toward chat spam/repeat/content enforcement.

Build from the full source tree with its `../Shared/Storage/` helpers present. They compile into ChatGuard and introduce no shared runtime DLL.
