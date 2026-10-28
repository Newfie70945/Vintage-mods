# NOS 0.20.19-preview — audited patch compatibility

This update enables the following combinations on official Vintage Story 1.22.7:

| NOS module | Audited companion | Hook preserved |
| --- | --- | --- |
| idleFirepitFastPath | Attribute Rendering Library 3.2.0, 3.1.5, 3.1.4 | Attribute redirection transpiler on firepit heating/smelting helpers |
| collisionTraversalReuse | Walkable Entities 1.0.44 | MovingFrame.Terrain postfix |
| blockUpdateListReuse | Server Essentials Suite 1.2.53, tested public binary | OreShield BeforeSetBlocksPacket prefix |
| assetCompressionCache | GeneLib 3.2.1 | SendServerAssets_Postfix |
| repulseDelegateReuse | Walkable Entities 1.0.44 | MovingFrame.Repulse prefix on WalkEntity |

No module is automatically enabled. Existing switches/defaults are preserved. NOS
does not remove or rewrite these mods' hooks. Other Suite/companion versions are
unchanged by this release.

## Why these hooks can coexist

- ARL redirects item/collectible attribute access. NOS skips only the already
  verified empty, cold, exact-native firepit case; populated/warm/custom firepits
  retain native processing and ARL behavior. Only the audited helper transpiler
  is exempted; unrelated ARL patches remain conflicts.
- Collision traversal changes the local iterator, retaining the native collision
  body and Walkable Entities' subsequent terrain postfix.
- Repulsion caches the delegate to the same native WalkEntity method, retaining
  Walkable Entities' prefix and its result. A late-patch test checks that an
  already-created callback observes subsequently installed Harmony hooks.
- GeneLib sends its additional network message after normal server assets. NOS
  caches eligible compression bytes rather than replacing SendServerAssets, so
  the postfix remains installed. Cached send buffers remain independently owned.
- OreShield filters protected positions synchronously, copies coordinates used
  for recipient corrections and leaves ordinary positions for native sending.
  NOS releases its scratch list after the native pass, not before those hooks.

## Guard boundaries

Each exception requires the expected NOS module, original declaring type/method,
patch kind, Harmony owner, handler type/name and audited assembly SHA-256. All
foreign patches are checked individually. An allowed hook cannot conceal another
unknown hook, even under the same owner. The repeating compatibility monitor uses
the same rules for late patches and removes only NOS's affected hooks on conflict.

Fingerprints are cached once per loaded assembly; the recurring check does not
rehash files. Missing/unverifiable/changed binaries remain blocked pending review.
This is patch compatibility validation, not security attestation of a mod.

Audited DLL hashes:

| DLL / release | SHA-256 |
| --- | --- |
| AttributeRenderingLibrary 3.2.0 | 8fa22db982ab466446e241a62d66f04be694d351ee2ee693f0dab494ed70a010 |
| AttributeRenderingLibrary 3.1.5 | 817ee5f024bbde2b8be218a65e368a3f3ce9acbe7494bf9678fbf762d79ee2b5 |
| AttributeRenderingLibrary 3.1.4 | df96de712ce8bc543b077fb302ca13b1e07d7ced9a4503fd81b0c7393de25b00 |
| WalkableEntities 1.0.44 | 1daaa4b1a69c270c7ccefd5aa4730820313f119d4584c67a2e312db4e8b425c6 |
| Genelib 3.2.1 | 29773f973b482186b55464d6819b9b8a136c7e2f57a8fb02c2d2db6dfe6ef6a2 |
| Suite 1.2.53 | 7da05293c81202193b588e42c284ca26b0afa2003540fe7fdb57dd6153d161ca |

## Reproducing the external-mod checks

Supply official game libraries using `VINTAGE_STORY`. Set `NOS_COMPAT_FIXTURES`
to a private directory containing the downloaded public DLLs at:

```
arl-3.2.0/AttributeRenderingLibrary.dll
arl-3.1.5/AttributeRenderingLibrary.dll
arl-3.1.4/AttributeRenderingLibrary.dll
genelib/Genelib.dll
walkableentities/WalkableEntities.dll
```

Set `NSS_COMPAT_SUITE_DIR` to the tested Suite DLL directory. Run the full optimizer
build, then run `tests/PatchCompatibilityTests` with arguments `3.1.5` and `3.1.4`
for those ARL versions. With no external fixture directory, the compatibility test
explicitly reports that only owner-spoof rejection checks ran. Native collision
and entity-hot-path tests install the published Walkable hooks when supplied.

The opt-in Suite HTTP integration accepts `NSS_WEB_TEST_PATCH_COMPATIBILITY=1`
alongside the existing optimizer/server-protection flags. It installs the three
public mod ZIPs from the fixture directory, enables OreShield and requests the
five NOS modules, requiring all five to activate with OreShield hooks installed.
Use only a disposable world. See the release validation report for executed
results. These fixtures do not simulate players walking aboard boats/trains or
establish full-modpack performance gains.
