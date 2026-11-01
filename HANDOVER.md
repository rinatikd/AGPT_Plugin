# HANDOVER — AGPT/APPT

See TRANSFER_FOR_AI.md for current status (2026-11-01).

## Stack
- AutoCAD 2026 Managed API
- .NET 8 (net8.0-windows), x64
- Commands via CommandMethod

## Priority bug
Stamp overlap on all AGPT_ALBUM sheets. ContentBottom=90 insufficient; hard-coded o.Y+28..85 must be raised above stamp.

## Build
dotnet build AGPT.csproj -c Release -p:Platform=x64 -p:AcadDir="C:\Program Files\Autodesk\AutoCAD 2026"
