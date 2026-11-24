# Cold Waters + DotMod v0.4c: code map for the 1v1 duel (step 1)

Source: decompiled DotMod `Assembly-CSharp.dll` (identical to Raf's install, SHA-256 `fcbed6e1…`).
Line numbers refer to ILSpy 8.2 output and are approximate. "Verified" means read in code; "test" means confirmed in
Raf's game with the hello plugin (2026-11-24).

## 1. Mission loading (verified)

- A mission is a text file (`StreamingAssets/dotmod/missions/single/singleNNN.txt`) parsed by
  `TextParser.PopulateLevelLoadData(string filename, string[] customLinesInFile = null)` into the `LevelLoadData`
  component.
- **The second argument takes the mission as text lines.** The mission editor already uses this
  (`EditorMission.cs:687`). So the host can send the mission text over the network, and the client loads it without
  needing the file on disk. This replaces Sea Power's "ship the save file".
- Relevant keys: `UsePresetPositions`, `PlayerPosition`, `PlayerHeading`, `PlayerDepthInFeet`, `PlayerTelegraph`,
  `EnemyShipClasses`, `EnemyPositionsX`, `EnemyPositionsZ`, `EnemyHeadings`, `NumberOfEnemyUnits`, `CombatBehaviour`,
  environment keys (`Date`, `Time`, `Weather`, `SeaState`, `DuctStrength`, `LayerStrength`), map keys.
- The player's sub class is `PlayerFunctions.playerVesselClass` (an index into `database.databaseshipdata`), set by the
  single-mission menu (`DotModSingleMissionMenuController`).
- Load order (test log): `SetupCombat` → `SpawnPlayerVessel(0, class)` → `LoadLevel` finishes. `SetupCombat` runs
  before the player array is filled, so read the player vessel after `SpawnPlayerVessel` or `LoadLevel`.
- `LevelLoadManager.LoadLevel()` (~427): sets `playerNumberofShips = 1`, allocates `playervesselsonlevel`, calls
  `SpawnPlayerVessel`, then `enemyvesselsonlevel = new Vessel[enemyNumberofShips]` with
  `enemyNumberofShips = levelloaddata.kmShipClasses.Length`, then `SpawnShip(k)` for each enemy.

**Duel design:** the host builds the duel mission with UsePresetPositions=TRUE, the host's sub as the player and the
client's sub as the only enemy (NumberOfEnemyUnits=1-1). The client receives a mirrored copy (player and enemy classes,
positions and headings swapped) and loads it with `PopulateLevelLoadData(string.Empty, lines)`. No change to array
sizes is needed. The opponent is simply "enemy 0" on both machines.

## 2. Vessels and movement (verified)

- Player and AI ships are both `Vessel`, with components `vesselmovement`, `damagesystem`, `acoustics`, `vesselai`,
  `submarineFunctions`. Flag `playercontrolled`.
- Movement is kinematic: `VesselMovement.FixedUpdate` / `TranslateShipForward` move the transform with `Translate` and
  rotate with Slerp/RotateTowards. There's no Rigidbody physics to fight.
- Inputs worth replicating: `telegraphValue`, `rudderAngle`, `ballastAngle`, `shipSpeed`, `isCavitating`, depth,
  `blowBallast`; plus `PlayerFunctions.runningSilent`.

**Puppet:** on each machine, disable `vesselai` (and `CombatAI` / `VesselAINav` if attached) on enemy 0. Each tick, write
the opponent's telegraph, rudder and ballast inputs so the game's own movement code moves the puppet between packets.
Correct position and heading by smoothing small errors and snapping large ones (more than ~50 m or 10°).

## 3. Sensors / detection (verified, with a fairness issue)

- Each machine runs detection for its own player only (`SensorManager.FixedUpdate`, `SonarCheck`, `CheckSingleSonar`,
  `CalculatePlayerTMA`). That suits us, because each player's fog of war is computed locally.
- The opponent's loudness comes from `SensorManager.GetVesselNoiseLevel(Vessel)` (~1644): self-noise, speed, cavitation
  and damage, **plus player-only modifiers that only apply when `playercontrolled` is true**: running silent, the DotMod
  RPG self-noise bonus, a dev bonus, and masking by nearby loud ships.
- **Fairness bug to fix:** a puppet has `playercontrolled = false`, so it would be heard *louder* than the real player.
  Fix: Harmony-patch `GetVesselNoiseLevel` so the puppet uses the opponent's replicated running-silent and RPG state. The
  noise itself includes random values, which is fine because only the listener's machine computes it.
- Transients (tube flooding, launches) go through `CheckCanHearTransient` and need a replicated "transient" event.

