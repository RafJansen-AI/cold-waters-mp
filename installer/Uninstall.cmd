@echo off
rem Removes exactly what Install.cmd added and restores the original game DLL.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0ColdWatersMP-Setup.ps1" -Uninstall %*
