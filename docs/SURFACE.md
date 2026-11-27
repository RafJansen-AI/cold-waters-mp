# Surface vessels (design draft)

Status: plan only, nothing built (27 Nov). Goal of the next release: a player can command a surface ship instead of a
submarine, against subs and against other ships, in 1v1 and in team battles ([TEAMS.md](TEAMS.md)).

**Decided by Raf (27 Nov):** balance waits for a later release (proper matches with match mode and battle rating,
[MATCH-MODE.md](MATCH-MODE.md), [BATTLE-RATING.md](BATTLE-RATING.md)). For this release **any ship can be picked and
any matchup is possible** (sub vs ship, ship vs ship, mixed teams), for fun and testing. No `Allow ships` setting, no
exclusions. The helicopter dipping sonar goes in this release (step 5, section 7). Plan agreed by Raf, 27 Nov.

Sources: Cold Waters 1.15g + DotMod v0.4c, read with Mono.Cecil (`Assembly-CSharp.dll`) and from the DotMod data files
(`StreamingAssets/dotmod`). "Read in code" = seen in the IL; "data" = from the text files; nothing below was tried in
a game yet, with or without the mod. Ranges are computed with the game's formulas, not measured.

## What the game allows

### Player ships exist, as "submarines that can't dive"
- DotMod ships **46 player ship classes** (files `vessels/02_Surface/**/..._pl.txt`): US (Knox, Perry, Gearing,
  Spruance, Arleigh Burke I/II/IIA, Belknap, Ticonderoga, Iowa, Nimitz), Soviet (Poti, Grisha III, Riga, Krivak I,
  Kotlin, Kashin, Kanin, Udaloy, Sovremenny, Sverdlov, Kresta I/II, Kara, Kirov, Moskva, Kiev), UK (Type 21, Type 42,
  County), Australia (Adelaide), China (Huangfeng, Chengdu, Jianghu, Jiangwei, Luda, Luhu, Sovremenny). The testing
  grounds mission (single001) lists all of them as selectable, and several normal single missions offer some.
- Each `_pl` file is typed `ShipType=SUBMARINE` with `TestDepth=40` ft, `Diesel=TRUE`, `SubmergedAt=0.2`. The game
  sets `Vessel.isPlayerSurfaceShip` when the player vessel's test depth is below 42 ft
  (`LevelLoadManager.SpawnPlayerVessel`, read in code). So a player ship is the normal player sub code with a
  surface-ship flag on top: same helm, same torpedo tubes, same sonar screens, a ship HUD (`hud/default_usn`, ...).
- Every `_pl` has an AI twin (`..._ai.txt`, or the plain name for China) typed `ESCORT`, `CAPITALSHIP` and so on,
  checked for all 46. The twins differ: `_pl` Spruance has SelfNoise 160 (twin 175), Mk46 tubes + ASROC and Harpoon as
  VLS "torpedoes", a towed array and a Nixie towed decoy.
- **The mod filters player ships out today** (`Patch_PopulateLevelLoadData_AllSubs.IsRealSub` drops `_pl` names).

### What the flag changes (read in code, `isPlayerSurfaceShip`)
- Hull: `DamageInit` gives a `_pl` ship the submarine half (it is typed SUBMARINE), then x1.35, then x0.8 above
  20,000 t (more cuts above 50,000 t). Spruance: 160 points, Perry 98, Ticonderoga 184, Udaloy 154, Krivak 78,
  Kirov 315.
- **Torpedo hits on a player ship do x2.2 damage**, depth charges x1.48 (`Compartment.OnTriggerEnter`). So against
  torpedoes a Spruance (160 / 2.2 = 73) is about as tough as a Kilo (70), a Perry (45) like an Oberon.
- Towed array: no "too close to the bottom" check (`GetArrayBonus`). AI anti-ship missiles fly low against a player
  ship (`Enemy_Missile.LaunchMissile`); AI picks missiles instead of torpedoes against one (`VesselAI.GetAttackType`).
- Ballast and Battlestations have player-ship branches (`VesselMovement.SubBallast`, `MissionManager.Battlestations`;
  seen, not followed in detail).

