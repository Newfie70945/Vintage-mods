# Shift+L administration workspaces

The suite opens to a Home page with one card for each administration workspace. Use the workspace selector at the top to move between Block Logging, Animal Limiter, Profiler, Login Protection, Integrations, Anti-Cheat, and Player Admin. Home also provides direct access to player risk reports and block history.

Each workspace remembers its last selected page for the current game session. Returning to a workspace refreshes its data through the existing server requests. Blue selected buttons identify the active page. The Home button remains available from every workspace.

Block Logging uses a narrower window and separates its settings into three pages:

- **Logged events:** block, container, and entity event switches.
- **Storage & writer:** retention, flush timing, queue capacity, and overflow capacity.
- **Query defaults:** default and maximum search radii and result counts.

The BlockLogger master switch remains visible on all three settings pages. Switch changes save immediately; numeric values retain their individual Save buttons.

Anti-Cheat client settings use a compact card for scanner policy and a separate card linking to packet protection. Anti-Cheat settings retain their existing explicit Save action. Diagnostic tables and packet settings keep their wider layouts so their columns and explanations remain readable.

The packet-protection header displays the server's vanilla packet ingress status. Green indicates active coverage; red indicates that coverage was enabled but failed to become active. Hover over the status to read the full server reason. An awaiting-data status is shown until the server reports its state. This is read-only runtime information.

Audit and investigation results have alternating row surfaces, a visible row range, a scrollbar, and Previous/Next page controls. Mouse-wheel scrolling works in every generic results pane, including Block Logging and Login Protection. Inventory slot viewers retain their own scrolling controls.

All actions continue to use server privileges and the existing destructive-action confirmation rules. Workspace navigation changes the client layout; it does not grant permissions or change packet protocols.

## Validation

The native components are compiled against Vintage Story 1.22.7. All original BlockLogger and Anti-Cheat client-settings control IDs remain represented. New workspace strings are included in English, German, French, and Russian.

The [Home preview](shift-l-home-preview.png) and [settings preview](shift-l-settings-preview.png) are native Cairo composition previews generated without a running game client. They show the shell, card positions, typography, and control layout. They do not exercise in-game input, hover tooltips, GPU texture rendering, or display scaling. These interactions still need a client smoke check at the server's usual screen resolution and GUI scale.

The [preview harness](shift-l-preview-harness.zip) contains the renderer project and reproduction instructions. Its ZIP packaging keeps the renderer source out of the mod's normal compilation.

Suggested client smoke check: open and close Shift+L; visit every workspace and page; switch away and return; inspect audit rows and inventory scrolling; save one reversible numeric setting and restore it; confirm that denied actions still display the server's permission response.
