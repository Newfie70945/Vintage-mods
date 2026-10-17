# ChatMarkdown2 validation

Build the Suite and ChatGuard Release projects against Vintage Story 1.22.7 first. Set `VINTAGE_STORY`, `NSS_ASSEMBLY` and `CHATGUARD_ASSEMBLY` to the game folder and the absolute paths of the compiled DLLs, then run:

```sh
dotnet run --project ChatMarkdownSmoke.csproj -- /tmp/nss-markdown-panel.html
node webpanel.cjs /tmp/nss-markdown-panel.html
```

The browser check requires Playwright and Chromium (`/usr/bin/chromium`). The runtime check uses the actual compiled modules and game types with a disposable API fixture. It verifies `/chatformat` registration and live private guide output in addition to canonical moderation and the stock VTML parser. It creates no live world, sends no external messages and cleans its fixture directory.

The lightweight parser/chat-handler test is in `../../chat/ChatCommandBoundaryTests.csproj`. It checks escapes, nested styles, malformed/oversized input, URL restrictions, raw VTML escaping, role/group scope, moderation and settings preservation.
