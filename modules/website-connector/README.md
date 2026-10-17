# Connect this website through a separate server module

This website runs on your Nginx/PHP host or the alternative Node.js 24 backend. Install **Newfies Website Connector 1.1.1**
on the Vintage Story server alongside Suite 1.2.37 or newer. It is a separate
server-only mod; players install nothing. The Suite DLL and its settings are unchanged.

```
Visitor → website backend → authenticated Website Connector → Suite public map listener
                           ↳ cached game status and owner-maintained supporters
Forum → website database (unchanged)
```

## 1. Install the mod

Install the Website Connector mod ZIP from the Suite bundle into the server's `Mods` directory and
restart. Do not extract the ZIP into a second nested Mods directory. On first
RunGame startup the mod creates `ModConfig/NewfiesWebsiteConnector.json` and a
random 64-character `AccessToken`. Keep that token on the server and website backend.

The defaults listen on `127.0.0.1:9466` and read the Suite public map listener
at `http://127.0.0.1:9465`. Enable the Suite's Web Map and set its public port to
match `SuitePublicOrigin`; this is the map listener, not the admin dashboard port.

## 2. Give the website backend access

A hosted website cannot reach your server's `127.0.0.1`. The recommended setup is
an HTTPS reverse proxy at a hostname such as `connector.your-domain.example`
forwarding to `http://127.0.0.1:9466`. This preserves the module's loopback default.
Forward the `Authorization` header. Permit GET reads and authenticated POST only on `/api/link/challenge` and `/api/link/status` for optional account verification; deny all other writes. Do not point this proxy
at the Suite admin panel. An example Caddy configuration:

```
connector.your-domain.example {
    reverse_proxy 127.0.0.1:9466
}
```

If deliberately using direct HTTP, set `BindAddress` to your server's interface
(e.g. `0.0.0.0`) and permit its configured port through the firewall. Set
`WEBSITE_CONNECTOR_ALLOW_HTTP=true` on the website backend to opt into that
transport. HTTPS keeps the bearer secret encrypted in transit.

## 3. Configure the existing website

Set these **server-side website variables** in private `website.config.php` for PHP, or `website.config.json`/environment variables for Node.js, rather than public JavaScript:

- `WEBSITE_CONNECTOR_ORIGIN`: the connector's reachable HTTPS origin, with no path.
- `WEBSITE_CONNECTOR_TOKEN`: the mod's `AccessToken`, exactly as saved in its config.
- `SERVER_ADDRESS`: your game server address (already `172.93.104.60:42420`).

The website changes to connector mode as soon as the origin and secret are set.
A configured connector failure does not fall back to unprotected Suite routes.
Until configured, the website keeps using its existing public Suite feed and
existing supporter snapshot. Forum accounts and posts are stored in the standalone website database. The source handoff contains no live forum database export, so deployment does not automatically transfer records from the old hosted preview.

## 4. Supporters and refresh rates

Edit the mod's `Supporters` list with real, owner-approved names, tiers and dates:

```json
"Supporters": [
  { "Name": "Actual player name", "Tier": "Supporter", "Since": "October 2026" }
]
```

The default list is empty. These are explicit acknowledgements, not automatic
purchases, role grants or inferred donors. Run `/websitereload` after edits.
The website supporter board refreshes every 60 seconds, with a 15-second backend
cache. `SnapshotCacheSeconds` controls the module's cache (5–300 seconds; default
15). Game status is sampled every 10 seconds. Public map player visibility,
waypoint visibility and leaderboard enablement still follow the Suite settings.

## 5. Verify

`/websitestatus` reports the listener and whether a Suite read has succeeded,
without displaying the token. Test the authenticated `/api/health` and `/api/map`
through your reverse proxy using an API client with the bearer header. Missing or
wrong secrets return 401; admin/config routes return 404; unsupported writes return 405.
Website `/api/status` reports `connectorConnected` and `suiteConnected` separately.
The online count works even when map player names and coordinates are hidden.

The standalone website is public on its configured allocation/domain. Reading community pages does not require an account; posting requires a community account. The admin page requires the website owner account. No platform identity headers are trusted.

For startup, persistence and owner setup, read [README.md](README.md).


## 6. Optional game-account verification (Connector 1.1.1)

Replace the existing Connector mod ZIP rather than installing two copies of the same mod. Keep `ModConfig/NewfiesWebsiteConnector.json` and its token. `AccountLinkingEnabled` defaults to true; set it false to disable the feature.

The website creates a private, authenticated challenge through POST `/api/link/challenge`. A signed-in member receives only a short confirmation code and runs `/websitelink CODE` in the game server. The command obtains the player UID directly from the server-side caller. The website retrieves the result through authenticated POST `/api/link/status`; there is no HTTP route that confirms a player identity. Both POST routes accept small JSON bodies and require the existing bearer token. Never expose that token to browser JavaScript.

Challenges expire in ten minutes, are bounded and rate-limited, and disappear on Connector restart or `/websitereload`. Only one website account can link a game UID. Members can unlink their own account. Linking supplies a verified forum badge; it never grants owner/moderator permissions and does not publish the game UID. The Connector caches the game’s mod IDs/versions for the public joining information alongside its status snapshot.

## 1.1.1 game-version reporting fix

Replace the existing Connector ZIP and restart the game server, keeping its configuration and token. The game version is read from the loaded game API instead of being embedded from the build reference. `/websitestatus` shows the detected version. Website 2.7.3 also keeps connector outages from substituting directory versions.

To build your own release on Linux, set `VINTAGE_STORY` to the game installation and run `./build.sh` with .NET 10 and Python 3 available. It builds, runs the connector checks and writes clean compiled/source ZIPs in `releases/`. The compiled mod ZIP contains its DLL at the root and excludes the game API assemblies and live configuration.
