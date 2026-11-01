# Build AGPT

Requires AutoCAD 2026 + .NET 8 SDK.

```bat
dotnet build AGPT.csproj -c Release -p:Platform=x64 -p:AcadDir="C:\Program Files\Autodesk\AutoCAD 2026" --no-incremental
```

Output: bin\Release\AGPT.dll
NETLOAD in AutoCAD 2026.
