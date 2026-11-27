# Battle rating (draft table)

Status: first computed draft (27 Nov), not playtested. Part of [match mode](MATCH-MODE.md). Computed from the
Cold Waters 1.15g + DotMod v0.4c data files by a script kept outside the repo (`research/br-draft.ps1` in the local
working folder); the numbers below are a starting point for the hand-tuned `cwmp-balance.cfg`, not the final values.

## How the draft is computed

Four parts, each 0-1, weighted and scaled to BR 1.0-6.1 in steps of 0.3:

| Part | Weight | From |
|---|---|---|
| Acoustic edge | 45% | Hull sonar sensitivity + towed-array bonus - SelfNoise / 3.2 (+ a little for a narrow baffle). This is the game's own detection sum (see [MATCH-MODE.md](MATCH-MODE.md#how-passive-detection-works-in-the-game)): 1 dB more is the same range gained, for hearing or for staying unheard. |
| Weapon | 30% | Best anti-sub torpedo the boat can load: speed 35%, range 20%, seeker range 20%, wire 15%, warhead 10%. +0.12 for a rocket-launched torpedo (RPK-6, RPK-2). Anti-ship-only torpedoes (53-65, 65-76, Mk16, Mk8, SAET-60M, Yu-1/4) don't count. |
| Survival | 15% | Hull points (from displacement, as the game computes them) 45%, speed 30%, test depth 15%, anechoic coating 10% (it halves active-homing torpedoes' acquisition range). |
| Firepower | 10% | Tubes 50%, reload time 30%, wires 20%. |

Hull points as the game sets them (`DamageSystem.DamageInit`, submarines get half): displacement up to 5,000 t:
d/30 + 9; up to 12,000 t: d/50 + 76; above: d/60 + 116; then x0.5. Examples: Kilo 70, Los Angeles 107, Akula 164,
Oscar 246, Typhoon 458.

## Hunters (SSN, SS, SSK)

| BR | Sub | Acoustic edge (dB) | Best torpedo | Rocket torpedo | Hull points |
|---|---|---|---|---|---|
| 6.1 | US Seawolf '99 | 21.2 | Mk48 ADCAP | | 129 |
| 5.8 | US Virginia '04 | 21.9 | Mk48 ADCAP | | 117 |
| 5.8 | UK Trafalgar '04 | 20.3 | Spearfish | | 91 |
| 5.8 | USSR Yasen '04 | 20.6 | TEST-71MK | yes | 173 |
| 5.2 | UK Trafalgar '99 | 15.0 | Spearfish | | 91 |
| 5.2 | US Los Angeles Flight III '99 | 15.6 | Mk48 ADCAP | | 107 |
| 5.2 | US Los Angeles Flight II '99 | 14.7 | Mk48 ADCAP | | 107 |
| 4.9 | AU Collins '99 | 13.4 | Mk48 ADCAP | | 56 |
| 4.9 | USSR Akula I '84 | 12.1 | TEST-71MK | yes | 164 |
| 4.6 | US Los Angeles '84 | 12.1 | Mk48 ADCAP | | 107 |
| 4.6 | US Narwhal '84 | 13.6 | Mk48 ADCAP | | 92 |
| 4.6 | US Sturgeon '84 | 11.1 | Mk48 ADCAP | | 82 |
| 4.3 | USSR Sierra '84 | 7.4 | TEST-71MK | yes | 119 |
| 4.3 | US Permit '84 | 8.9 | Mk48 ADCAP | | 76 |
| 4.0 | USSR Victor III '84 | 6.5 | TEST-71MK | yes | 110 |
| 4.0 | USSR Mike '84 | 3.4 | TEST-71M | yes | 119 |
| 3.7 | UK Trafalgar '84 | 8.1 | Tigerfish | | 91 |
| 3.7 | US Barbel '84 | 6.5 | Mk48 | | 34 |
| 3.7 | China Song '99 | 4.1 | Yu-6 | | 42 |
| 3.4 | USSR Kilo '84 | 4.2 | TEST-71MK | | 70 |
| 3.4 | China Kilo '99 | 3.6 | TEST-71MK | | 70 |
| 3.4 | UK Churchill '84 | 6.5 | Tigerfish | | 91 |
| 3.4 | UK Valiant '84 | 6.5 | Tigerfish | | 91 |
| 3.4 | US Skipjack '84 | 1.8 | Mk48 | | 63 |
| 2.8 | USSR Victor II '84 | -5.6 | TEST-71M | yes | 110 |
| 2.8 | USSR Tango '84 | 0.5 | TEST-71M | | 68 |
| 2.8 | NL Zwaardvis '84 | 3.7 | NT37C | | 48 |
| 2.5 | USSR Alfa '84 | -4.2 | SET-65M | yes | 66 |
| 2.5 | UK Oberon '84 | -0.2 | Tigerfish | | 45 |
| 2.2 | US Sturgeon '68 | -0.5 | Mk37 mod 2 | | 82 |
| 2.2 | US Permit '68 | -2.1 | Mk37 mod 2 | | 76 |
| 2.2 | China Han '99 | -10.5 | Yu-6 | | 93 |
| 2.2 | NO Kobben '68 | -3.9 | NT37C | | 12 |
| 1.9 | USSR Victor I '68 | -7.2 | SET-65M | | 99 |
| 1.9 | UK Valiant '68 | 0.5 | Mk20S | | 91 |
| 1.9 | China Ming '99 | -2.1 | Yu-3C | | 40 |
| 1.6 | USSR Foxtrot '68 | -7.1 | SET-65M | | 46 |
| 1.6 | UK Oberon '68 | -0.3 | Mk20S | | 45 |
| 1.6 | UK Dreadnought '68 | -2.6 | Mk20S | | 91 |
| 1.6 | US Guppy III '68 | -7.1 | Mk37 mod 2 | | 45 |
| 1.6 | US Barbel '68 | -5.5 | Mk37 mod 2 | | 34 |
| 1.3 | US Skipjack '68 | -10.2 | Mk37 mod 2 | | 63 |
| 1.3 | USSR November '68 | -11.8 | SET-65M | | 84 |
| 1.3 | USSR Zulu '68 | -8.3 | SET-65M | | 46 |
| 1.3 | USSR Romeo '68 | -8.0 | SET-65M | | 35 |
| 1.3 | China Romeo '99 | -9.6 | Yu-3C | | 35 |
| 1.3 | NL Dolfijn '68 | -3.3 | Mk20S | | 35 |
| 1.3 | Taiwan Guppy (Tench) '73 | -11.1 | A184 | | 45 |
| 1.0 | USSR Whiskey '68 | -8.6 | SET-65M | | 27 |
| 1.0 | US Nautilus '68 | -13.6 | Mk37 mod 2 | | 73 |
| 1.0 | US Guppy IIA '68 | -11.1 | Mk37 mod 3 | | 45 |

## Missile and ballistic-missile boats (SSBN, SSGN, SSG, SSB)

Computed the same way, but their job is not hunting submarines. Proposal: not allowed in 1v1 by default; allowed in
team games and scenarios (an Oscar or Typhoon is a natural objective holder or the "missile sub to hunt").

| BR | Sub | Acoustic edge | Best torpedo | Rocket torpedo | Hull points |
|---|---|---|---|---|---|
| 4.9 | US Ohio '84 | 15.6 | Mk48 | | 214 |
| 4.3 | USSR Oscar '84 | 4.9 | TEST-71MK | yes | 246 |
| 4.3 | UK Resolution '84 | 15.6 | Tigerfish | | 214 |
| 4.0 | USSR Typhoon '84 | 5.2 | TEST-71M | yes | 458 |
| 4.0 | USSR Delta IV '84 | 6.5 | TEST-71M | yes | 210 |
| 3.7 | USSR Delta III '84 | 4.9 | TEST-71M | yes | 166 |
| 3.7 | UK Resolution '68 | 15.6 | Mk20S | | 214 |
| 2.8 | USSR Charlie II '84 | 0.0 | SET-65M | yes | 92 |
| 2.2 | USSR Papa '84 | -6.6 | SET-65M | yes | 109 |
| 2.2 | China Xia '99 | -10.5 | Yu-6 | | 118 |
| 1.6 | USSR Charlie I '68 | -9.6 | SET-65M | | 88 |
| 1.6 | USSR Juliet '68 | -9.2 | SET-65M | | 74 |
| 1.6 | USSR Golf '68 | -8.6 | SET-65M | | 63 |
| 1.6 | USSR Yankee '68 | -10.8 | SET-65M | | 153 |
| 1.6 | US George Washington '68 | -10.2 | Mk37 mod 2 | | 105 |
| 1.0 | USSR Echo II '68 | -13.3 | SET-65M | | 96 |

## Team points (draft)

Cost = 2^BR, rounded. Examples: BR 1.0 = 2, 2.5 = 6, 3.4 = 11, 4.0 = 16, 4.6 = 24, 4.9 = 30, 5.2 = 37, 5.8 = 56,
6.1 = 69. So one Seawolf (69) is worth a bit more than two Akulas (60), or about four Victor IIIs (64). A team budget of 60
buys two Akulas, or one LA '84 with two Victor IIIs, or one Virginia alone.

## What to check before trusting it

- **Loadouts**: the draft assumes each boat carries the best torpedo it can load (including DotMod's alternate
  loadouts). The '84 Sturgeon, Narwhal and Permit get ADCAP that way, which may be generous; if the mod fixes
  loadouts per match, rerun with the fixed loadout.
- **Speed and depth counters** are undervalued by a weighted sum: an Alfa (41 kn) outruns every 1960s torpedo, a Mike
  (3,927 ft) sits below every torpedo's depth limit except ADCAP's. These need the pairing warnings from the spec,
  or a hand-tuned BR bump.
- **Weights are a guess** (45/30/15/10). The acoustic part is grounded in the game's formula; how much weapon
  quality is worth against it is not.
- **Active sonar** is not in the draft.
- Same-BR pairings that look right on paper (LA '84 vs Akula, Sierra vs Permit '84, Victor I vs Sturgeon '68)
  are the first ones to playtest.
