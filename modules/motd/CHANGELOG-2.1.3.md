# Community MOTD 2.1.3

- Check file size before reading server logos.
- Validate the full PNG signature and cache immutable bytes/hash until the file changes.
- Fetch each logo once per MOTD packet.
- Assembly metadata now matches the module release.
