# Cold Waters MP

**1v1 submarine duels for Cold Waters.** Two players, each in their own sub, hunting each other in the same ocean. It is a mod on top of [DotMod](https://github.com/DotModGroup/ColdWaters-DotMod), loaded with BepInEx.

> Status: **early test version.** It works end to end, but expect rough edges. Bug reports are welcome (see [Troubleshooting](#troubleshooting)).

## What you get

- **Full duel sync:** movement, sonar (passive and active), torpedoes, missiles with torpedo payloads, noisemakers, damage and sinking, all in both games.
- **Pick any single mission and any sub.** The host's map, weather, sea state, layer and difficulty are used for both players.
- **Decoys as "ghost subs":** a MOSS or Korund decoy shows up for the other player as a second sub of the launcher's class. A sharp sonar operator can spot the difference in the signature. Tag a contact as a suspected decoy with the DECOY button in the ID book, or with Ctrl+F5.
- **Neutral traffic:** merchants, fishing boats and whales, the same in both games. The host picks off, random, low, medium or high; the ID book always lists every neutral class the mission could have.
- **Fair play:** time compression only while both players ask for it. The host decides whether Esc pauses both games. Both installs must match: different game data, such as an edited weapons file, is refused at connect.

## Requirements

| | Version |
|---|---|
| Cold Waters | 1.15g. Tested on GOG; Steam should work but is untested (a GOG and a Steam player have not played each other yet). |
| DotMod | v0.4c, installed as DotMod describes (JSGME / DotModInstaller) |
| A way to reach each other | Same LAN, or a VPN such as [Tailscale](https://tailscale.com), ZeroTier or Radmin VPN |

Both players need the same game version, the same DotMod and the same mod version.

## Install (about 2 minutes)

1. Install Cold Waters and DotMod, and start the game once to check that DotMod works.
2. Download `ColdWatersMP-<version>.zip` from [Releases](../../releases).
3. Extract **everything** in the zip into your Cold Waters folder, the one that holds `ColdWaters.exe`. On GOG this is usually `C:\Program Files (x86)\GOG Galaxy\Games\Cold Waters`. You should now have `winhttp.dll`, `doorstop_config.ini` and a `BepInEx` folder next to `ColdWaters.exe`.
4. Start the game. The main menu now has a **MULTIPLAYER** button.

The zip contains [BepInEx 5](https://github.com/BepInEx/BepInEx) (x86), already set up for Cold Waters, plus the mod. No game file is changed.

## Play

1. **Host:** MULTIPLAYER, choose **Host**, then **Start hosting**. The window shows your addresses; give the right one to your opponent (your VPN address if you use one). The first time, Windows asks whether Cold Waters may use the network: allow it. The host needs incoming **UDP port 7777**.
2. **Client:** MULTIPLAYER, choose **Client**, type the host's address, then **Join**. The status line says *Ready* when you are connected.
3. Both players open **Single Mission**, pick a mission and a sub, and start. The host's mission and conditions are used. Each player waits at battle stations until the other is ready.

**Playing over the internet with Tailscale:** both PCs must be in the same Tailscale network; having Tailscale installed is not enough. With two accounts, the host shares their machine with the other player in the Tailscale admin console (*Share*), or invites them into their network. `tailscale status` should list the other PC; the client then joins the host's `100.x.y.z` address.

**Address with a port:** the client's address box takes `host` or `host:port`, for tunnels such as playit.gg that give their own port.

**Session password (recommended with strangers):** type the same password on both sides; the host refuses and drops anyone without it, and also drops a connection that sends nothing valid within 5 s. It is a door lock, not encryption: game traffic is not encrypted, and the password is stored as plain text in `cwmp.cfg`, so use a throwaway one.

**Disconnect** (or **Stop hosting**) in the window ends the connection; during a duel it asks for a second click.

Host settings in the MULTIPLAYER window (Ctrl+F6): conditions (as the mission, random, or custom), starting range, pause, time compression, Reduced Visibility and neutral traffic.

## Keys

| Key | Does |
|---|---|
| Ctrl+F5 | Tag the selected contact as a suspected decoy (same as the DECOY button in the ID book) |
| Ctrl+F6 | Multiplayer window |
| Ctrl+F8 | Start a mission that is waiting for the opponent anyway |
| Ctrl+F9 | Status line: compact / off |
| Ctrl+F12 | Reconnect |

The game's own F-keys are not changed.

## Known limits

- Only 1v1, only submarines, no aircraft.
- Decoys don't explode when a torpedo reaches them. This is how Cold Waters and DotMod treat decoys.
- Your opponent sees only your player name (set it in `BepInEx\config\cwmp.cfg`, default "Player").

## Safe download

The mod is a DLL that runs inside your game with your rights, like any mod. Only download it from **this repository's [Releases](../../releases) page**. Copies elsewhere, or "improved" versions under the same name, are not ours.

Each release lists the SHA-256 of the zip and of `ColdWatersMP.dll`. To check yours, run this in PowerShell in the folder where you saved it and compare:

```
Get-FileHash .\ColdWatersMP-<version>.zip
```

The source code is all here, so anyone can read what it does. Forks are welcome, but the licence requires a changed version to be plainly marked as changed and never passed off as the original.

## Update

Close the game, extract the new zip over the old files and overwrite everything. Your settings in `BepInEx\config\cwmp.cfg` are kept (the zip doesn't contain that file). Both players need the same version, or the join is refused with a message saying so.

## Uninstall

Delete `winhttp.dll`, `doorstop_config.ini`, `.doorstop_version` and the `BepInEx` folder from the Cold Waters folder.

## Troubleshooting

- **No MULTIPLAYER button:** check that `winhttp.dll` and `BepInEx` sit next to `ColdWaters.exe`. Then open `BepInEx\LogOutput.log` and look for `Cold Waters MP ... loaded`.
- **Can't connect:** check the address, whether the host has started hosting, the Windows firewall (UDP 7777 on the host), and whether your VPN is connected on both PCs.
- **"Refused: ... differs":** the two installs differ. Use the same mod version, and put back any edited game files (or reinstall DotMod) on one side.
- **Bug report:** open an issue with `BepInEx\LogOutput.log` from both players and what happened.

## For developers

This repository is source only: no game, DotMod or BepInEx files. The plugin is C# 5 / .NET 3.5 with Harmony. Build with `src/ColdWatersMP/build.cmd` against your own game's `Managed` folder and BepInEx. Notes on the game's code are in `docs/`. `package/` holds the files that go into the release zip next to the DLL.

## Licence

zlib (see [LICENSE](LICENSE)): free to use and change; changed versions must be marked as such. BepInEx inside the release zip keeps its own licence (MIT, included as `BepInEx/LICENSE-BepInEx.txt`).

## Credits

- [DotMod](https://github.com/DotModGroup/ColdWaters-DotMod) by the DotMod team: this mod is built on top of it.
- [BepInEx](https://github.com/BepInEx/BepInEx), [HarmonyX](https://github.com/BepInEx/HarmonyX), MonoMod and Mono.Cecil (all MIT).
- Cold Waters by Killerfish Games. This is an unofficial fan mod.
