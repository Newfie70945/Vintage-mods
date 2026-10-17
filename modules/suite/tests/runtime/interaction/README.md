# Interaction regression check

Uses the production guard with real engine player/packet objects and read-only API proxies; it does not start a game world. Set `VINTAGE_STORY` to the official 1.22.7 install and `NSS_ASSEMBLY` to the rebuilt Suite DLL, then run:

```sh
dotnet run --project InteractionRuntimeSmoke.csproj
```

It checks normal interaction branches, distant/cross-dimension/world-bound targets, unloaded chunks, and missing/non-finite server positions. Unexpected world/API calls throw, so a passing check does not request chunk loads.

To generate an isolated field-layout API fixture (never install it in the game), run `dotnet run --project references/ReferenceFixture.csproj -- /tmp/new-field-api`. Change `VINTAGE_STORY` to that directory and repeat the smoke check. The generator retains the original getter methods for engine binary references but exposes `Pos` as a field for the compatibility accessor.

Optionally append the prior ApiCompat1 release ZIP path and a new temporary binary directory to the generator command. It rewrites the prior guard's two direct getter instructions into stale `Entity.Pos` field reads. Against the official property-layout API, run the smoke check on that reconstructed binary with `--expect-field-failure`; this reproduces the uploaded log's MissingFieldException. This is an ABI reproduction fixture, not an inspection of the user's installed DLL.
