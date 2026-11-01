# Transfer package — AGPT/APPT AutoCAD Plugin

**Date:** 2026-11-01
**Owner GitHub:** rinatikd
**Purpose:** hand off to another AI agent

## What this is
.NET 8 plugin for AutoCAD 2026: gas fire suppression (AGPT) + powder (APPT).
Commands: AGPT_ALBUM, AGPT_ZONE, AGPT_CALC, APPT_ALBUM, AGPT_FRAME, AGPT_UGO_*, ...

## Build (Windows)
```
cd <repo>
dotnet build AGPT.csproj -c Release -p:Platform=x64 -p:AcadDir="C:\Program Files\Autodesk\AutoCAD 2026" --no-incremental
NETLOAD bin\Release\AGPT.dll
```

## Current status (2026-11-01)
- DLL builds and loads (*** AGPT/APPT v1.0.0 LOADED OK ***).
- AGPT_ZONE: Pline/Draw/Rect → area, height, volume (XData).
- Album generates ~12 sheets along X with pitch.
- **OPEN BUG:** content overlaps corner stamp on ALL sheets.
  - ContentBottom raised to 90.0 (was 68); still not enough.
  - Many hardcoded o.Y + 28..85 invade stamp zone.
  - **Next priority:** 1) Frame offset (all Y >= ContentBottom) 2) PlanSheet 3) Spec GOST 21.110

## Key paths
- src/AGPT_Plugin_Drawings.cs — album, frames, tables
- src/AGPT_Stage3_Blocks_Full.cs — AGPT_ZONE
- src/AGPT_Stage4_Hydro_Axo_v2.cs — hydro/axo
- src/APPT_Plugin_Drawings.cs — powder
- src/PluginInit.cs — entry
- bridge/ — Grok↔ChatGPT queue

## Immediate task
Fix stamp collision: no content Y below ContentBottom+clearance; retest AGPT_ALBUM.
