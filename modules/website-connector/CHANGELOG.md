# Website Connector changelog

## 1.1.1

- Fixed stale game-version reporting by reading constants from the game API loaded by the running server, rather than embedding the build machine's version.
- Added the detected game version to `/websitestatus`.
- Cached the installed-mod metadata at startup/reload instead of enumerating it every ten seconds.
- Added specific, safe configuration-validation messages without logging tokens or raw config.
- Cleaned up the listener and tick callback when startup fails.
- Updated the mod, assembly, status and health versions together.

## 1.1.0

- Added optional website/game-account verification with `/websitelink CODE` using the server-authenticated player identity.
- Added bearer-protected challenge/status POST routes; no HTTP route can confirm a game identity.
- Bounded ten-minute challenges, limited creation and guesses, and prevented confirmation reassignment.
- Added `AccountLinkingEnabled` (default true); restart/reload clears temporary codes.
- Included cached mod IDs and versions in the public status snapshot for joining information.
- Added HTTP/core checks for proof privacy, replay, expiry, guesses, body validation and disabled linking.
- Keeps the existing configuration, token, listener defaults and Suite visibility controls.

# Website Connector 1.0.0

- Added a standalone server-only companion mod; the Suite DLL is unchanged.
- Added a separate authenticated read-only listener for the community website.
- Connected public players, map terrain, waypoints, playtime and XSkills rankings.
- Added cached game status and accurate online counts with hidden map players.
- Added an owner-maintained supporter board and reload/status admin commands.
- Preserved Suite public visibility rules and removed internal IDs/admin fields.
- Added bounded requests, response sizes, timeouts and shared snapshot caching.
- Updated the existing website backend with server-side connector variables.
- Preserved website design, existing forum storage, sign-in and private access.
