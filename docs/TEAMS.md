# Team battles (design draft)

Status: research and design, nothing built yet. Goal: up to 4 against 4, any split (1v3, 2v1, ...), with an
arcade mode (teams share what they know) and a hardcore mode (each sub only knows what its own crew detects).
Each player still runs their own game; the mod keeps the games in step, as in the 1v1 duel.

## What the game allows (from reading its code, Cold Waters 1.15g + DotMod v0.4c)

- **One player sub per game, hard-coded.** `LevelLoadManager.LoadLevel` sets `playerNumberofShips = 1`, and the
  game reads `playervesselsonlevel[0]` in about 150 places. Teammates can't sit on the player's side of a mission.
- **Everything else is the "enemy" list.** `enemyvesselsonlevel` is sized from the mission's ship list, capped at
  `GameDataManager.maxShips = 15` (a static set in the class constructor). The sonar arrays and map markers are
  sized from the actual list length; the cap itself is only used when a mission is read and in one camera check.
  Raising it for team games looks possible but is untested.
- **Torpedoes can target any vessel in that list** (`Torpedo.SetTorpedoTargetsList`: the player sub plus every
  non-sinking vessel in the enemy list, minus a few surface/missile filters). The mod already removes decoy ghosts.
  So a teammate placed in the list can be hit by your torpedo: friendly fire is possible without extra work.
- **Sonar hears every vessel in the enemy list** the same way (`SensorManager.SonarCheck`). A teammate in that list
  is detected, numbered and classified like any contact unless the mod steps in.

Conclusion: **every other player's sub, friend or foe, is a remote-controlled vessel in your enemy list** (as the
opponent is today), with the mod deciding how friends are shown.

## How it would work

### Network
- The host is a hub: every client connects only to the host (one forwarded port, or one VPN address, as now), and
  the host relays each player's messages to the others. Up to 8 players.
- Each message carries the sending player's number. The handshake (version, DLL hash, DotMod data) runs per client.
- Time compression starts only when every player asks for it; any player can stop it. Pause follows the host
  setting, as now.

### Lobby
- The host opens a team lobby: two teams (side A / side B), up to 4 each, any split. Each player picks a sub in
  the normal mission screen; the lobby shows who is on which team, their sub and whether they are ready.
- The host picks the mission, conditions, mode (arcade or hardcore) and the starting distance between teams.

### The mission in each game
- Your enemy list holds: every other player's sub, decoy slots for each enemy sub's class, and neutral traffic.
  With 7 other players that needs the 15-ship cap raised (for example to 30), which has to be tested first.
- Placement: each team starts together (spread a little), the teams apart by the host's range band.
- A sub's decoy ghost must have the launcher's class, so decoy slots are reserved per enemy class (2 per enemy is
  probably enough; today a 1v1 has 5).

### Hits and damage (unchanged principle)
- The torpedo runs for real in the firer's game and as a copy everywhere else. Only the game of the sub that gets
  hit decides the hit and the damage, and tells everyone; a copy hitting someone else's sub in your game is ignored.
- Friendly fire works the same way: your torpedo can go for a teammate if its seeker picks them.

### Arcade mode (teams share what they know)
- Teammates are always visible on your map in your side's colour with the player's name, and never become contacts.
- An enemy contact any teammate holds is shown to the whole team: one shared number (S1 is S1 for everyone on the
  team), shared classification, and the best solution anyone has. Decoys a teammate has tagged are tagged for all.
- Simple to play, and the closest to how the game feels in single player.

### Hardcore mode (each crew on its own)
- Teammates are contacts like any other: your sonar has to hear them, they get a number, and you classify them.
  Your side's colour (blue or red) after classification is the only help in telling friend from foe.
- Nothing is shared automatically. Coordination is by voice (Discord) or chat.
- A teammate's decoy fools you too.

## What changes in the mod
- `Transport` / `Session`: several peers, host relay, per-player ids, per-client handshake.
- `Duel`: from "the puppet" to a list of remote players (sub, team, state); placement per team; end of match when one
  team has no sub left (draw if both lose their last sub at once).
- `Weapons`, `Ghosts`, `Tracks`, noise and towed-array code: everything that now reads "the opponent" (around 60
  places) becomes "the remote player this vessel belongs to".
- New: lobby window, team colours (side colours already work), arcade sharing of contacts, friend display.

## Order of work
1. Multi-player network and lobby, still 1v1 in play (proves the hub, ids and handshake).
2. 1v2 and 2v1 (several enemies, one teammate, raised ship cap). Hardcore first: it needs no sharing.
3. Arcade sharing (shared contacts and numbers, friend display).
4. Up to 4v4, balancing and cleanup.

Each step stays on `dev`; the release comes with the finished feature.

## Testing
Two game copies on one PC work today (small windows). Three may be possible; more needs real PCs. Steps 1 and 2 can
mostly be tested here; 3v3 and 4v4 need Raf, the laptop and players from the Discord.

## Open questions
- Raising the 15-ship cap: safe? (Test early in step 2.)
- Teammates in arcade mode: shown as fixed friendly markers on the map only, or also in 3D?
- Respawn or spectating after your sub is sunk? (Spectating your team's map would be a nice extra.)
- Should AI subs fill empty slots (for example 2 players vs 3 AI)? Not planned now.
