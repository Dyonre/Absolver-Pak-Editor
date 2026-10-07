# Absolver Pak Editor

Windows tool for inspecting, editing, and rebuilding Absolver mod paks.

## What it does

- Extract the installed base game for Data Editor access.
- Inspect any mod pak before loading it.
- Open a mod pak as a local editable copy; the original pak is never changed.
- Edit DataTable and asset properties, preview moves and animations, then build a new override pak.
- Package a finished mod with its recipe and install instructions.

## Requirements

- A legal Steam copy of Absolver.
- Unreal Engine 4.18's `UnrealPak.exe` configured in Settings (required for extraction and packing).
- The self-contained release needs nothing else. The small release needs the .NET 8 Desktop Runtime.

## Quick start

1. Set the game Paks folder and `UnrealPak.exe` path in Settings.
2. Click **Extract Game Files...** and choose an empty folder.
3. Edit an asset, then click **Finalize...** to create a new pak.

To modify an existing mod: click **Load Mod Pak...**, inspect it, then choose **Open for editing**. The editor extracts it under `work/imports/`; Finalize creates a separate override pak.

## Licenses and credits

This repository is MIT licensed. The 3D viewer derives from the MIT-licensed Sifu Custom Moveset Maker by AllThatRain; its full license is at [viewer/LICENSE-SifuMovesetMaker.txt](viewer/LICENSE-SifuMovesetMaker.txt). See [tools/release/THIRD-PARTY-NOTICES.txt](tools/release/THIRD-PARTY-NOTICES.txt) for other dependencies.

This project includes no game assets. Absolver and Sifu are property of their respective owners.
