# Cold Waters 1v1 sub duel: proof-of-concept plan

Target: DotMod v0.4c on Cold Waters 1.15g (Unity 5.4.0f3, Mono, .NET 3.5, 32-bit). Raf's install verified 2026-11-24.
Code references are to the decompiled DotMod `Assembly-CSharp.dll` (SHA-256 `fcbed6e1…`). Estimates are my judgment, not measured.

## Goal of the PoC

Two players on separate PCs load the same single mission. Each drives their own sub. Each sees the other's sub as an
enemy contact that moves correctly, can be detected by sonar, shot at with torpedoes, damaged and sunk. Time compression
stays in sync. Nothing else (no AI escorts, no missiles, no aircraft, no campaign).

**Done when:** 5 full duels in a row over Tailscale, each ending in a sinking, with no desync that a player notices
(position error under ~50 m, same winner on both screens).

## Ground rules

- **Both players must run the same build.** On connect, exchange SHA-256 of `Assembly-CSharp.dll` and a hash of the
  mission file plus vessel/weapon data. Refuse to connect on a mismatch and say which file differs.
- **Cross-store is expected to work.** GOG and Steam 1.15g almost certainly ship the same game code [uncertain, ~85%],
  and the fingerprint check proves it either way.
- **Cross-version (vanilla vs DotMod) is out of scope.** The two versions have different torpedo, sensor and AI code,
  so a duel between them could never be fair or consistent. "Works with different versions" means one mod that loads
  on either version, with both players on the same one. Vanilla support comes after the PoC: hook only methods present in
  both (1,513 of 1,555 vanilla signatures survive in DotMod).
- **Not lockstep.** Same model as the Sea Power mod: each side is in charge of its own sub and its own torpedoes, and the
  target's game decides hits and damage.

## Architecture

| Layer | What it does | Notes |
|---|---|---|
| Loader | BepInEx 5 x86 + Harmony 2 | Needs the preloader entrypoint tweak that Cold Waters Expanded documents (`UnityEngine.dll` / `MonoBehaviour` / `.cctor`). Coexists with DotMod because DotMod is only a replaced DLL. |
| Transport | `ITransport` interface, one implementation for the PoC | PoC: direct UDP over Tailscale/ZeroTier. Later: NAT punch-through, relay fallback, Steam P2P. Same interface, chosen in config. |
| Session | Hello (fingerprint) → mission handshake → ready → start | Host sends the mission file name, its hash and a random seed. No save file shipping (Cold Waters has no mid-mission saves). |
| Replication | 10–20 Hz state packets for own sub and own torpedoes | Position, heading, speed, depth, noise level, mast/array state. Receiver smooths small errors and snaps on large ones. |
| Events | Reliable messages | Torpedo fired, wire cut, countermeasure launched, hit/damage result, sunk, time-compression vote. |

### How the opponent appears (key design decision)

The game assumes one player sub (`GameDataManager.playervesselsonlevel[0]` is hard-coded about 858 times in DotMod),
so we don't try to support two. On each PC:
- your own sub stays the normal player vessel;
- the opponent's sub is spawned as an extra entry in `enemyvesselsonlevel`, using the opponent's chosen class, with
  `VesselAI` disabled and its movement written each frame from network state (a "puppet").

Both players see a mirror image. Sonar, TMA and the tactical map then work unchanged, because to the game the opponent
is just an enemy ship moving in a way it can't predict.

### Torpedoes

- The firer's game owns the torpedo: its path, wire guidance, seeker and target choice.
- The target's game shows a puppet torpedo that follows the firer's state, and decides whether it detonates and how much
  damage it does, then broadcasts the result. This avoids "hit on my screen, miss on yours".
- DotMod rewrote torpedo logic (wake-homing etc.), so the PoC uses one plain wire-guided torpedo type first.

### Time compression

**PoC: disabled** (Raf, 2026-11-24). A prefix on `PlayerFunctions.SetTimeCompression()` keeps `Time.timeScale = 1`.
This also removes a sync risk, because `fixedDeltaTime` changes with compression (0.02 at 1x, 0.06 at 10x).
Later it becomes a match setting (see below), using propose → host decides → both apply.
Raf's idea (2026-11-24): compression is active only while **both** players hold it on (e.g. both press a key). It's simple and fair, because either side can drop back to 1x at any time.

### End of match

A sinking does **not** end the match while weapons are still running, so mutual kills (draws) are possible.
The match ends when no torpedoes are in the water and at most one sub is still afloat, or when the time limit runs out.
The host decides the end, from both sides' reports. Possible results: win, loss, draw (both sunk) or stalemate (time limit).
Needs a patch on `MissionManager` so the game's own "player sunk → mission over" path waits for the host's decision.

## Hook points (from the decompile, to confirm in step 1)

| Need | Candidate hook |
|---|---|
| Mission start / vessel spawn | `LevelLoadManager.LoadLevel`, `SetupCombat`, `SpawnShip`, `SpawnPlayerVessel` |
| Enemy array allocation | `LevelLoadManager` line ~595 (`enemyvesselsonlevel = new Vessel[...]`), to add one slot for the opponent |
| AI off for puppet | `VesselAI` (it caches `playervesselsonlevel[0]` as its target) |
| Torpedo launch | `PlayerFunctions.FireTube` / `LaunchWeapon`, `WeaponSource.FireTube` |
| Torpedo end | `Torpedo.DestroyTorpedo` |
| Damage | **Not yet found.** No obvious `ApplyDamage` entry; damage seems spread across `Compartment`/`DamageSystem`. Finding the right hook is the biggest unknown in step 4. |
| Time compression | `PlayerFunctions.SetTimeCompression` |

## Steps and estimates

