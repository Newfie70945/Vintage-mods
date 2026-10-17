# Discord Integration 1.0.13

- Fixed `/discordlink` throwing “Incomplete command — no name or required privilege has been set.” Register with normal player `chat` privilege; admin tools retain `controlserver`.
- Preserve command ownership when another mod already registers `/discordlink`.
- Pass the untouched chat line to the Suite presentation bridge so custom layouts do not leak prefixes, timestamps or suffixes into Discord chat content. Standalone relay behavior remains available.

Verified with the official VS 1.22.7 command dispatcher and disposable fixtures, including normal-player status/code/cancel commands. No live Discord connection was used.
