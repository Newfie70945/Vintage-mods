# Community MOTD 2.1.5

- Dispose replaced GUI composers/textures on tab changes and live refreshes.
- Reuse one dialog across repeated MOTD requests instead of leaving each window registered in the client GUI list.
- Release textures when closed; remove dialog registration and temporary logo files on client shutdown.
- Preserve current packet/read-state handling and live-refresh scroll positions.

This is client-side resource cleanup. Server feed/logo behavior and configuration are retained.