## 4. Torpedoes (verified)

- Player launch: `PlayerFunctions.FireTube` (~1937) / `LaunchWeapon` → `WeaponSource.FireTube` (~492) → `Torpedo`
  spawned, `InitialiseTorpedo`, `SensorManager.AddTorpedoToArray`.
- `Torpedo.whichNavy` = 0 for player-side, 1 for enemy-side. `SetTorpedoTargetsList` adds the player and all enemies as
  candidate targets. Wire: `onWire`, `playerControlling`, `CheckWire`, `BreakWire`, `TorpedoCutWire`. Homing:
  `ActivateTorpedo`, `CheckTargetInSensorCone`, `RaycastSensor`. End: `DestroyTorpedo` (~2958).
- `Torpedo.FixedUpdate` is about 1,100 lines (DotMod rewrite), which is where the complexity lives.

**Design:** the firer's machine runs the real torpedo. The target's machine spawns a puppet torpedo (whichNavy = 1) with
guidance disabled, driven by 10–20 Hz state packets. Noisemakers and decoys dropped by the target are replicated to the
firer, so the real seeker can be fooled by them.

## 5. Damage (verified)

- Damage is collision-driven: `Compartment.OnTriggerEnter` (~104) fires when a `Torpedo` collider (or depth charge)
  enters a vessel compartment. It reads the warhead from `databaseweapondata`, sets `torpedo.damageDealt` and
  `destroyMe`, then applies explosion, flooding, fire and subsystem damage (`ApplyExplosion`, `ApplyFlooding`,
  `ApplyFire`, `CheckSubsystemsDamage`, `SinkShip`). It uses random values.

**This makes "the target decides" almost free:**
- On the target's machine, the puppet torpedo physically reaches the real player sub, and the game's own
  `Compartment.OnTriggerEnter` applies damage as normal. The resulting hull %, floods and sinking are then broadcast.
- On the firer's machine, a Harmony prefix on `Compartment.OnTriggerEnter` skips damage when the vessel is the puppet.
  The torpedo is despawned without effect, and the firer waits for the target's "hit/damage/sunk" event, then shows
  the explosion and applies the reported damage state to the puppet.
- Edge case: a near miss on one machine that's a hit on the other. The target's result wins, and the firer's torpedo
  is removed when the target reports a hit.

## 6. Time compression (verified + test)

- `PlayerFunctions.SetTimeCompression()` (~2289) sets `Time.timeScale` (1 or 10) and `Time.fixedDeltaTime`
  (0.02 at 1x, 0.06 at 10x; test log). The game also calls it automatically from `DamageControl` and `VesselMovement`
  (~1267) to drop out of compression.
- Because the physics step changes with compression, both machines must always be at the same setting. Patch the
  method into propose → host decides → both apply. Any automatic drop to 1x on either machine forces 1x on both.

## 7. Patch list for the PoC

| # | Target | Type | Purpose |
|---|---|---|---|
| 1 | `TextParser.PopulateLevelLoadData` | call | Client loads the mirrored mission from received lines |
| 2 | `LevelLoadManager.LoadLevel` | postfix | Mark enemy 0 as puppet, disable its AI, start the session |
| 3 | `VesselAI/CombatAI/VesselAINav.FixedUpdate` | prefix | Skip for the puppet (belt and braces) |
| 4 | `SensorManager.GetVesselNoiseLevel` | postfix | Apply the opponent's running-silent/RPG modifiers to the puppet |
| 5 | `WeaponSource.FireTube` / `PlayerFunctions.LaunchWeapon` | postfix | Announce launch, start streaming the torpedo |
| 6 | `Torpedo.FixedUpdate` | prefix | Puppet torpedo: skip guidance, follow network state |
| 7 | `Compartment.OnTriggerEnter` | prefix | Firer side: no local damage to puppet; target side: report result |
| 8 | `Compartment.SinkShip` | postfix | Broadcast sinking |
| 9 | `PlayerFunctions.SetTimeCompression` | prefix | Time-compression vote |
| 10 | `WeaponSource.DropNoisemaker` | postfix | Replicate countermeasures |

## Open questions for step 2–4

- Does `SpawnShip` give a submarine enemy extra AI components (formation, `CombatAI`) that must also be disabled?
- What exactly does `Torpedo.FixedUpdate` drive besides movement (wire, sensor cone UI, warnings)? That decides how
  much of it the puppet can skip.
- Win/lose: `MissionManager` (`playerSunk`, `enemysunk`) must end the mission on both machines consistently.
- RPG mode / prestige modifiers: both players should use the same setting (part of the fingerprint check).
