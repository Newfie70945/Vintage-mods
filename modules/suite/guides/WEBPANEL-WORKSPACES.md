# WebPanel page workspaces

Use **Find a page** in the sidebar to filter by page title or common investigation terms such as inventory, offline, packet or lag. Enter opens the first match; Escape clears the filter. Existing navigation groups remember their expanded state.

The toolbar above each page includes:

- **On this page:** jump to a visible panel, automatically expanding it.
- **Compact:** reduce card/table spacing across pages.
- **Focus page:** give the page the desktop navigation's space; select Exit focus to restore it.
- **Refresh page:** request the current page's existing refresh action.

Cards with a titled header have an accessible expand/collapse button. Compact, focus and collapsed-card preferences persist in this browser. They affect presentation and send no server mutations. Existing account roles, controls and server authorization continue to apply.

Dashboard shortcuts open Anti-Cheat player investigation, Player Administration inventory inspection and Performance Center. Online/offline inventory inspection identifies evidence source, read/capture time and partial data. Saved offline inventories remain read-only. Changing the top-level page preserves nested investigation tabs.

Mobile controls wrap to the available width, keep usable touch heights and use the existing navigation drawer. Desktop focus mode is hidden on narrow screens.

## Layout previews

These Chromium screenshots render the actual embedded page with local empty API fixtures, an owner fixture account and live updates disabled. The zero counts, waiting/problem indicators and connection label are fixture output, not a running game server. They verify layout only.

- [Player Administration, desktop](webpanel-admin-desktop.png)
- [Dashboard, desktop](webpanel-home-desktop.png)
- [Player Administration, mobile](webpanel-admin-mobile.png)

The portable browser regression runner is `tests/web/test_workspace_pages.cjs`. Install Playwright in your developer environment and set `CHROMIUM` to a Chromium executable if it is not `/usr/bin/chromium`. It serves every request from local fixtures and checks search, panel selection, preference persistence, nested tabs, mobile control width, script errors and absence of mutation requests. Set `NSS_PREVIEW_DIR` to an output folder to recreate the screenshots.
