# Cold Waters MP

A 1v1 multiplayer submarine duel mod for Cold Waters. It is a BepInEx 5 (x86) plugin that runs on top of DotMod.

Status: proof of concept (plugin 0.0.5). Handshake, placement, sub choice and state sync work. Torpedo and damage sync are being tested.

This repository contains source only. It holds no game files, no DotMod files and no BepInEx binaries.

Requirements: Cold Waters 1.15g, [DotMod](https://github.com/DotModGroup/ColdWaters-DotMod) v0.4c, and BepInEx 5.4.x x86 with the preloader entrypoint set to UnityEngine.dll / MonoBehaviour / .cctor. Both players need the same Assembly-CSharp.dll.

Build (C# 5, .NET 3.5): see docs/ and the csc command in docs/duel-poc-plan.md.
