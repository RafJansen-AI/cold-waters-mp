@echo off
rem Build ColdWatersMP.dll with the .NET Framework compiler (C# 5). Run from this folder.
rem GAME = a Cold Waters copy with BepInEx 5 (x86) installed.
if "%GAME%"=="" set GAME=C:\Users\rafja\Games\ColdWaters-MP
set M=%GAME%\ColdWaters_Data\Managed
set B=%GAME%\BepInEx\core
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /noconfig /nostdlib+ /target:library /out:ColdWatersMP.dll ^
 /r:"%M%\mscorlib.dll" /r:"%M%\System.dll" /r:"%M%\System.Core.dll" /r:"%M%\UnityEngine.dll" /r:"%M%\UnityEngine.UI.dll" /r:"%M%\Assembly-CSharp.dll" ^
 /r:"%B%\BepInEx.dll" /r:"%B%\0Harmony.dll" ^
 Plugin.cs Session.cs Transport.cs Duel.cs Weapons.cs Environment.cs Match.cs Sync.cs MenuTab.cs Ghosts.cs Ambient.cs DataCheck.cs HudFix.cs Tracks.cs Ships.cs Rounds.cs HitFx.cs Missiles.cs Dipping.cs Helos.cs VesselPicker.cs
