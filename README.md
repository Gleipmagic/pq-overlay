# EQ Mob Overlay (prototype)

Standalone Windows overlay for the Project Quarm EQ client. It does **not** require Zeal.

## What it shows

- Current target name, level and Spawn ID
- Horizontal distance to the target
- Live target HP percentage
- Estimated remaining HP: `database max HP × live client HP%`
- Five resist badges using icons instead of MR/CR/DR/FR/PR text:
  - ✦ Magic
  - ❄ Cold
  - ☠ Disease
  - ♨ Fire
  - ☣ Poison
- Draggable panel constrained to the EverQuest client rectangle
- Saved panel position relative to the EQ client
- Hides when EverQuest is minimized or another application is active

## How it works

The overlay is a separate process. It reads the Quarm client's local target/self structures with `ReadProcessMemory` and never sends commands or writes to EQ memory. Target HP% and position update every 100 ms.

NPC max HP and resist values come from a local `quarm.db`. On first launch the app downloads the current public `data-latest` database release from the open-source PQ Companion project and stores it under:

`%LOCALAPPDATA%\\EQMobOverlay\\quarm.db`

No database web request is made when you change targets.

## Build

### Easiest: GitHub Actions

Upload this project to a GitHub repository and push it. The included workflow publishes a self-contained Windows x86 build and uploads `EQMobOverlay-win-x86` as an artifact.

### Visual Studio / command line

Install the .NET 8 SDK, then from the project folder:

```powershell
dotnet restore
dotnet publish -c Release -r win-x86 --self-contained true -p:PublishSingleFile=true
```

The executable will be under:

`bin\\Release\\net8.0-windows\\win-x86\\publish\\EQMobOverlay.exe`

## Use

1. Start Project Quarm / `eqgame.exe`.
2. Run `EQMobOverlay.exe` at the same Windows privilege level as EQ.
3. On first run, allow the NPC database download to finish.
4. Target a mob.
5. Drag the panel; it cannot be dragged outside the EQ client rectangle.

## Important prototype notes

- The client memory addresses/structure offsets are taken from the current Quarm client layout represented in the Zeal source used for this project. A future Quarm client patch could require updating them.
- Same-name NPCs are first matched by name and then preferentially by the target's zone-number-derived NPC ID range when available. Some scripted/instanced duplicate NPCs may still need stronger disambiguation later.
- Remaining HP is deliberately marked with `~` because it is an estimate based on database max HP and the client-reported HP percentage.
- Reading another process's memory may be covered by a game's server/client rules even when the program is read-only. Check Project Quarm's current rules before distributing or using it broadly.
