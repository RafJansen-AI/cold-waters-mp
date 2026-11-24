# Cold Waters code findings (1v1 sub duel feasibility)

Source: DotMod repo (github.com/DotModGroup/ColdWaters-DotMod, last commit 2024-10-20), which ships its own
`ColdWaters_Data/Managed/Assembly-CSharp.dll` (1.64 MB) plus `Assembly-CSharp.bak` (1.07 MB, presumed vanilla).
Both decompiled with ILSpy 8.2. Raf's actual install NOT yet checked (device unreachable 2026-11-24).

| Metric | Vanilla | DotMod |
|---|---|---|
| Decompiled files / lines | 334 / 78k | 409 / 109k |
| `playervesselsonlevel[0]` hard-coded refs | 416 | 858 |
| `playervesselsonlevel[activePlayerSlot]` refs | 5 | 5 |

Findings
- Player and AI ships are the same `Vessel` class with a `playercontrolled` flag (set in LevelLoadManager / VesselBuilder).
- Two-sided world: `GameDataManager.playervesselsonlevel[]` vs `enemyvesselsonlevel[]`. Player side is an array with an
  `activePlayerSlot`, but only slot 0 is ever used, and ~99% of code hard-codes `[0]`.
- Enemy AI (`VesselAI`) caches `playervesselsonlevel[0]` as its target.
- Time compression = `Time.timeScale` + `fixedDeltaTime` set in `PlayerFunctions.SetTimeCompression()` (single choke point).
- Mid-mission saves don't exist (only campaign saves), so Sea Power's "ship the save file" scene sync doesn't apply;
  single missions are text files, so ship the mission file + a seed instead.
- DotMod is a replaced DLL, not a BepInEx/Harmony mod. No BepInEx/Harmony references inside it. A BepInEx mod would
  have to patch DotMod's DLL (not vanilla), and each DotMod release can move/rename methods.
- DotMod adds player surface ships, helicopters, SAMs, friendly task forces, rewritten torpedo logic.

Duel architecture this implies (inference)
- Each instance: own sub = player[0] (unchanged). Opponent's sub = an extra entry in enemyvesselsonlevel with AI off,
  driven by network state. Mirror image on the other side. No need to make the game support 2 player slots.
- Torpedoes: firer owns guidance/wire; target's instance decides detonation/damage (Sea Power's rule).
- Time compression: patch SetTimeCompression to propose/vote; host broadcasts.

## Raf's install (verified 2026-11-24)
- GOG Cold Waters 1.15g (Unity 5.4.0f3) + DotMod v0.4c installed with JSGME; no other mods, no BepInEx.
- Assembly-CSharp.dll SHA-256 fcbed6e1... = the DotMod GitHub DLL analysed above; the .bak is vanilla 1.15g.
- DotMod code build dates from 2022-08-06 (script version VS220806F), so the code has been stable for ~4 years.
- GOG build: Steam P2P/lobbies unavailable; plan on direct UDP (e.g. LiteNetLib) or a relay.
