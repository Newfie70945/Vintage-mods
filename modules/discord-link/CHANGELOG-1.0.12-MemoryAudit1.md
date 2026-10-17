# Discord Integration 1.0.12

- Remove expired link codes, issuance cooldown records and failed-attempt histories, including idle cleanup every minute.
- Cap pending codes at 1,024 and temporary issuance/failure identities at 4,096. Preserve active cooldowns and rate limits when busy.
- Unregister linking maintenance and clear temporary codes on shutdown.
- Verified expiry, busy limits, valid-code validation and cooldown behavior with an injected clock; no Discord network requests were sent.

Persistent account links and role mappings are preserved. This fixes source-level long-session retention; it does not establish the cause of a production server's full memory footprint.
