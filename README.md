# Cold Waters MP

**1v1 submarine duels for Cold Waters.** Two players, each in their own sub, hunting each other in the same ocean. It is a mod on top of [DotMod](https://github.com/DotModGroup/ColdWaters-DotMod), loaded with BepInEx.

> Status: **early test version.** It works end to end (tested GOG to GOG over LAN, Radmin VPN and Tailscale), but expect rough edges. Bug reports are welcome (see [Troubleshooting](#troubleshooting)).

## What you get

- **Full duel sync:** movement, sonar (passive and active), torpedoes, missiles with torpedo payloads, noisemakers, damage and sinking, all in both games.
- **Pick any single mission and any sub.** The host's map, weather, sea state, layer and difficulty are used for both players. Difficulty changes hull strength, TMA, reload and repair times; it does not change how loud contacts are.
- **Decoys as "ghost subs":** a MOSS or Korund decoy shows up for the other player as a second sub of the launcher's class, as loud as the launcher would be at the decoy's speed. A sharp sonar operator can spot it by its signature, its steady course and speed, or by running a torpedo past it. Tag a contact as a suspected decoy with the DECOY button in the ID book, or with Ctrl+F5.
- **Contact tracks that don't give decoys away:** a decoy heard right next to a contact you track may take over that contact's number (a coin decides which of the two keeps it). A lost contact loses its number once its solution has decayed and leaves a faded "S1 Barbel?" marker; a new contact near that marker takes the old number back. Delete the marker with shift-click like any nav marker.
- **Contact colours by side:** NATO blue, Warsaw Pact red, unidentified and civilian green, including the opponent's torpedoes and noisemakers.
- **Neutral traffic:** merchants, fishing boats and whales, the same in both games. The host picks off, random, low, medium or high; the ID book always lists every neutral class the mission could have.
- **Fair play:** time compression only while both players ask for it. The host decides whether Esc pauses both games. Both installs must match: different game data, such as an edited weapons file, is refused at connect.

## Requirements

| | Version |
|---|---|
| Cold Waters | 1.15g. Tested on GOG; Steam should work but is untested (a GOG and a Steam player have not played each other yet). |
| DotMod | v0.4c, installed as DotMod describes (JSGME / DotModInstaller) |
| A way to reach each other | Same LAN, a VPN such as [Radmin VPN](https://www.radmin-vpn.com) (easiest), [Tailscale](https://tailscale.com) or ZeroTier, or direct over the internet with UDP port 7777 forwarded to the host |

Both players need the same game version, the same DotMod and the same mod version.

## Install

Two downloads on the [Releases](../../releases) page (from 0.0.44 on), pick one:

**A. Setup, for a clean game (no modding needed; with JSGME it adds the mod to JSGME):** `ColdWatersMP-Setup-<version>.zip`. Extract it anywhere and double-click `Install.cmd`. It finds your Cold Waters folder, downloads DotMod v0.4c from the DotMod team's GitHub (about 740 MB, checked by SHA-256), installs it and then this mod. If DotMod is already installed, it only adds the mod. `Uninstall.cmd` puts back exactly what was there before.

**B. JSGME, if you already run DotMod with JSGME:** `ColdWatersMP-<version>.zip`.

1. Extract the zip into the `MODS` folder in your Cold Waters folder (the one that holds `ColdWaters.exe`), so you get `MODS\ColdWatersMP`.
2. Start JSGME and enable **ColdWatersMP** (DotMod stays enabled too).
3. Start the game. The main menu now has a **MULTIPLAYER** button. Disable the mod in JSGME to play without it.

Without JSGME you can also copy the *contents* of the `ColdWatersMP` folder into the Cold Waters folder, so that `winhttp.dll`, `doorstop_config.ini` and a `BepInEx` folder sit next to `ColdWaters.exe`.

Both contain [BepInEx 5](https://github.com/BepInEx/BepInEx) (x86), already set up for Cold Waters, plus the mod. No game or DotMod file is changed.

**Check your DotMod data:** both players' DotMod files must match. `check-dotmod.ps1` (in the setup zip and in `tools/`) compares yours with the official v0.4c files and lists any difference. With JSGME, disabling and re-enabling DotMod usually fixes it.

## Play

1. **Host:** MULTIPLAYER, choose **Host**, then **Start hosting**. The window shows your addresses; give the right one to your opponent (your VPN address if you use one). The first time, Windows asks whether Cold Waters may use the network: allow it. The host needs incoming **UDP port 7777**.
2. **Client:** MULTIPLAYER, choose **Client**, type the host's address, then **Join**. The status line says *Ready* when you are connected.
3. Both players open **Single Mission**, pick a mission and a sub, and start. The host's mission and conditions are used. Each player waits at battle stations until the other is ready.

**Playing over the internet (Radmin VPN, easiest):** both players install the free [Radmin VPN](https://www.radmin-vpn.com). The host chooses *Network > Create new network* with a name and a password and gives both to the other player, who chooses *Network > Join existing network*. In the MULTIPLAYER window the client then joins the host's Radmin address (`26.x.y.z`, shown in Radmin next to the host's name). Leave the network when you're done: everyone in a VPN network can reach your PC, not just the game, so only share it with people you play with.

**Tailscale** works too: both PCs must be in the same Tailscale network; having Tailscale installed is not enough. The host shares their machine in the Tailscale admin console (*Share*) or invites the other player, who then joins the host's `100.x.y.z` address.

**Direct, without a VPN (port forwarding):** the mod talks plain UDP, so no VPN is needed if the host's router lets the game in.
1. The host forwards **UDP port 7777** in their router to their PC's local address (the `192.168.x.y` the MULTIPLAYER window shows), and allows Cold Waters through the Windows firewall (or allows UDP 7777 inbound).
2. The host looks up their public address, for example on a "what is my IP" website (the MULTIPLAYER window only shows local addresses).
3. The client joins that public address. Only the host needs a forwarded port; the client needs nothing.

This doesn't work if the host's internet provider puts them behind carrier-grade NAT (common on mobile and some fibre/cable connections): then the router's own WAN address differs from the address websites show, and nothing can be forwarded. Use a VPN or a tunnel in that case. Another port works too: set `Port` under `[Network]` in `BepInEx\config\cwmp.cfg` on both PCs, or have the client type `address:port`. The connection is direct between the two PCs; the password option (both type the same one) keeps strangers who find the open port from joining.

**Address with a port:** the client's address box takes `host` or `host:port`, for tunnels such as playit.gg that give their own port.

**Linux / Proton:** untested. BepInEx loads through `winhttp.dll`, which Wine/Proton usually only uses with a DLL override: in Steam's launch options for Cold Waters, `WINEDLLOVERRIDES="winhttp=n,b" %command%`. Reports welcome.

**Session password (recommended with strangers):** type the same password on both sides; the host refuses and drops anyone without it, and also drops a connection that sends nothing valid within 5 s. It is a door lock, not encryption: game traffic is not encrypted, and the password is stored as plain text in `cwmp.cfg`, so use a throwaway one.

**Disconnect** (or **Stop hosting**) in the window ends the connection; during a duel it asks for a second click.

Host settings in the MULTIPLAYER window (Ctrl+F6): conditions (as the mission, random, or custom, including a fixed ambient noise level), starting range, pause, time compression, Reduced Visibility and neutral traffic.

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

- **Setup:** run the new setup's `Install.cmd`; it replaces the mod files.
- **JSGME:** disable ColdWatersMP in JSGME, delete `MODS\ColdWatersMP`, extract the new zip into `MODS` and enable it again. (The setup does the same for you: it sees JSGME and puts the mod in `MODS`.)
- **Copied by hand:** close the game and copy the new files over the old ones.

Your settings in `BepInEx\config\cwmp.cfg` are kept (the zip doesn't contain that file). Both players need the same version, or the join is refused with a message saying so.

## Uninstall

Setup: run `Uninstall.cmd`. JSGME: disable the mod (BepInEx leaves its own logs and settings in a `BepInEx` folder; delete it if you like). By hand: delete `winhttp.dll`, `doorstop_config.ini`, `.doorstop_version` and the `BepInEx` folder from the Cold Waters folder.

## Troubleshooting

- **No MULTIPLAYER button:** check that `winhttp.dll` and `BepInEx` sit next to `ColdWaters.exe`. Then open `BepInEx\LogOutput.log` and look for `Cold Waters MP ... loaded`.
- **Can't connect:** check the address, whether the host has started hosting, the Windows firewall (UDP 7777 on the host), and whether your VPN is connected on both PCs. Without a VPN: the port forward (UDP, not TCP, to the host's current local address) and whether the host is behind carrier-grade NAT (see *Direct, without a VPN*).
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
