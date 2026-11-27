# Match mode (design draft)

Status: spec only, nothing built (27 Nov). Builds on team battles ([TEAMS.md](TEAMS.md)): host hub, lobby with two
teams, several remote subs per mission.

## Why

Today the only way to win is to sink the other side. The safest play is to sit deep, slow and silent and wait, so a
competitive match turns into nobody doing anything. Match mode adds an objective that rewards whoever goes and holds
it, and a balance system so the subs on both sides are worth roughly the same.

## Overview

- New host setting **Mode**: `Classic` (today: last team with a sub wins) or `Area control`.
- In area control a point on the map is revealed a few minutes after the start. The team closest to it, within a
  maximum radius, scores. First to the target score wins; otherwise the higher score at the time limit.
- Sinking still counts: a sunk sub stops scoring, and a team with no sub left loses at once.
- Works for 1v1 and for teams of up to 4 per side, with any split (1v3, 2v1, ...).
- Sub choice is limited by a **battle rating** per sub class (1v1) or a **points budget** per team (team games).

## Area control

### The objective
- A short reason in the briefing, picked at random, so the spot is not arbitrary. Examples: "a crashed test
  warhead lies on the bottom here", "an enemy seabed listening array to tap", "a downed satellite's beacon".
  Shown as an objective marker on the map once revealed.
- **Placement** (host's game, at mission start, hidden): deep water checked with both terrain probes (as for sub
  placement), and about equal distance from each team's starting position (team average). Never closer than ~40%
  of the start distance to either team.
- **Reveal**: to everyone at the same moment, default 3 minutes after Battlestations. Before that nobody scores.

