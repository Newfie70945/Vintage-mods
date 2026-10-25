# Mod update ignore and dependency rules

Reflection test against the shipping Suite assembly (`VINTAGE_STORY`, `NSS_ASSEMBLY`). Covers when an
"ignore this outdated mod" decision expires (game version, installed version, offered release, status), how
dependency states are resolved (Ok, Missing, Disabled, TooOld, Unknown) and the reverse "required by" map.
