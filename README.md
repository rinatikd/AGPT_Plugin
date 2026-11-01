# AGPT/APPT — AutoCAD 2026 Plugin

**.NET 8** plugin: gas (AGPT) + powder (APPT) fire-suppression design album.

**Repo:** https://github.com/rinatikd/AGPT_Plugin  
**Owner:** rinatikd

## For the next AI agent

1. Read **[TRANSFER_FOR_AI.md](./TRANSFER_FOR_AI.md)**.
2. Full sources: handoff zip `AGPT_Plugin_GitHub_Handoff_20261101.zip` (SHA256 prefix `ed779aa2c8cef15f`) from Grok project artifacts, or `c:\\Work\\AGPT_Plugin\\` on owner PC.
3. Meta already in repo: TRANSFER_FOR_AI.md, AGPT.csproj, PluginInit.cs, HANDOVER.md, BUILD*.

### Build
```bat
dotnet build AGPT.csproj -c Release -p:Platform=x64 -p:AcadDir="C:\\Program Files\\Autodesk\\AutoCAD 2026" --no-incremental
NETLOAD bin\\Release\\AGPT.dll
```

### Commands
AGPT_ALBUM, AGPT_ZONE, AGPT_CALC, APPT_ALBUM, AGPT_FRAME, AGPT_UGO_LIST

### Open bug (priority 1)
Stamp overlap on all AGPT_ALBUM sheets. ContentBottom=90 insufficient; hard-coded o.Y+28..85 invade stamp. Raise all content Y above stamp zone, then PlanSheet+UGO, then Spec GOST 21.110.