### Scoring
- **Who counts**: only real player subs that are not sunk. **Decoy ghosts never score or contest** (the host scores
  from each player's own reported position, never from what the other side sees). Neutral traffic and torpedoes do
  not count either.
- **Rule** (host setting, default `Closest within radius`):
  - `Closest within radius`: the team with the sub nearest the point scores, as long as that sub is within the
    outer radius (default 8 nm). The score rate is full within the inner radius (default 2 nm) and falls off
    linearly to zero at the outer radius. Pulls players in without making the edge a magic line.
  - `Area`: a team scores at full rate while it has a sub inside the radius (default 4 nm) and the other team does
    not. Simpler, easier to explain.
- **Contested**: subs of both teams inside the inner radius: nobody scores until one side leaves or is sunk.
- **Teams score per team, not per sub**: three subs near the point score no faster than one.
- **Rate**: 1 point per 10 s at full rate. Default target 100 points (~17 minutes of uncontested holding), time limit
  45 minutes. Game time, so time compression (only by agreement) speeds everything up equally.
- **Score indicator**: each player sees both scores and a "the enemy is scoring" light. That tells you someone is
  near the objective without saying where, which pushes the trailing side to go and look.
- **End**: target score reached, last sub of a team sunk, or time limit. Equal scores at the time limit: overtime
  until the next point is scored.

### Relocating objective (experimental, default off)
Optional host setting: the objective jumps to a new spot every N minutes (with a warning a minute before), so the
holder has to move and make noise. Unclear whether it is fun or just chaotic; try it in playtests before deciding.

### What the network needs
- The host is the referee: it places the objective, computes scores from the positions it already receives, and
  sends `G_OBJECTIVE` (position, reveal time, story line) and `G_SCORE` (both scores, who is scoring, end of match)
  to everyone.
- Match settings go out with the existing match message (`G_MATCH`) at load, as map and conditions do now.
- The result line (`Duel.cs`) gets the reason: target score, time limit, overtime or last sub sunk.

## Balance

### What the game data says (Cold Waters 1.15g + DotMod v0.4c)

Read from `StreamingAssets/dotmod/vessels/01_Submarine` (67 sub files), the sonar files and the weapon files.

| Factor | How it works in the game | Range across subs |
|---|---|---|
| Hull strength | `DamageSystem.DamageInit`: damage points come from **displacement** (with size-band multipliers). Bigger hull, more hits to sink. | Kobben 485 t, LA 6,927 t, Akula 12,770 t, Oscar 22,500 t, Typhoon 48,000 t |
| Self noise | `SelfNoise` (dB) plus anechoic coating. 10 dB is a big difference in detection range. | Virginia/Seawolf 105, Akula/LA 115, Victor I 135, Nautilus 146, Han/Xia 152 |
| Sonar | Hull sonar `SonarPassiveSensitivity` (32 for 1960s, 40 for 1980s, 45-46 for Collins, Virginia, 2004 Trafalgar) plus towed array (44-54, many subs have none). Baffle 150 deg for almost all; Oberon 85, 2004 Trafalgar and Virginia 130. | |
| Speed | `SubmergedSpeed` (kn). Matters most against slow torpedoes. | Romeo 13, LA 33, Seawolf/Yasen 35, Alfa 41, Papa 44.7 |
| Test depth | `TestDepth` (ft) against the enemy torpedo's `MaxDepth`. | LA 1,200, Akula 1,969, Mike 3,927 (below every torpedo's limit except ADCAP's 4,020) |
| Tubes, reload | Tubes, wires, `TubeReloadTime` | NATO mostly 4-6 tubes and 45 s; Soviet 6-8 tubes and 30 s (Yasen 11) |
| Torpedo | Speed, range, seeker, wire guidance, warhead | See below |

Key torpedoes (speed in kn as slow,fast setting; range in yd; seeker range in yd):

| Torpedo | Warhead | Speed | Range | Seeker | Wire |
|---|---|---|---|---|---|
| Mk37 mod 2 (US 1960s) | 225 | 17 / 26 | 23,000 / 9,952 | 1,000 | yes |
| Mk48 | 550 | 40 / 55 | 43,744 / 32,808 | 3,000 | yes |
| Mk48 ADCAP | 550 | 50 / 65 | 54,680 / 38,276 | 3,500 | yes |
| Spearfish (UK) | 550 | 45 / 80 | 35,000 / 11,000 | 4,200 | yes |
| Tigerfish | 453 | 24 / 35 | 35,000 / 20,000 | 876 | yes |
| SET-65M (Soviet 1960s) | 300 | 40 | 17,500 | 880 | no |
| TEST-71M | 300 | 35 / 40 | 27,340 / 16,404 | 1,450 | yes |
| USET-80 | 375 | 48 | 19,685 | 1,640 | no |
| 65-76 (anti-ship) | 557 | 50 / 45 | 54,680 / 89,361 | 2,600 | no |

Sonar of the 1984 front-line boats (from `sensors/01_SONAR`):

| Sub | Hull sonar | Active / passive | Towed array | Towed passive |
|---|---|---|---|---|
| Los Angeles '84 | BQQ-5 | 28 / 40 | TB-16 | 48 (noise per knot 2) |
| Akula | MGK-540 | 28 / 40 | Pelamida | 48 (noise per knot 1) |
| Victor III, Sierra | MGK-500 | 28 / 40 | MGK-500 TA | 44 |
| Victor II, Alfa, Mike, Kilo | | 35-40 passive | none | |
| Virginia ('04) | BQQ-10 | 34 / 46 | | 54 |

So in the files, NATO's sonar edge is not in the 1984 hull sonars (identical numbers) but in towed arrays (many
Soviet boats have none, Victor III/Sierra's is weaker) and in the post-1999 NATO sets. Not yet checked: how the game
turns these numbers into detection, and the per-frequency noise signatures (`vessels/signature`), which may favour
NATO further.

Rocket-launched torpedoes: Soviet only in practice. RPK-6 (Akula, Sierra, Victor II/III, Alfa, Mike, Yasen, Oscar,
Typhoon, Delta, Papa, Charlie II): 6,000-55,000 yd, drops a UMGT-1 (41 kn, 275 warhead, 1,650 yd seeker) on the
target area within minutes, no wire. The US Sea Lance (UUM-125B, 71,000 yd, Mk50 payload) exists in the data but no
sub carries it.

### What that means
- **Time period is not enough.** Same-era subs are very different: an Alfa (41 kn, loud, small hull) and a Kilo
  (17 kn, very quiet) are both "1980s Soviet". A 1984 Los Angeles has the best torpedo in the game but no anechoic
  coating; a 1984 Akula is as quiet, dives 60% deeper, has twice the tubes and nearly twice the hull, but its
  torpedo is slower, shorter-ranged and not wire-guided.
- **NATO's edge is the torpedo and the towed array, the Soviet edge is the hull and the rocket torpedo.** ADCAP
  outranges USET-80 by almost 3x and is faster, wire-guided and heavier, and most NATO boats tow an array. Against
  that, a big Soviet hull needs more hits, Soviet boats fire and reload faster, and the RPK-6 puts a torpedo on a
  distant contact within minutes (a long-range snap shot NATO has no answer to, but its small torpedo has no wire).
- **Detection probably matters most.** Whoever hears first chooses when the fight starts. The rating should weigh
  detection (hull sonar, towed array, baffles) about as high as stealth, and both above firepower.
- **Hard counters exist**: a Mike sits below the depth limit of every torpedo except ADCAP; an Alfa or Papa outruns
  Mk37s and SET-65s; a 1960s Mk37 at 26 kn cannot catch a 1980s SSN. Pure stat sums would miss these, so pairings
  need a check, not just a number.
- **Loadout choice matters**: many subs can carry several torpedo types (an '84 Los Angeles carries Mk48 or ADCAP).
  The rating must use the weapons actually loaded, or the mod must fix the loadout per rating.
- **Missile boats and SSBNs** (Oscar, Typhoon, Ohio) are huge hulls with few or weak anti-sub weapons. Tanky but
  toothless; fine in team games as an objective holder, odd in a 1v1.

### Proposal: battle rating (BR)
1. **Computed starting value** per sub class, from five scores (each normalised 0-1 across all subs):
   stealth (self noise, anechoic), detection (hull and towed sonar, baffles), weapon (best loaded anti-sub torpedo:
   speed, range, seeker, wire, warhead), firepower (tubes, wires, reload, torpedoes carried) and survival (hull from
   displacement, speed, depth margin against the enemy's torpedo). Weighted sum, scaled to a BR of 1.0-6.0 in 0.3
   steps. Stealth and detection weigh most, then weapon (including rocket torpedoes).
2. **Hand-tuned overrides** in a config file (`cwmp-balance.cfg`, shipped with the mod, same on both sides because
   the handshake already compares data): `usn_84_ssn_los_angeles = 4.3`, and so on. The computed value is only the
   start; the file is what counts.
3. **Pairing check**: a short list of hard counters (depth, speed vs torpedo speed) that the lobby warns about.
4. **Tuning from results**: the mod logs each match (classes, BRs, result, reason). Collect those from playtests and
   adjust the file.

Host settings:
- 1v1: `Free`, or `Max BR difference` (default 0.7): both subs must be within 0.7 BR of each other.
- Teams: **points budget**. Each sub costs points that grow faster than its BR (for example cost = 2^BR, rounded), so
  three weak boats can match one strong one but not swamp it. The host sets the budget per team (equal by default,
  or a handicap). The lobby shows each team's spend and refuses Ready over budget.
- The rule is enforced at load: a sub that breaks it is refused with a clear message, before Battlestations.

### Same-side matches
- Host setting `Allow same nation` (off by default): NATO vs NATO, Soviet vs Soviet.
- Contact colours are by side (NATO blue, WP red) today. With same-side matches they must be by **team** instead
  (own team blue, enemy team red), or a hostile Los Angeles shows up as a friendly. Same for the ID book page and
  the puppet's side: the game expects the enemy to be Soviet in places; check before enabling.

## Order of work
1. Area control in 1v1: objective placement and reveal, `Closest within radius` scoring, score indicator, time
   limit, overtime. Decoys excluded from the start.
2. Battle rating: compute starting values, ship `cwmp-balance.cfg`, `Max BR difference` for 1v1, match log.
3. Team games: per-team scoring and contesting, points budget in the lobby.
4. Same-nation toggle with team colours.
5. Playtests: tune radii, rate, target score, BRs; decide on the relocating objective.
6. Later: scenarios (hunt the missile sub, transit/barrier) on top of the same objective and scoring code.

## Open questions
- Default match length and target score: 45 min / 100 points is a guess.
- `Closest within radius` or `Area` as the default rule.
- Whether the score indicator gives away too much.
- Whether sunk subs' teams can still win on points if the other team is ahead in subs but behind in score (current
  draft: yes, until their last sub is sunk).
- BR weights; needs a first computed table to argue about.
