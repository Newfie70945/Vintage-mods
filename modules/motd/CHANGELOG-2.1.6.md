# Community MOTD 2.1.6

- Improve the in-game Announcements and Updates typography: section labels, larger neutral headings and body text, muted metadata, wider inner margins and separated entries.
- Honor local/Discord feed merging, with local fallback when no remote posts exist.
- Deduplicate remote entries and order newest first within a bounded candidate list and configured item limit.
- Keep feed timestamps explicit in UTC and improve spacing/source attribution.
- Include stable local announcement/update IDs in unread tracking; local edits can appear as NEW.
- Keep unread IDs in sync with displayed entries and avoid unread markers in Events.

The Suite CENTER1 editor adds a plain-text post composer without changing the MOTD packet/config format. Existing local VTML remains supported. Verified using the official 1.22.7 runtime in disposable fixtures; no live Discord connection was used.
- Hash missing or unsafe feed IDs so unread markers remain valid even when titles contain brackets.
