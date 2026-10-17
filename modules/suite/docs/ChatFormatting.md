# Suite 1.2.41 — Chat formatting

- Added server-side inline chat Markdown: bold, italic, combined bold/italic, inline code and nested styles.
- Added optional six-digit hex text colors and clickable HTTP/HTTPS links.
- Added webpanel Chat Display toggles and a role-code allowlist, with a formatting reference table.
- Preserved chat routing, commands, sender/role styling and older Shift+L settings saves.
- Updated ChatGuard to 2.1.2 to moderate visible formatted text and link destinations without duplicate spam/repeat samples.

## ChatMarkdown2 update

Use `/chatformat` for a private in-game syntax guide with current role, group, color and link availability. It requires a player with normal chat permission and remains available when formatting is off.

Nested colors are balanced and restore the outer color. Touching markers such as `**bold with *italic***` and `*italic with **bold***` now work. Link labels restore escaped symbols, URLs can contain balanced parentheses, and backslashes inside code stay literal. A shared 65,536-step delimiter-scan limit bounds malformed input in addition to the existing limits.

Replace the Suite with the ChatMarkdown2 build; keep ChatGuard 2.1.2. No settings migration is required. See `CHAT-MARKDOWN2.md` in the release for installation and validation details.

## Installation

Stop the game server, replace the existing Suite ZIP and ChatGuard ZIP with these builds, then start the server. Keep existing config and data files. Do not install two versions of either mod. Suite remains 1.2.41; website and connector are unchanged.

No extra client mod is required for chat formatting. Existing clients with ChatGuard's optional staff GUI can keep it; server moderation uses the updated server module.

## Settings and syntax

Open **WebPanel → Chat Display → Message Formatting**. Basic Markdown is enabled for General chat by default. Colors and links default to off. Non-General groups keep their existing opt-in scope. Disable Markdown to restore literal messages. An empty role-code allowlist permits everyone; otherwise only the exact listed role codes may format messages (case-insensitive).

Stored in `ModConfig/NewfiesServerEssentialsSuite/NewfiesChatDisplay.json`:

```json
{
  "MarkdownEnabled": true,
  "MarkdownAllowColors": false,
  "MarkdownAllowLinks": false,
  "MarkdownAllowedRoleCodes": []
}
```

These are additions to the existing config, not a replacement for the complete file. Saves from older Shift+L clients preserve these fields.

| Style | Input |
| --- | --- |
| Bold | `**text**` or `__text__` |
| Italic | `*text*` or `_text_` |
| Bold italic | `***text***` or `___text___` |
| Code | Surround text with a single backtick |
| Nested | `**bold with *italic* inside**` |
| Color (opt-in) | `[color=#80C8FF]text[/color]` |
| Link (opt-in) | `[Community](https://newfiescommunity.com)` |
| Literal symbols | Escape a formatting symbol with a backslash |

This is an inline Markdown subset, not a full document renderer. Underline, strikethrough, headings, images and raw HTML/VTML are not enabled. Unmatched delimiters remain literal. Underscores inside player/item identifiers remain literal. Inline code does not interpret formatting tags.

## Moderation and limits

Install the supplied ChatGuard 2.1.2 together with the Suite when using ChatGuard. Its matching receives the visible text, including link destinations. The presentation query shares the same short-lived decision across raw input, formatted output and timestamped messages regardless of handler order; a message is not counted repeatedly as spam because it was formatted. ChatGuard still works standalone without the Suite.

Formatting is bounded to 4,096 input characters, 64 spans and eight levels of nesting. Longer messages are escaped without Markdown processing; this formatter does not raise the server's chat length limits. URLs are limited to 512 characters and HTTP/HTTPS, rejecting credentials, controls and markup characters. Player-entered text never supplies VTML tags or changes sender names/roles. The formatting cache holds at most 1,024 sender/channel entries and only reuses entries for 100 ms.

## Validation

- Suite Release compiled against Vintage Story 1.22.7; ChatGuard Release compiled successfully.
- Parser tests: nesting, literal symbols/code, raw markup escaping, URL rules, malformed input, bounds and pathological input.
- Chat handler tests: command pass-through, moderation blocking, routing payload preservation, group/role scope and old/new settings saves.
- Compiled Suite + ChatGuard checks: actual game VTML parser, canonical moderation, timestamp and handler-order deduplication, blocked terms spanning styles.
- Webpanel browser checks: desktop/mobile controls and save payload roundtrip.

Validation uses API fixtures and Chromium, not a live player/server session. Stock rendering support was checked against the game's VTML implementation; please confirm the appearance in game after installing.


## 1.2.43 UI2 — owner-defined chat layout

Open WebPanel → Chat Display → Player Chat Format. The default layout is `{role} {name}: {timestamp} {message}`. Templates support each placeholder once; `{name}` and `{message}` are required. Examples:

- Time first: `{timestamp} {role} {name}: {message}`
- Time after rank/name: `{role} {name} {timestamp}: {message}`
- Custom prefix/suffix: `[Global] {role}  {name}: {message} · Community`

Role and timestamp placeholders include their brackets and follow the existing visibility toggles. When disabled, one following separator space is omitted. You can choose any spacing between the displayed rank and name. Other template text is literal; HTML/VTML tags are not executed. Templates are single-line, at most 512 characters, and reject unknown or duplicate placeholders. Player-written message text is never reparsed as a layout.

Saving an invalid layout leaves the previous layout active. Older Shift+L settings saves preserve this setting. Use the matching UI2 Suite/ChatGuard/Discord files so relays and moderation receive the original message body, without template prefixes or suffixes. Commands and non-opted-in chat groups remain unchanged. The private `/chatformat` guide still describes message Markdown; owners configure layout separately.
