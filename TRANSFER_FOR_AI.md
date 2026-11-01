# Transfer package — AGPT/APPT AutoCAD Plugin

**Date:** 2026-11-01  
**Owner GitHub:** rinatikd  
**Purpose:** hand off to another AI agent

## What this is
.NET 8 plugin for AutoCAD 2026: gas fire suppression (АГПТ) + powder (АППТ).
Commands: AGPT_ALBUM, AGPT_ZONE, AGPT_CALC, APPT_ALBUM, AGPT_FRAME, AGPT_UGO_*, …

## Build (Windows)
```
cd <repo>
dotnet build AGPT.csproj -c Release -p:Platform=x64 -p:AcadDir="C:\Program Files\Autodesk\AutoCAD 2026" --no-incremental
NETLOAD bin\Release\AGPT.dll
```

## Current status (2026-11-01)
- DLL builds and loads (`*** AGPT/APPT v1.0.0 LOADED OK ***`).
- AGPT_ZONE: Pline/Draw/Rect → area, height, volume (XData).
- Album generates ~12 sheets along X with pitch.
- **OPEN BUG:** content overlaps corner stamp on ALL sheets.
  - Local user set `DL.ContentBottom = 90.0` (was 68).
  - Not enough: many hardcoded `o.Y + 28..85` still invade stamp zone (~55–90 mm).
  - **Next fix priority (agreed with ChatGPT bridge):**
    1. Frame offset — move all content Y to `o.Y + ContentBottom + clearance` (no hardcoded y < ContentBottom).
    2. PlanSheet — room contours + UGO.
    3. Spec table — GOST 21.110 columns; Table.Columns[i].Width API.

## Key paths
| Path | Role |
|------|------|
| src/AGPT_Plugin_Drawings.cs | Album, frames, tables, PlanSheet |
| src/AGPT_Stage3_Blocks_Full.cs | AGPT_ZONE, modules, pipes |
| src/AGPT_Stage4_Hydro_Axo_v2.cs | Hydro / axo |
| src/APPT_Plugin_Drawings.cs | Powder album |
| src/PluginInit.cs | IExtensionApplication |
| bridge/ | Grok↔ChatGPT message queue |
| HANDOVER.md | Architecture notes |

## References (not all in git — ask user)
- Эталон: Детский сад АГПТ/АППТ 09.02.26, МГИМО 25/р-34-ПР-АГПТ
- Norms 2026: СП 484/485/486; algorithm C only
- UGO from R-CAD / Садик.dwg blocks

## Do not commit
bin/, obj/, *.dll, .env secrets, large DWG working copies unless needed.

## Immediate task for next agent
Fix stamp collision: replace hardcoded low Y with SheetArea.FloorY / ContentBottom+; retest AGPT_ALBUM on AutoCAD 2026.
