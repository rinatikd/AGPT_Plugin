@echo off
chcp 65001 >nul
setlocal EnableDelayedExpansion
cd /d "%~dp0"

set CONFIG=Release
set PLATFORM=x64
set ACADDIR=

if not "%~1"=="" (
  set "ACADDIR=%~1"
  goto have_acad
)

if exist "C:\Program Files\Autodesk\AutoCAD 2026\AcCoreMgd.dll" set "ACADDIR=C:\Program Files\Autodesk\AutoCAD 2026"
if not "%ACADDIR%"=="" goto have_acad
if exist "C:\Program Files\Autodesk\AutoCAD 2025\AcCoreMgd.dll" set "ACADDIR=C:\Program Files\Autodesk\AutoCAD 2025"
if not "%ACADDIR%"=="" goto have_acad
if exist "C:\Program Files\Autodesk\AutoCAD 2024\AcCoreMgd.dll" set "ACADDIR=C:\Program Files\Autodesk\AutoCAD 2024"
if not "%ACADDIR%"=="" goto have_acad

echo [ERROR] AutoCAD 2025/2026 not found.
echo Run: build.bat "C:\Program Files\Autodesk\AutoCAD 2026"
exit /b 1

:have_acad
echo AutoCAD: %ACADDIR%
if not exist "%ACADDIR%\AcCoreMgd.dll" (
  echo [ERROR] AcCoreMgd.dll not found in %ACADDIR%
  exit /b 1
)

set MSBUILD=
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if exist "%VSWHERE%" (
  for /f "usebackq tokens=*" %%i in (`"%VSWHERE%" -latest -prerelease -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe`) do set "MSBUILD=%%i"
)
if "%MSBUILD%"=="" if exist "%ProgramFiles%\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" set "MSBUILD=%ProgramFiles%\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"
if "%MSBUILD%"=="" if exist "%ProgramFiles%\Microsoft Visual Studio\2019\Community\MSBuild\Current\Bin\MSBuild.exe" set "MSBUILD=%ProgramFiles%\Microsoft Visual Studio\2019\Community\MSBuild\Current\Bin\MSBuild.exe"
if "%MSBUILD%"=="" (
  where msbuild >nul 2>&1
  if not errorlevel 1 set MSBUILD=msbuild
)

if not "%MSBUILD%"=="" (
  echo MSBuild: %MSBUILD%
  echo Building AGPT.dll ...
  "%MSBUILD%" AGPT.csproj /t:Restore,Build /p:Configuration=%CONFIG% /p:Platform=%PLATFORM% /p:AcadDir="%ACADDIR%" /v:m
  if errorlevel 1 (
    echo [ERROR] MSBuild failed.
    exit /b 1
  )
  goto ok
)

where dotnet >nul 2>&1
if errorlevel 1 (
  echo [ERROR] Neither MSBuild nor dotnet found.
  echo Install .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0
  exit /b 1
)

echo Using dotnet SDK
dotnet --version
dotnet restore AGPT.csproj
dotnet build AGPT.csproj -c %CONFIG% -p:Platform=%PLATFORM% -p:AcadDir="%ACADDIR%" -v:m
if errorlevel 1 (
  echo [ERROR] dotnet build failed.
  exit /b 1
)

:ok
echo.
if exist "bin\%CONFIG%\AGPT.dll" (
  echo OK: bin\%CONFIG%\AGPT.dll
  explorer "bin\%CONFIG%"
) else (
  echo [WARN] AGPT.dll not found
  dir /s /b AGPT.dll 2>nul
)
endlocal
