# Making a release

Built on a PC with the game (the plugin compiles against the game's own DLLs; see `src/ColdWatersMP/build.cmd`).

1. Set the version in `src/ColdWatersMP/Plugin.cs` and build `ColdWatersMP.dll` from the tagged commit.
2. **Mod zip** `ColdWatersMP-<version>.zip`, extracted into the game folder:
   - the official BepInEx 5.4.23.2 x86 files, unchanged (`winhttp.dll`, `doorstop_config.ini`, `.doorstop_version`, `BepInEx/core/*`; not BepInEx's `changelog.txt`, which would overwrite the game's own)
   - everything in `package/` (BepInEx.cfg preset, BepInEx licence, mod licence, README.txt)
   - `BepInEx/plugins/ColdWatersMP.dll`
   - never `BepInEx/config/cwmp.cfg`, so updates keep players' settings
3. **Setup zip** `ColdWatersMP-Setup-<version>.zip`:
   - root: everything in `installer/` plus `tools/check-dotmod.ps1`
   - `mod/`: the same files as the mod zip
4. Check both by extracting them: file count, DLL SHA-256, BepInEx files identical to the official zip. Test the setup once on a clean game copy.
5. Release notes list the SHA-256 of both zips and of `ColdWatersMP.dll`. Never upload game, DotMod or decompiled files.
