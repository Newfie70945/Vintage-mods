# Current release changes

## Server Essentials Suite 1.2.50

- Marked Block ID Repair as experimental, with a persistent use-at-your-own-risk and backup notice.
- Automatic world backup is optional and stays enabled by default. Disabling it displays a manual-copy warning and requires acknowledgement in the panel or command; this is enforced again before applying.
- Replacement destinations accept a numeric ID or exact `domain:code`. Preview shows the resolved code and world ID; startup refuses changed mappings or mismatched codes.
- Bound backup, acknowledgement, air and valid-source choices to the reviewed plan. Editing inputs or refreshing after an edit cannot re-enable a stale staging token.
- Job history and failure messages distinguish verified automatic backups from unverified manual copies. Temporary repair codec buffers are released after failed preflights too.
- Existing Owner permissions, startup-only application, block-entity protection and layer/format checks remain. NOS and companion mods are unchanged.
