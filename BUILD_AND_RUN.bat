@echo off
chcp 65001 >nul
setlocal
cd /d "%~dp0"

echo ============================================================
echo  AGPT/APPT Plugin - build and run
echo ============================================================
echo.

set ACADDIR=
set ACAD_EXE=

if not "%~1"=="" (
  set "ACADDIR=%~1"
  goto :check_acad
)

if exist "C:\Program Files\Autodesk\AutoCAD 2026\AcCoreMgd.dll" (
  set "ACADDIR=C:\Program Files\Autodesk\AutoCAD 2026"
  set "ACAD_EXE=C:\Program Files\Autodesk\AutoCAD 2026\acad.exe"
  goto :check_acad
)
if exist "C:\Program Files\Autodesk\AutoCAD 2025\AcCoreMgd.dll" (
  set "ACADDIR=C:\Program Files\Autodesk\AutoCAD 2025"
  set "ACAD_EXE=C:\Program Files\Autodesk\AutoCAD 2025\acad.exe"
  goto :check_acad
)
if exist "C:\Program Files\Autodesk\AutoCAD 2024\AcCoreMgd.dll" (
  set "ACADDIR=C:\Program Files\Autodesk\AutoCAD 2024"
  set "ACAD_EXE=C:\Program Files\Autodesk\AutoCAD 2024\acad.exe"
  goto :check_acad
)

echo [ERROR] AutoCAD 2024/2025/2026 not found.
echo Usage: BUILD_AND_RUN.bat "C:\Program Files\Autodesk\AutoCAD 2026"
exit /b 1

:check_acad
if not exist "%ACADDIR%\AcCoreMgd.dll" (
  echo [ERROR] AcCoreMgd.dll not found in:
  echo   %ACADDIR%
  exit /b 1
)
if "%ACAD_EXE%"=="" set "ACAD_EXE=%ACADDIR%\acad.exe"
echo AutoCAD: %ACADDIR%
echo.

set "DLL=%~dp0bin\Release\AGPT.dll"
if exist "%DLL%" (
  echo Checking DLL lock...
  del "%DLL%" >nul 2>&1
  if exist "%DLL%" (
    echo.
    echo [WARNING] AGPT.dll is locked by AutoCAD.
    echo Close ALL AutoCAD windows, then press any key...
    pause >nul
    del "%DLL%" >nul 2>&1
    if exist "%DLL%" (
      echo [ERROR] Cannot delete DLL. Close AutoCAD manually.
      tasklist /FI "IMAGENAME eq acad.exe" 2>nul
      exit /b 1
    )
  )
  echo DLL is free.
  echo.
)

echo Building Release x64...
echo.

where dotnet >nul 2>&1
if errorlevel 1 (
  echo [ERROR] dotnet SDK not found. Install .NET 8 SDK.
  exit /b 1
)

dotnet build AGPT.csproj -c Release -p:Platform=x64 -p:AcadDir="%ACADDIR%" --no-incremental
if errorlevel 1 (
  echo.
  echo [ERROR] Build failed.
  exit /b 1
)

if not exist "%DLL%" (
  echo [ERROR] DLL not found after build:
  echo   %DLL%
  exit /b 1
)

echo.
echo [OK] Built: %DLL%
for %%A in ("%DLL%") do echo      Size: %%~zA bytes  Time: %%~tA
echo.

set "SCR=%TEMP%\AGPT_netload.scr"
echo _.NETLOAD> "%SCR%"
echo %DLL:\=/%>> "%SCR%"
echo _.ZOOM>> "%SCR%"
echo _E>> "%SCR%"

echo Script: %SCR%
echo.

if not exist "%ACAD_EXE%" (
  echo [WARN] acad.exe not found. Start AutoCAD manually:
  echo   NETLOAD
  echo   %DLL%
  echo   AGPT_ALBUM
  goto :done
)

echo Starting AutoCAD...
echo After load type:  AGPT_ALBUM
echo.
start "" "%ACAD_EXE%" /nologo /b "%SCR%"

:done
echo ============================================================
echo  Done.
echo  Commands: AGPT_ALBUM  APPT_ALBUM  AGPT_UGO_IMPORT_ALL
echo ============================================================
endlocal
exit /b 0