### How a ship senses
- **Passive sonar**: the same sum as for subs ([MATCH-MODE.md](MATCH-MODE.md#how-passive-detection-works-in-the-game)).
  Ship hull sonars are weaker than sub sonars: SQS-26 37, SQS-56 38, SQS-53 39, Udaloy MGK-355 40, Bull Nose 32,
  Type 2016 36, most Chinese 21-26 (subs: 40 for 1984 boats, 46 Virginia). Only 15 of 46 have a towed array
  (SQR-18 44, SQR-19 48, Horse Tail 44, Mare Tail 40).
- **The ship's own noise does not hurt its own listening** (only its speed does), but **it makes the ship very easy
  to hear**: SelfNoise 157-200 against 105-152 for subs, so with the 3.2 compression a ship is 13-27 dB louder than a
  Los Angeles at the same speed, before speed.
- **Active sonar** (`SensorManager.GetActiveSonarNoise`, read in code): signal = SonarOutput - 2 x (spreading loss +
  attenuation) + target reflection x (0.6 bow-on to 1.0 broadside) - own noise + active sensitivity + layer/CZ.
  Ship sets are loud (SonarOutput 220-235; sub reflection 20-30). Threshold assumed to be the same 10 as passive.
- **Radar** (`CheckPlayerRADAR`): finds any vessel within radar range x signature factor, but a submarine only when it
  is at or very near the surface (mast or periscope up, or surfaced; exact depth not converted to feet).
- **Helicopter**: player ships with `AircraftTypes` (28 of 46) carry 1-2 helicopters (Moskva and Kiev 18), driven by
  DotMod's `DotModPlayerAircraft`: launch, fly waypoints, drop torpedoes, land, rearm. **The player's helicopter has
  no sensors in the code** (no dipping sonar, no sonobuoys, no radar: only the AI `Helicopter` class has those). It is
  a torpedo carrier aimed at what the ship already knows. Its torpedo is created in `DotModPlayerAircraft.DropWeapon`
  directly (`Instantiate` + `InitialiseTorpedo`), **not** through `WeaponSource.FireTube`, which the mod hooks today.

### Ship weapons against a submarine (data)
| Weapon | Who | How | Range | Payload / warhead |
|---|---|---|---|---|
| Mk46 mod 5 (tubes, 324 mm) | most NATO | lightweight torpedo, 40/45 kn, 1,500 ft | 12,000 yd | 70 |
| Mk50 | Perry '99 | 45/55 kn, 3,600 ft | 14,175 yd | 100 |
| Stingray | Type 21, Type 42 | 45 kn | 8,750 yd | 70 |
| SET-65M, 53-65K, UMGT-1 | Soviet ships | 533 mm tubes | 17,500-20,800 yd | 275-305 |
| ASROC RUR-5 (launcher/VLS) | Knox, Gearing, Spruance, Belknap, Tico '84 | rocket drops a Mk46 | 2,950-10,560 yd | Mk46 |
| VL-ASROC RUM-139 | Burke, Tico '99 | rocket drops a Mk46 | 2,950-21,945 yd | Mk46 |
| SS-N-14 (URK-5) | Krivak I, Kresta II, Kara, Udaloy | rocket drops a UMGT-1 | 7,655-98,425 yd (data) | UMGT-1 |
| RPK-6 | Sovremenny, Kirov, Kiev | rocket drops a UMGT-1 | 6,000-55,000 yd | UMGT-1 |
| RBU mortars (`Player_RBU`) | 24 Soviet/Chinese ships | salvo of depth bombs ahead of the ship | short | blast |
| Helicopter torpedo | 28 ships | Mk46 / Stingray / APR etc. dropped at a waypoint | helicopter radius | |

Plus naval guns (`Player_Guns`), SAMs (`Player_AntiAirMissile`, `Player_AutoMissile`), CIWS and chaff
(`Player_AntiMissileGuns`), noisemakers and towed Nixie decoys.

### How a submarine attacks a ship (data)
- **Torpedoes**: any torpedo the sub carries; the Soviet 53-65K and 65-76 (passive, 45-50 kn, 557 warhead, up to
  89,000 yd) are the anti-ship ones. With the x2.2 damage rule ships go down fast.
- **Anti-ship missiles** (`Torpedo` objects with `isMissile`, `HomeSettings=SHIP`): Harpoon UGM-84 (US '84+, UK '84+,
  Collins; 8,000-120,000 yd, 405 warhead), TASM (US '84+), Granit P-700 (Oscar), P-6 (Echo II, Juliet), P-70/P-120
  (Charlie, Papa), C-801 (Song, Han). Kilo, Victor III, Akula, Sierra carry none. A missile's target list leaves out
  vessels flagged `notMissileTarget`; ships defend with CIWS, SAMs and chaff.

### Who hears whom (computed, sea state 3, no layer or CZ, listener's array deployed)
Sub listening to a ship (sub at 5 kn), nm, for ship SelfNoise 160 (Spruance, Burke) / 185 (Tico, Belknap):

| Sub \ ship speed | 5 kn | 10 kn | 15 kn | 20 kn |
|---|---|---|---|---|
| Los Angeles '84, Akula | 8.6 / 12.0 | 10.7 / 14.3 | 13.0 / 16.7 | 15.3 / 19.3 |
| Victor III | 7.1 / 10.2 | 9.0 / 12.4 | 11.1 / 14.8 | 13.4 / 17.2 |
| Kilo (no array) | 5.0 / 7.7 | 6.7 / 9.8 | 8.6 / 12.0 | 10.7 / 14.3 |
| Virginia | 11.1 / 14.8 | 13.4 / 17.2 | 15.8 / 19.8 | 18.3 / 22.4 |

Ship listening passively to a sub at 5 kn, nm:

| Ship (hull + array) \ own speed | 5 kn: LA/Akula, Virginia, Kilo | 10 kn: LA/Akula | 15 kn: LA/Akula |
|---|---|---|---|
| Ticonderoga, Burke (39 + 48) | 3.8, 3.1, 3.3 | 1.8 | 0.7 |
| Spruance, Perry LH (38 + 44), Udaloy (40 + 44) | 2.9, 2.2, 2.4 | 1.4-1.6 | 0.6-0.8 |
| Krivak, Kara (32 + 40) | 2.1, 1.6, 1.7 | 0.9 | 0.3 |
| Knox, Type 42 (no array) | 1.5-1.6, 1.1-1.2, 1.2-1.3 | 0.9-1.0 | 0.5-0.6 |

Active: an SQS-53 pinging at 5 kn finds a Los Angeles broadside at about 6 nm, bow-on at about 4 nm; a 235-output set
(SQS-56, MGK-355) reaches further (rough, assumes threshold 10 and that the angle term is aspect). Pinging tells the
sub exactly where the ship is, from further than that (counter-detection range not computed yet).

**What this means:** passively, a ship is nearly blind and very loud. A sub hears it at 8-20 nm and can shoot from
outside ASROC range (Mk48: 43,000 yd = 21 nm; ASROC 5.2 nm). A ship wins only by pinging, by going slow, and with
help (teammates, or a sub on its own team). In 1v1, a careful sub should beat a single ship nearly every time; that
is realistic, but it is not a fair duel.

## What changes in the mod

### 1. Ship choice
- The vessel list in multiplayer includes all 46 `_pl` classes (from single001's `PlayerVessels`) next to the subs,
  in every mission, as the subs are today. No setting, no exclusions, any pairing (Raf, 27 Nov). Nimitz and Iowa have
  no real sonar (`nav_sonar` 12) and Huangfeng has 6 hull points: allowed anyway, just weak.

### 2. The opponent's ship in your game (the puppet)
- Spawn the **AI twin class** (`_ai` / plain name), not the `_pl` class: it is typed as a ship, so your torpedoes,
  missiles, radar, map icon and ID book treat it as a ship. Name mapping `_pl` -> twin is by file name (all 46 exist).
- Noise from the player's `_pl` SelfNoise (the twin is 10-15 dB louder): the existing `GetVesselNoiseLevel` patch
  gets the opponent's class from the session, not from the puppet's data.
- AI fully off, as now, **plus** the ship-only AI parts the twin brings: `Enemy_Missile`, `Enemy_AntiMissileGuns`,
  `Enemy_AntiAirMissile`, `Enemy_RBU`, guns, and **no AI helicopters spawned for it** (`CombatAI.enemyHelicopters`
  is filled from the mission's ships; an AI Seahawk hunting you would be a bug). To check in game: which of these
  run on their own when `VesselAI` is off.
- Moves by position, heading and speed (no depth). Hull state, decals, fire and smoke as now (ships do burn:
  `SetFire` only returns early for subs; fire sync to be tested).

### 3. Placement
- Ships at the surface, never moved by the spawn-depth check; deep water still required under the start point (subs
  around them need it). Range bands per pairing: sub vs ship starts further apart than sub vs sub (the ship is
  heard at 8+ nm); host setting, default to be found in playtests.

### 4. Weapons, by "the firer runs it, the target decides" (as for torpedoes today)
| Weapon | Today | Work |
|---|---|---|
| Tube torpedoes from a ship | via `FireTube`, probably works | test (324 mm tubes, Mk46) |
| ASROC, VL-ASROC, SS-N-14, RPK-6 from a ship | payload rockets work for subs (RPK-6) | test the VLS/launcher path |
| Helicopter torpedo | not seen (no `FireTube`) | hook `DotModPlayerAircraft.DropWeapon` or the torpedo's `InitialiseTorpedo`, send as a normal launch from the drop point; optional splash transient |
| RBU mortars, depth bombs | not synced | new message: salvo (launcher, aim point per round, sink rate, fuse depth); the target's game spawns the rounds and decides damage; the firer sees the explosions |
| Anti-ship missiles (sub -> ship, ship -> ship) | not synced | firer runs the missile; the target's game runs a copy that its CIWS, SAMs and chaff can engage; the target decides shot down, decoyed or hit and reports it; chaff replicated like noisemakers |
| Naval guns | not synced | ship vs ship and surfaced subs only; later step |
| Nixie towed decoy | side colour follows today | test that the firer's torpedo can be seduced by it (it must be replicated like a noisemaker) |

### 5. Sensors that cross games
- Active sonar pings are already sent (subs). Ship pings use the same path; check that a sub hears a ship's ping.
- Radar: each player's radar runs in their own game against puppets, so it works as is. Open: does the sub's
  ESM/mast warning (`strongestRadarSignal`) react to the ship's radar? If it needs the emitter's state, send "radar
  on/off" with the vessel state.
- The helicopter is not shown in the opponent's game at first (without a dipping sonar nothing gives it away, and
  subs can't shoot at it). Later: a visible helicopter model, which ship vs ship needs (SAMs can shoot helicopters).

### 7. Helicopter dipping sonar (looked into 27 Nov; in this release, Raf)
What the AI helicopter does (`Helicopter.CheckDippingSonar`, read in code): it hovers, then calls the game's general
sonar function `SensorManager.CheckSingleSonar(listener, contact, passiveSonarID, activeSonarID, mode, sensordata,
isSonobuoy, sonobuoy)` with itself as the listener and its own sonar (SH-60B: AQS-13F; Hormone, Helix, Lynx, Z-9 have
sets in the data too). The same function serves AI ships and the player's torpedo tracking. It works out what kind
of listener it has with `GetComponent` (`Vessel`, `Helicopter` or `Aircraft`).

What a player dipping sonar needs:
1. **A "dip" order.** The player's helicopter (`DotModPlayerAircraft`) already flies waypoints and hovers. Simplest:
   it dips by itself when it stops at its last waypoint, and lifts when given a new one. A key or button is nicer;
   DotMod's aircraft panel would need a new button (the mod already adds UI elsewhere).
2. **The detection.** Every sonar sweep (8 s game time, as the player's own sonar) while dipping, for each vessel in
   the enemy list, call `CheckSingleSonar` from the helicopter's position. The player's helicopter has none of the
   three components, so the call needs a stand-in: either a disabled `Helicopter` component on it, so the game takes
   its helicopter path (about 60% it works without side effects; the helicopter path reads fields I haven't traced),
   or the mod's own copy of the formula, which is already known (passive and active sums above). Either way it can
   be tested in single player, without a second game.
3. **Feeding the contact to the player's map.** Call the game's `ContactDetected(i, "SONAR")` and
   `CalculatePlayerTMA(i, ...)`, as `SonarCheck` does. The game draws a contact's estimate from the contact's true
   position plus an error, so a detection away from the ship shows in the right place. The delicate part: the ship's
   own `SonarCheck` calls `CheckIfContactLost` every sweep for contacts its own sonar doesn't hear, which would drop
   the helicopter's contact at once. A patch must keep a contact the helicopter heard in the last sweep or two. The
   mod already patches contact loss (tracks, decoys), so it fits next to that code.
4. **Multiplayer.** An active dip pings: the sub must hear it from the helicopter's position, so the ping message gets
   a position (today pings come from the vessel). A passive dip is silent and needs nothing.

**Estimate:** easier than feared. The game has a general sonar function and a contact display that works for
off-ship detections; the work is the dip order, the contact-loss patch and the ping position. Roughly 1-2 sessions of
work plus testing, about the size of the rule-2 track work. Risks: the stand-in listener (point 2) and how DotMod's
helicopter state machine reacts if the mod holds it in a hover. Sonobuoys (dropped by the helicopter) would reuse the
same pieces later.

### 6. Team battles and match mode
- A ship is just another player vessel: the lobby shows class and type, the hub, placement lists, results and
  scoring work the same. `Duel.cs`/`Weapons.cs` places that assume "the remote player is a sub" (depth, ballast,
  noise) get a vessel-kind check.
- Team setups are where ships make sense: a ship and a sub against two subs, or two ships against one sub. Arcade mode
  (shared contacts, step 3 of TEAMS.md) helps ships most: a ship's active contacts feed the team's subs.
- Area control: ships count like subs (real player vessels only, decoys never). A ship near the objective is loud,
  so it is a natural "holder" that the other side hunts.

## Balance (later release, Raf 27 Nov)

Not part of this release: any ship, any matchup. Kept here as the starting point for proper matches.

### Battle rating for ships
The sub formula (acoustic edge) would put every ship at the bottom, because their SelfNoise wipes out everything else.
Ships get their own formula on the same 1.0-6.1 scale, so the points budget (2^BR) mixes ships and subs:

| Part | Weight | From |
|---|---|---|
| Finding subs | 40% | best of passive (hull + array bonus, at 5 kn) and active (output + sensitivity); +bonus per helicopter (extends the attack, not the search) |
| Reaching subs | 25% | longest stand-off weapon (ASROC 5.2 nm, VL-ASROC 10.8, SS-N-14, RPK-6), helicopter torpedoes, tube torpedo quality, RBU |
| Surviving | 25% | hull points / 2.2 (torpedo rule), speed against the enemy torpedo's speed, CIWS + SAM + chaff against missiles, Nixie |
| Firepower | 10% | weapons carried, reload, anti-ship missiles (for ship vs ship) |

Then a hand-set offset per type in `cwmp-balance.cfg`, because the formula can't see that a ship alone can't find a
quiet sub. Expected order (my guess, to be computed): Ticonderoga, Burke, Udaloy, Spruance near the top; Perry,
Krivak, Kara in the middle; Knox, Kotlin, Riga, Chinese frigates at the bottom.

### Formats (ideas for later)
- 1v1 sub vs ship is lopsided by the numbers above (the sub wins almost always); team games with mixed teams are
  where ships fit. A mixed-team discount on ship costs is one idea to test.

## Seen in game (step 0, 27 Nov, test copy, single player, testing grounds)
- Picked from the normal vessel list: the ships come after the 66 subs (Knox first). Loaded an Arleigh Burke Flight I
  (meant the Spruance; one click too many). Loads and runs with the mod installed, 0 exceptions.
- **HUD works** (no `HudFix` trouble seen): helicopter panel (SH-60B, launch / plot course / recover, torpedo count),
  Nixie and towed array (SQR-19) panel, anti-ship missile (UGM-84) and SAM (RIM-66M) panel, telegraph, 5"/54 gun
  (680 rounds), masts, active sonar, decoys; six 324 mm tubes with Mk46; weapons tab: Mk46 13, VL-ASROC 25, SM-2 38,
  TLAM 10 (VLS 73). Depth stays 0 ft. Own noise shown 147.8 dB at 5 kn = 92.8 + 160 / 3.2 + 5, as computed.
- **Helicopter**: first press of launch arms it ("Aircraft ready to launch", 2 Mk46 loaded, tubes 15 -> 13), second
  press launches. "Plot aircraft course": the course line follows the cursor and a right-click commits it; it flew
  there. "Plot aircraft attack" (drop point) worked the same way but the point landed where the cursor rested, not
  where I clicked: the UI is fiddly, not understood yet. About two minutes later: "Air target SH-60B Seahawk is shot
  down" and the icon froze. No enemy was seen and nothing in the logs; the message comes from `Player_AntiMissileGuns`
  or a SAM (`AirTargetIntercepted`). Possibly a hostile in the testing grounds, possibly own fire: to check, because
  in multiplayer nobody should be shooting at your helicopter.
- VLS: selecting VL-ASROC for the VLS bar wasn't figured out (clicking the bar and the weapon did nothing visible).
- Not tried yet: firing tubes or VLS at a target, active sonar, guns, a ship in a duel.
- Logs: `logs\ship-look-0` (local folder).

## Order of work
0. **Look first**: load a `_pl` ship in the testing grounds on a test copy without a duel, to see the controls, HUD
   (does `HudFix` handle `default_usn`?), helicopter panel and VLS in normal play.
1. **Any ship as a player, any matchup.** Ships in the vessel list, puppet = AI twin with all ship AI off and no AI
   helicopters, noise from the `_pl` class, surface placement. Weapons: tube torpedoes, ASROC/VL-ASROC/SS-N-14/RPK-6,
   active sonar pings, Nixie. Test Spruance vs Los Angeles, Udaloy vs Akula, and Spruance vs Udaloy (torpedoes only)
   on the two copies.
2. **Helicopter torpedoes and RBU/depth bombs** (new launch hook, mortar salvo message).
3. **Anti-ship missiles, from subs and ships**: missile copy in the target's game, CIWS/SAM/chaff decide, chaff sync.
4. **Naval guns** (ship vs ship, surfaced subs), visible helicopter (SAMs can shoot it).
5. **Helicopter dipping sonar** (section 7).
6. Ships in team battles (mostly checks: lobby shows type, placement per team). Release 0.3.0 when 1-6 work.
