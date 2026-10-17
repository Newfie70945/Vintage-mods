# Real-client feasibility and delivery limits

Newfie's VS Stress Tester controls **existing, licensed, official game client windows**. It does not implement Vintage Story authentication, a replacement client, or fake player entities. The server continues to use its normal player authentication and vanilla protocol. Ordinary players need no client mod.

## What was inspected

The available distribution is the official **Vintage Story 1.22.7 dedicated Linux server**, not the desktop client. Its server assemblies expose normal player joins/leaves and authenticated player identities. They do not provide a supported standalone headless client launcher. Exact server assembly inspection also found that the protocol network-version string differs from the marketing version. Guessing messages from names or inventing an authentication sequence would be unreliable.

No licensed account credentials, authenticated desktop client sessions, desktop game installation, or usable X11 graphical client displays were supplied in this workspace. The implementation therefore never sends guessed protocol packets and never disables `VerifyPlayerAuth`.

| Approach | Feasibility in this delivery | Decision |
|---|---|---|
| Official desktop clients controlled through X11 | Implementable with Python and `xdotool`; operator supplies accounts, official clients, dedicated displays and test server | Selected; working driver and scenario scheduler supplied, real client execution still unvalidated |
| Headless client using server assemblies | No supported standalone authenticated headless client identified; dedicated-server distribution is insufficient | Not implemented |
| Protocol-compatible replacement client | Requires licensed authentication/session implementation and complete target-version protocol validation, not available here | Not implemented |
| In-server NPCs or fabricated player objects | Does not reproduce client networking, chunk demand, login or synchronization | Excluded from multiplayer results |

## What the harness actually does

* Selects exact existing X11 windows; resolves their PIDs for separate load-generator resource accounting.
* Uses configurable key/mouse/authorized-chat-command macros on those windows.
* Drives deterministic per-client action choices, with one focus owner per display and bounded parallel display workers.
* Waits for trusted server-written join/presence evidence for **all requested exact PlayerUIDs** before starting the workload.
* Expires stale presence evidence, records disconnects and input failures, and releases held keys/buttons on errors, cancellation and completion.
* Writes plans, action attempts, unconfirmed actions, resource samples and a JSON report.

A matching PID/window is not cryptographic proof of official client provenance. The operator must launch the legitimate licensed game. Local server evidence must come from the owned test server, transported over a trusted filesystem/SSH channel. A user-edited JSON file is not acceptable benchmark evidence.

No automated account login is included. Start and authenticate the real game normally. A configurable `connect` macro can navigate a calibrated saved-server UI; a configurable `reconnect` macro can disconnect/rejoin that same saved server. Coordinates and launch arguments are deliberately not guessed from the dedicated-server distribution. Keep account passwords out of macros, command lines, configuration and reports.

Mouse movement can depend on the client's mouse capture mode, X11 window manager, resolution and settings. Calibrate it on one client and validate movement on the server before scaling. Wayland-native clients and background Windows/macOS input are not supported by this driver.

## Demonstrated versus outstanding

**Maximum authentic connected clients actually tested in this delivery: 0.** The Python scheduler, evidence handling, bounds and cleanup are unit-tested. Dry-run records explicitly say `simulated_tooling_only` and report zero real clients and zero confirmed game actions. These are tooling checks, not multiplayer performance evidence.

Official-client GUI execution, authenticated join/rejoin, all interactions, 1–200-client scaling, mod compatibility, 2–6-hour endurance and production performance gains remain untested. Only publish results after those workloads have actually completed on a dedicated test clone with accounts and server-side correctness evidence.

A stock server used for untouched baseline A does not run this profiler. Its authentic client evidence must come from a separately validated read-only parser of the exact stock join/leave logs or an equivalent trusted observer. No such stock-log parser is supplied here. The included optimizer's optional evidence stream is convenient for instrumented trials, but installing it changes an untouched baseline into profiler baseline B. Do not silently relabel B as A.
