# Co-op against the AI (exploratory analysis, 28 Nov 2026)

Question (Raf): can two or more players fight the game's AI together, in single missions and maybe later a campaign?
What would have to change? No code yet; this is for a future release.

Everything marked *read* was read in the game's code (Cold Waters 1.15g + DotMod v0.4c, via Cecil); *guess* is not
verified. Rough probabilities where it matters.

## Short answer

Possible, but it is the biggest change the mod would make. The hard part is not the network (the mod already
copies vessels, weapons, helicopters and AI traffic between games). It is that the game's AI, sensors and mission
logic are written for exactly **one** player. The most promising way is to keep the game's AI unchanged and, for each
AI unit, make "the player" mean whichever human it is hunting at that moment. Estimate: a first playable prototype
(two subs vs a small AI group, AI torpedoes only) is several weeks of work; full parity with single player (aircraft,
missiles, guns, mission endings, campaign) much more. Chance the main approach works without rewriting the AI: about
65-70%.

## What the game does today (read)

- **One player everywhere.** `GameDataManager.playervesselsonlevel[0]` is read 926 times in 48 classes: mission
  logic (126), sensors (118), campaign (114), map, weapons, damage control, and the AI itself (VesselAI 22,
  CombatAI 15, Enemy_Missile 20). The array has room for more, but nothing looks past index 0.
- **The AI hunts a track, not a vessel.** Every AI unit keeps one `SensorData`: `playerDetected`,
  `decibelsTotalDetected`, `lastKnownTargetPosition`, `rangeYardsLastDetected`... Sonar, radar, MAD and ESM
  (`SensorManager.CheckSingleSonar`, `CheckEnemyUnitRADAR`, `CheckEnemyUnitMAD`) write into it; the AI then attacks a
  *position* (`CombatAI.AttackPosition`, `BeginAttackOnPosition`, hunter/killer assignment from
  `lastKnownTargetPosition`).
- **Why the MOSS fools the AI (your observation).** When an AI unit hears a decoy, `CheckSingleSonar` (contact =
  torpedo, weapon type DECOY) falls through to the same code that updates the "player" track: the decoy becomes "the
  player" for that unit (`VesselAI.vesselIsDecoyed`, `decoyTimer`). So the AI is not tied to the player's vessel object:
  it follows whatever detection it has. That is good news (the AI can be pointed at something else), and it shows the
  limit: there is only one track per AI unit, so two players would merge into one target unless we manage it.
- **Weapons already home on more than one vessel.** `Torpedo.SetTorpedoTargetsList`: every torpedo's target list is
  the player plus every non-sinking vessel in the enemy list (that is how friendly fire works in single player). A
  co-op partner present as a vessel can already be hit physically; what is missing is the AI *deciding* to shoot at it.
- **Aircraft** (helicopters, MPA) hunt the player through the same `SensorData` (dipping, sonobuoys, MAD, radar) and
  drop weapons at the player's position.
- **Mission end.** `MissionManager` ends the mission when the player sinks or all hostiles are gone or left.

## What the mod already has that co-op would reuse

| Need | Already in the mod |
|---|---|
| Host runs AI, others see it | `Ambient.cs`: host runs neutral traffic AI, streams position/course/speed/damage/sinking; clients show copies with AI off; the host decides hits on them (`G_AMB_HIT`). |
| Other players' vessels in your game | Puppets (`Duel.cs`): another player's sub or ship as an AI vessel with AI off, driven by their state; noise corrected to their real class. |
| Weapons between games | `Weapons.cs`: firer runs the real weapon, others see copies; the target's game decides hits/damage. Rounds, missiles (shoot-downs, chaff), towed decoys, helicopters (`Helos.cs`) too. |
| Friends vs foes | Team battles: side colours on the map, team placement (1 nm apart), `Duel.IsAlly`, team-mates' missiles ignored by our air defence (`Weapons.MaskAllies`). |
| Time | `Sync.cs`: time compression by agreement, pause. |
| Mission choice and conditions | `Match.cs`: host's map, conditions, difficulty sent to everyone. |

## What has to change

### 1. One authority for the AI (host)
All AI enemies run in the host's game only, like neutral traffic today. Clients get them as copies with AI off
(extend `Ambient` from neutrals to hostiles: same spawn list, state stream, host-decided hits on them). Clients'
weapons against AI vessels: as for neutrals, the host decides (the client's torpedo copy in the host's game hits the
real AI ship there). Effort: medium; the pattern exists.

### 2. The AI must see and hunt every player (the core problem)
In the host's game, each client is a puppet. The AI must detect it, track it and attack it as if it were the player.
Three ways, best first:

