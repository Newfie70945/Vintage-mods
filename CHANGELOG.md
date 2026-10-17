# Listing update and Connector exclusion

- Removed the standalone Website Connector project, solution entry, package-manifest entry and associated CI step at the owner’s request.
- Retained its source in a local backup outside the repository.
- Retained required shared storage code, source tests, packaging tools and GitHub workflows.
- Updated ModDB HTML separately; listing HTML remains outside this repository.

# Original-source restoration and repository cleanup

- Restored original Suite and companion author sources from the uploaded CENTER1 archive.
- Replaced temporary decompiled projects while preserving current Suite 1.2.44 and MOTD 2.1.6 fixes.
- Added AntiCheat Client, ChatGuard, Discord Link, Website Connector, Player List and shared source.
- Consolidated modules under stable names, updated shared/test paths and added a central module manifest.
- Added one build/package entry point for all seven regular mods.
- Excluded the standalone website and ModDB descriptions at the owner's request.
- Kept historical source notes and protected earlier work in Git history and a local backup bundle.

# Suite 1.2.44 and Community MOTD 2.1.6

This changelog covers this improvement pass against the recovered public Suite 1.2.43 and MOTD 2.1.5 baselines. It does not claim to include work lost with the earlier workspace.

## Server Essentials Suite 1.2.44

- Fixed Shift+L online-player pickers targeting visible roleplay names instead of account identities. Actions now send player UIDs; names remain presentation labels.
- Kept players with identical roleplay names as separate choices and added UID labels to disambiguate them.
- Fixed selecting a player from an ADMINPLAYER result row to retain its UID.
- Improved the webpanel MOTD news/update post builder with consistent titles, date/source details, spacing and separators.
- Added simple dash/star bullet-list formatting to posts created with the builder.
- Escaped ampersands, angle brackets and quotes in post-builder text so text is not interpreted as VTML markup.
- Fixed `/nsefeatures` replies throwing a null-reference exception when run from the server console.
- Updated assembly, manifest and runtime build identifiers to 1.2.44 / ms11-motd-roleplay1.
- Repaired six C# expressions damaged during source recovery so the reconstructed project builds against the official 1.22.7 assemblies.

## Newfies Community MOTD 2.1.6

- Improved news, updates and events with page introductions, stronger post titles, quieter metadata, more space between sections and dividers between feed entries.
- Removed the generic outer Server Announcement/Server Update heading from owner-written content, avoiding duplicate titles.
- Preserved existing owner VTML and the NEW/read-state identifiers used by the client.
- Normalised CRLF/CR line endings and `[br]` markers on all content tabs.
- Displayed Discord post timestamps explicitly in UTC; malformed optional dates no longer interrupt formatting.
- Honoured MergeLocalAndDiscord: enabled combines both sources; disabled prefers available Discord posts and keeps local content as the empty/disabled-feed fallback.
- Kept a maximum of 50 remote entries, validated the accent colour and allowed only HTTP(S) original-post links in the formatter.
- Updated assembly, manifest and runtime version reporting to 2.1.6.

## Listing material

- New Suite and MOTD ModDB HTML descriptions, both below 60,000 bytes.
- Documented installation, client requirements, companion boundaries and the scope of roleplay-name compatibility.
- Used the community hosting date of 4 June 2023.

The optional request for separate Player List account/RP-name display toggles is not implemented in this pass. No standalone Anti-Cheat, Discord Link, Player List, website or optimizer release is included.