| # | Step | Estimate | Main risk |
|---|---|---|---|
| 0 | Dev setup: BepInEx on a copy of the install, Harmony "hello world" plugin logging mission load | 1–2 days | Unity 5.4 + DotMod + BepInEx entrypoint quirks |
| 1 | Map the code: mission loading, spawn, AI, sensors, torpedo life cycle, damage path | 3–5 days | Damage path is spread out |
| 2 | Transport + session: connect over Tailscale, fingerprint check, mission handshake | 3–5 days | Getting a .NET 3.5-compatible UDP library (older LiteNetLib, or a thin custom UDP layer) [uncertain] |
| 3 | Puppet opponent: spawn, AI off, state replication with smoothing; visible on sonar/TMA | 1–2 weeks | Sensor code may treat AI-off vessels differently (e.g. noise model) |
| 4 | Torpedoes + damage: fire, puppet torpedo, target-side hit/damage, sinking | 2–3 weeks | Torpedo/damage code is the most DotMod-modified area |
| 5 | Time compression locked at 1x; match end (draws possible) | 2–4 days | Game's own "mission over" path on sinking |
| 6 | Test to the "done" bar, fix desyncs | 1–2 weeks | Edge cases (collisions, terrain, ice, noisemakers) |

**Total: roughly 6–10 weeks of focused work** for someone fluent in C#/Unity/Harmony. The chance of reaching the
"done" bar is about 65%. The most likely way to fail is torpedo/damage consistency, not networking.

## Match settings (after the PoC)

The host sets these in a lobby screen. They're sent to the client and included in the fingerprint, so both sides play
the same rules. Most of them map directly onto existing mission-file keys, so the host just generates a mission.

| Setting | Options | Maps to |
|---|---|---|
| Engagement range | e.g. 3 / 6 / 12 / 20 nm, or random within a band | `PlayerPosition`, `EnemyPositionsX/Z` |
| Start geometry | head-on, crossing, random bearing; known vs unknown start position | positions + headings |
| Sub choice | free, same class, or era-matched (1968 / 1984 / 1999) | `playerVesselClass` / `EnemyShipClasses` |
| Map / area | open ocean, specific map, terrain on/off | map keys, `UseTerrain` |
| Environment | time of day, weather, sea state, layer/duct strength, ice | environment keys |
| Random traffic | off / light / normal (neutral merchants, fishing boats, biologics) | `VesselsAndTraffic`, neutral vessels |
| Time compression | off / mutual agreement only / up to 10x when no contacts | `SetTimeCompression` patch |
| Time limit | none, 30, 60, 90 min | session timer |
| Loadout | free, default, restricted (e.g. no wake-homers, no missiles) | weapon loading |
| Assists | DotMod RPG/prestige bonuses on/off, contact labelling, auto-TMA | `RPG_Mode`, game options |
| Spectator / replay | later: log positions for after-action replay | own recording |

On engagement range vs time compression: at 1x, two subs at 12 nm closing at about 10 knots combined take about an
hour to meet, so long ranges are only practical with "mutual agreement" compression. I'd default to 6 nm and no
compression [my judgment].

Random traffic needs extra sync work: neutral ships must be the same on both machines. The simplest approach is for
the host to own all traffic and stream it like the opponent's sub (more puppets, same code). It adds an estimated
1–2 weeks, which is why it's off in the PoC.

### Raf's notes from the first torpedo test (2026-11-24)

- **Separate Multiplayer entry in the main menu**: host/join, lobby with the settings above, instead of piggybacking on the
  single-mission flow. This also fixes the "both sides must pick a sub before the host starts" race.
- **Conditions**: choose them, or pick "random" (the host rolls once and sends the result).
- **Neutrals**: choose whether there is neutral traffic (see Random traffic above).
- **Torpedo decoys / noisemakers need a design.** A decoy is launched by the target, but the firer's game runs the
  torpedo's seeker. The firer's game must therefore see the target's decoys as puppets (spawn, position, lifetime)
  so its seeker can be seduced. Open questions: whether DotMod's seeker logic reads decoys from a list we can inject into,
  and the 100–200 ms latency on a decoy that has to work in the last seconds [uncertain, needs a code check].
- **Pause (found in test 3):** pausing still works in MP and freezes only that player's sub, which the opponent then sees
  stopped dead. In MP, pause must either be disabled or pause both games (same propose/accept flow as time compression).
  Worse: incoming torpedoes are driven in FixedUpdate, which stops while paused, so a paused player probably can't be hit.
- **Testing aids:** F7 debug teleport (0.0.10) jumps your sub close to the opponent's bow. Keep weapon minimum ranges
  and the 5 s arming time in mind.
- **Rocket-delivered weapons** (Vjuga, ASROC-type): only the carrier is replicated so far, not the torpedo it drops.
- **Launch detection callout**: the target must get the game's "torpedo in the water" callout and contact when the
  opponent fires. Treated as a PoC bug, not a later feature.

## After the PoC (not in scope now)

1. Transport options behind the same interface: NAT punch-through with a tiny rendezvous server; relay fallback; Steam
   P2P via Steamworks.NET for Steam-only pairs. Tailscale/ZeroTier stays as the zero-code fallback.
   Expected reliability: Tailscale/ZeroTier ~99%; punch-through alone ~80–90% of home networks [uncertain]; punch-through
   plus relay ~99% but needs a hosted server (a few € a month); Steam P2P ~99% but only when both own it on Steam.
2. Vanilla support (same mod, both players on vanilla).
3. More weapons, missiles, noisemakers, AI escorts per side.
4. In-game overlay for host/join and connection status.

## Inputs needed from Raf before step 0

- A second machine or second player for testing. Two game copies on one PC might do for steps 2–3 [uncertain, untested], but real tests need two machines.
- Agreement to install BepInEx into a **copy** of the game folder, so the main install stays untouched.