**A. "Whose turn" swap (recommended).** Each AI unit gets a current target: the host's own vessel or one of the
client puppets (whichever it hears best, sticky for a while so it doesn't flip every sweep). Around that unit's own
AI work (its sensor checks against "the player", its attack decisions, its weapon launches) the mod sets
`playervesselsonlevel[0]` to its target for the duration of the call and restores it right after: the same trick as
`Weapons.MaskAllies` (masking for the length of one call). The AI then uses all its senses (sonar, radar, MAD, ESM,
visual) and all its weapons against either player, unchanged.
- Risk: `SensorManager.FixedUpdate` runs the player's own sensing and the AI's in one method, so the swap has to sit
  on the inner AI calls (`CheckEnemyUnitSONAR/RADAR/MAD`, `CheckSingleSonar` with an AI listener, `VesselAI.FixedUpdate`,
  `CombatAI.*Attack*`, `Enemy_Torpedo.FireTorpedo`, `Enemy_Missile.LaunchMissile`, aircraft drops), not on the whole
  frame. Finding every entry point is the main work; anything missed makes the AI look at the host while hunting the
  client. *Guess:* 20-40 patch points.
- The puppet must look enough like a player vessel to the AI code (acoustics values, `playercontrolled` checks,
  depth/mast state for radar/ESM). The puppet already carries the player's noise; masts, depth, speed and active sonar
  are in the state message.
- The AI fleet logic (`CombatAI`) shares one picture per fleet (WarnFleet, hunter/killer assignment). With two
  targets it needs one picture per target (keep the game's code, run it per target with the swap).

**B. Stand-in decoy (the MOSS trick).** Represent each client to the AI as a decoy-like object at the client's
position, emitting the client's noise; the AI hunts it as it hunts a MOSS. No AI changes, quick prototype. Limits:
sonar only (decoys don't show on radar/ESM/MAD, so a surface ship or a sub at periscope depth is invisible except by
sound), the single track merges host and client, and the AI's "decoyed" logic may give up on it. Good for an
experiment, not for release.

**C. Rewrite the AI's targeting** (several tracks per unit, choose a target). Cleanest, but means re-implementing a
large, undocumented AI. Not recommended.

### 3. AI weapons have to reach the client
An AI torpedo fired at a client exists in the host's game. Under "target decides" the client's game must decide the
hit: send AI weapons to clients the way player weapons are sent (spawn + state stream, the client's game reports hits;
reuse `Weapons.cs` with the host's AI as firer). Same for AI missiles, guns, depth bombs, RBU, helicopter torpedoes,
sonobuoys and active pings (clients must hear the AI's pings and see its sonobuoys). Effort: medium-large, mostly
plumbing.

### 4. Players are allies
- Each player sees the other as a friendly contact (side colours, ID, no torpedo warnings for the partner's weapons,
  air defence ignores them: `MaskAllies` exists for team battles).
- Friendly fire: torpedoes already home on any vessel in their list, so a partner can be hit. Keep it (as in single
  player) or add a setting.
- Sharing contacts: arcade (shared picture, as planned for teams) or hardcore (each crew on its own). Same choice as
  team battles; same code.

### 5. Mission flow
- Both games load the same mission. The host's game spawns the AI; the client's spawns copies (no AI). Start
  positions: partners near the mission's player start (team placement exists).
- A player whose vessel sinks: spectator camera until the end (the game ends the mission when "the player" sinks, so
  the host's game must not end while the client fights on, and vice versa: needs a patch on the mission-end checks).
- Victory/defeat when all hostiles are gone or all players are lost; result line per player.
- Mission types: start with the single missions (hunt, escort, ambush); some missions script the player's own role
  (land attack, rescue) and may not make sense for two.

### 6. Aircraft
AI helicopters and patrol aircraft run on the host. Clients need copies (the remote-helicopter builder in `Helos.cs`
can show AI aircraft too) and their weapons/sonobuoys (point 3). Their hunting goes through the same "whose turn" swap
(point 2).

### 7. Limits
- The enemy list holds 15 vessels; puppets, ghosts and neutrals share it with the AI. Large missions plus two
  players plus decoy ghosts may not fit (*read*: the mod already budgets 15 = opponent + 5 ghosts + 9 neutrals).
- Performance: the host runs all AI; clients run copies only (cheap).
- Determinism is not needed (one authority), but latency matters for close AI torpedo attacks on a client (same as
  player-vs-player today, acceptable at LAN/VPN latency).

### 8. Campaign (later, if ever)
The campaign (`CampaignManager`, 114 reads of the single player) adds ports, repairs, the strategic map, saves. A
co-op campaign would need a shared save on the host and both players' vessels in the campaign state. Single missions
first.

## Suggested order (when we get there)
1. **Prototype B (stand-in decoy), one evening:** confirm the AI hunts a client by sound in the host's game. Cheap
   proof that the AI can be steered at a second target.
2. **Host-run hostiles visible to the client** (extend Ambient): client invulnerable, AI weapons not yet sent.
3. **Approach A (whose-turn swap) for sonar and torpedo attacks:** AI hunts and shoots at either sub.
4. **AI torpedoes to the client** (client decides hits). First real co-op test: two subs vs a small surface group.
5. Radar/ESM/MAD, missiles, guns, depth bombs, aircraft.
6. Mission flow (spectator, mission end, result), friendly contacts, shared picture option.
7. Ships as co-op players (surface work done for duels carries over).

## Open questions for Raf
- Two players only, or up to four (more targets per AI decision)?
- Arcade (shared contacts) or hardcore (own sensors only), or a setting?
- Friendly fire on?
- When one player sinks: spectate, or end the mission for both?
- Which missions matter most (hunt a convoy, ASW against AI subs, escort)?
