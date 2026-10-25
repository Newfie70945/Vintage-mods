# Suite 1.2.51 · Performance and live map improvements

## Performance pages

Performance Center, Tick Debugging, Profiler & Lag, Memory & GC, Optimizations and Suite Health now share navigation, a panel jump menu, refresh controls and an update-age indicator. Long tables scroll within their panels. The Performance timeline appears above Suite Timing Hotspots.

Use **Auto refresh** to update the current page without enabling the global Live button. Choose a 5–60 second interval; Optimizer status has a 30-second minimum. Preferences and the timeline range are saved in this browser. With no page preference, automatic refreshing follows global Live Updates.

Automatic updates stop in hidden tabs, while editing fields and under Safe Diagnostics Mode. The Performance Center refreshes its summary AND timeline without fetching BlockLogger history, scanning entities or inspecting Harmony. Optimizations refreshes NOS status and preserves configuration drafts. Other providers retain their explicit refresh controls. Suite Health stays manual because it runs database/subsystem checks. Refreshing readings never starts a profiler capture, entity scan or heap capture.

**Refresh readings** updates lightweight readings; existing full refresh and capture buttons remain available for deliberate investigations. A failed refresh retains the last chart. Update age is response age, not proof that server diagnostics are enabled or that a new sample was recorded.

## Live webmap exploration

Open **Web Map → Export Schedule → Live explored terrain**, enable automatic updates and click **Save Export Schedule**. Recommended starting settings: **120 seconds**, **4 tiles per pass**. Automatic updates are off by default.

An initial compatible exported map is required. The direct save-database terrain reader must be enabled. Each pass checks saved coverage and renders at most the configured 1–16 base tiles. The interval is 30–3600 seconds, checked on the existing 30-second map clock. A running pass never overlaps another export.

Exploration must reach the normal world save first. It can take a save plus multiple update intervals to catch up with a large backlog. Updates are incremental publishing, not a second-by-second stream. Existing block edits remain in the dirty journal for **Update Changed Terrain**; automatic exploration does not acknowledge possibly unsaved edits.

Automatic passes never generate terrain, load live world chunks or escalate to a full rebuild. They preserve queued startup exports and do not suppress normal entity audit/error logging. If saved terrain cannot be decoded, the existing map stays published. Newly explored columns inside an existing tile are detected too. Failed passes retry on a later interval. Disabling the setting stops new passes; switching the map off cancels a running export.

Clients replace changed base tiles and their affected pyramid tiles while keeping other cached images and the current camera. A missed revision safely clears the client cache. Rendering, saved-coverage discovery, overview and pyramid publication still consume CPU and disk; keep the interval and tile limit conservative on a busy server.

Suite remains **1.2.51**, build **ms11-performance-map1**. Other modules, including NOS, are unchanged.
