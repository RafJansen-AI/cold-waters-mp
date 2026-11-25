@echo off
rem Cold Waters MP setup: installs DotMod v0.4c (from DotMod's GitHub, hash-checked) and Cold Waters MP.
rem The script is ColdWatersMP-Setup.ps1 next to this file; open it in Notepad to read what it does.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0ColdWatersMP-Setup.ps1" %*
