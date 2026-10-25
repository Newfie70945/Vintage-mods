# Suite 1.2.51 · ms11-proximity-chat1

## Changes

- Added optional server-side proximity chat under WebPanel > Chat Display > Local / proximity chat.
- Configurable dedicated group name, three-dimensional hearing radius (1–1,000 blocks) and automatic membership on connection. Defaults: disabled, `Local`, 100 blocks, auto-join enabled.
- The owned group uses existing Markdown, timestamps, role/name colors and nicknames without enabling formatting for every third-party group.
- Native player-chat processing and moderation run before filtered delivery. Native sender echo and server chat logging remain; commands, General and unrelated groups retain their original paths. The existing Discord relay forwards only General, so it does not relay the local group.
- Same-dimension distance and membership checks control receivers. Missing sender information, disabled routing or detected conflicting delivery patches do not fall back to an unrestricted group broadcast.
- No separate chat-history database, per-chat configuration writes, periodic position scan or world-save handler. Group/membership metadata uses native persistence; the new group starts with no replayable history.
- Existing groups/history are not adopted. Owned renames preserve group identity. Older client settings requests preserve new proximity fields.

## Replacing The BASICs proximity chat

1. Replace the previous Suite ZIP with this 1.2.51 build while stopped, then start the server.
2. Open **Chat Display > Local / proximity chat**. Enable it, choose an unused name (`Local` by default), set a hearing range and choose auto-join.
3. Click **Save Settings**. The status should show **Active** and the new native group ID. Existing online players are added when auto-join is enabled; future players join when entering play.
4. Players select the **Local** tab to speak locally. General remains global. With auto-join off, join through `/group join Local` (substitute the chosen name).
5. Verify with actual players inside/outside the radius and across dimensions. Once satisfied, stop the server, remove The BASICs, and restart. Retire any old proximity tab that remains: the Suite filters its own group only. No old group/history is imported or deleted automatically.

Removing The BASICs removes its own chat-history save handler. This implementation has no replacement history-save handler, but this test does not measure the live server's resulting autosave reduction.

Auto-join re-adds players on reconnect; leaving the owned group lasts for that connection. Disabling proximity retains its group/membership metadata but rejects local player messages. Name changes use one word (maximum 32 plain characters) so native group commands work. Built-in channel names are rejected. Native server logs still record messages, as normal for server chat.

## Validation

- Release build: zero warnings and errors on official Vintage Story 1.22.7.
- Existing chat parser/Markdown/layout/command/moderation/failure regressions pass, plus local formatting, disabled routing, command preservation and older-client settings preservation.
- 20 checks against the official binary API: actual patched native delivery, radius boundary/vertical distance/dimensions, membership, sender exclusion, unchanged metadata/formatting, General/foreign groups, unknown sender, disabled routing, auto-join/group packets, stable rename, collision refusal, late conflict blocking, empty history and non-finite coordinates.
- These routing tests use controlled player doubles and intercepted transport, not authenticated network clients or player-capacity benchmarks.
- Browser checks pass at 1,440, 390 and 320 pixel widths: save/apply roundtrip, empty-radius validation, status/group ID, field bounds and read-only controls.
- Disposable official server: startup creation, HTTP configuration/rename, disable/re-enable with stable ID, login, concurrent header requests and clean shutdown/database integrity passed. Owned group/settings persisted with zero history entries. This server had no authenticated players.
- The last two guarded delivery/command changes were additionally rebuilt and rerun through the routing/formatting checks; they do not change the tested group persistence format.

Suite version remains **1.2.51** as requested. NOS remains **0.20.13-preview** and is unchanged by this feature.
