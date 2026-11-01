# Сборка плагина AGPT/APPT v1.0

## Требования

- Windows x64
- AutoCAD 2024 / 2025 / 2026
- **.NET 8 SDK** (рекомендуется)
  https://dotnet.microsoft.com/download/dotnet/8.0
  **или** Visual Studio 2022 / Build Tools (MSBuild)

## Способ 1 — build.bat (предпочтительно)

```bat
cd AGPT_Plugin
build.bat
```

Скрипт сам находит AutoCAD и выбирает MSBuild или `dotnet build`.

Указать путь к AutoCAD вручную:

```bat
build.bat "C:\Program Files\Autodesk\AutoCAD 2026"
```

Результат: `bin\Release\AGPT.dll`

## Способ 2 — только dotnet

```bat
cd AGPT_Plugin
dotnet build AGPT.csproj -c Release -p:Platform=x64 ^
  -p:AcadDir="C:\Program Files\Autodesk\AutoCAD 2026"
```

## Способ 3 — Visual Studio

1. Открыть `AGPT.csproj`
2. Configuration: **Release**, Platform: **x64**
3. При необходимости задать `AcadDir` в `.csproj`
4. Build → Build Solution

## Установка в AutoCAD

### Временно (на сессию)

```
NETLOAD
```
→ `bin\Release\AGPT.dll`

### Постоянно (bundle)

```bat
install_bundle.bat
```

Копирует `AGPT.bundle` в `%ProgramData%\Autodesk\ApplicationPlugins\`.
Перезапустите AutoCAD — плагин загрузится сам.

## Проверка

```
AGPT_ALBUM
APPT_ALBUM
```

## Если сборка не идёт

1. `dotnet --version` → 8.0.x
2. Новое окно cmd после установки SDK
3. Есть файл `...\AutoCAD 2026\AcCoreMgd.dll`
4. Платформа только **x64**
