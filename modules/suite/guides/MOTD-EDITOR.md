# WebPanel MOTD editor

Open **Community MOTD** with the Community MOTD companion connected. Choose a content tab, edit its source and check the live preview. Formatting buttons apply to the selected text or insert a placeholder you can replace. Main follows the format selector; all other tabs use VTML. Switching format does not convert the existing source.

**Writing Mode** gives the source and preview more room while keeping presentation/join/feed settings intact. Exit Writing Mode to edit those settings. Modified content tabs show a dot. The counter shows source characters and lines. Ctrl/Cmd+B and I apply bold/italic in the editor; Ctrl/Cmd+S saves and applies.

**Save & Apply** submits the full draft, including every content tab. Automatic refresh pauses while changes are pending. Reads already in progress cannot overwrite new edits. Failed saves/refreshes retain the draft. **Revert Draft** restores the last loaded/saved configuration after confirmation; it does not write to the server. Refresh and Reload from Disk also ask before discarding unsaved work. Reset Seen preserves the draft.

The browser preview approximates the game's formatting. Logo assets, Discord/companion feeds and injected server information are visible in game. Preview links do not navigate, and embedded scripts/resources are not executed or fetched. Very large source is retained but its preview is limited. Read-only staff can view the source/preview and switch tabs without editing or saving.

The Chromium fixture `node tests/web/test_motd_editor.cjs` checks editing, preview isolation, refresh/save failure handling, draft protection and mobile/read-only behavior against the actual embedded page.
