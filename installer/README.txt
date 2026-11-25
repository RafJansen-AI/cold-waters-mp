Cold Waters MP - setup (DotMod + Cold Waters MP in one go)

1. Extract this whole zip anywhere (for example your Downloads folder).
2. Double-click Install.cmd. It finds your Cold Waters folder (or asks), then:
   - on a clean Cold Waters 1.15g: downloads DotMod v0.4c from the DotMod team's GitHub release
     (about 740 MB), checks its SHA-256, backs up the one game file DotMod replaces and copies DotMod in;
   - if DotMod v0.4c is already installed: skips that step;
   - anything else (other mods, other versions): stops without changing anything;
   - then copies BepInEx and Cold Waters MP into the game folder.
3. Start Cold Waters. The main menu has a MULTIPLAYER button.

Uninstall.cmd removes exactly what Install.cmd added (listed in ColdWatersMP-installed.txt in the game folder)
and restores the original game file.

Windows may warn that the file comes from the internet: choose "More info" > "Run anyway" only if you
downloaded it from https://github.com/RafJansen-AI/cold-waters-mp/releases .
DotMod: https://github.com/DotModGroup/ColdWaters-DotMod (made by the DotMod team, not by us).
