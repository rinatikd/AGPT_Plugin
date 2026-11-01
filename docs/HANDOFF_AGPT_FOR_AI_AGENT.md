# HANDOFF: AGPT / APPT AutoCAD Plugin — для продолжения другим ИИ-агентом

**Дата:** 2026-10-21  
**Язык проекта:** русский (нормы РФ/РК, интерфейс команд)  
**Цель:** автоматизация проектирования АГПТ (газовое) и АППТ (порошковое) в AutoCAD с выдачей полного альбома **как чертежей в модели** (не Word/Excel).

---

## 1. Миссия продукта

Плагин **AGPT** для AutoCAD:

1. Расстановка приборов: зоны, модули ГОТВ, насадки, автоматика (МПТ/УДП/табло), КСИД, СДУ/РВД, трубы.
2. Два вида тушения: **модульное** и **централизованное**.
3. Расчёты: масса ГОТВ, КСИД, гидравлика (ΔP, tpd, Fc), подбор Dy, NPSH/кавитация, гидроудар, время закрытия ЗПУ.
4. Графика: аксонометрия трубной разводки, структурная схема, кабельный журнал, спецификация, листы альбома.
5. Оформление по ГОСТ / эталону РИН-ОК-НН1-Р-ГАПТ / Gasbase; нормы **СП 484/485/486** (не СП 5); пуск только **алгоритм C**.

**Критическое правило оформления:** все общие данные, расчёты, спецификации, КЖ — **только чертежи в model space AutoCAD**.

---

## 2. Эталоны и источники

| Источник | Путь / URL | Роль |
|----------|------------|------|
| Главный эталон движка | https://augpt.online/ | объект→зоны, ИМПЕРАТОР/ЗАРЯ, гидравлика, DXF/DWG |
| Gasbase | gasbase.ru, book.gasbase.ru | состав установки, нормы, решения |
| Оформление | `ref_gapt/Эталон_оформления_ГАПТ.md`, `ref_gapt/05_Chertezhi.dwg` | штампы, ведомость, структура тома |
| Пример альбома | `Альбом_АГПТ_39-26-1_пример.docx/.pdf` | образец документации |
| Садик АГПТ/АППТ | PDF проекты детсада (в истории) | состав листов 2026 |
| BolidCAD / Rubezh / CAADS | bolid.ru, projects.rubezh.ru | блоки, UX |
| ТЗ | `TZ_Plugin_AUGP_AUPP_v1.docx` | требования |
| Архитектура | `Архитектура_плагина_АГПТ_AutoCAD_v1.docx` | структура |
| Блоки | `Структура_блоков_AutoCAD_АГПТ_v1.docx`, `AGPT_BlockLibrary_v1.json` | атрибуты, XData |
| Расчёт массы | `Расчёт_массы_ГОТВ_прототип.xlsx` | K4, КСИД, Vтр |
| Graphify-карта кода | `AGPT_Plugin/graphify-out/` | 309 nodes, 901 edges |

---

## 3. Структура репозитория (ключевое)

```
artifacts/
├── HANDOFF_AGPT_FOR_AI_AGENT.md   ← этот файл
├── AGENT_TOOLS.md                 ← Graphify + find-skills
├── AGPT_Plugin/                   ← ОСНОВНОЙ КОД ПЛАГИНА
│   ├── AGPT.csproj                ← net8.0-windows, AutoCAD 2026
│   ├── AGPT.runtimeconfig.json
│   ├── AGPT.deps.json
│   ├── build.bat / BUILD.md / README.md / VERIFY.md
│   ├── src/
│   │   ├── PluginInit.cs                 (~56 строк) — IExtensionApplication
│   │   ├── AGPT_Stage3_Blocks_Full.cs    (~796) — ZONE/MODULE/NOZZLE/AUTO/KSID/PIPE/CALC
│   │   ├── AGPT_Stage4_Hydro_Axo_v2.cs   (~1238) — HYDRO2, AXO2, граф сети, Dy
│   │   ├── AGPT_Plugin_Drawings.cs       (~2638) — ALBUM, листы, УГО, рамки
│   │   └── APPT_Plugin_Drawings.cs       (~1050) — порошковое (APPT_*)
│   ├── bundle/AGPT.bundle/PackageContents.xml  — ApplicationPlugins
│   └── graphify-out/graph.json
├── AGPT_BlockLibrary_v1.json
├── install_agent_tools.bat/.ps1/.sh
└── ref_gapt/, example_aupt/, …
```

Корневые копии `.cs` в `artifacts/` (Stage3/4, Drawings) — **устаревшие черновики**; актуальный код только в `AGPT_Plugin/src/`.

---

## 4. Стек и сборка

- **Target:** `net8.0-windows`, x64  
- **AutoCAD:** 2025/2026 (API Ac*Mgd ~25.x / R25–R26)  
- **Ссылки:** `AcCoreMgd`, `AcDbMgd`, `AcMgd` из `AcadDir` (по умолчанию `C:\Program Files\Autodesk\AutoCAD 2026`)  
- **Сборка (Windows):**

```bat
cd c:\Work\AGPT_Plugin
"C:\Program Files\Microsoft Visual Studio\18\Insiders\MSBuild\Current\Bin\MSBuild.exe" AGPT.csproj /t:Restore,Build /p:Configuration=Release /p:Platform=x64 /p:AcadDir="C:\Program Files\Autodesk\AutoCAD 2026"
```

Или `build.bat "C:\Program Files\Autodesk\AutoCAD 2026"`.

- **Выход:** `bin\Release\AGPT.dll` (+ `AGPT.runtimeconfig.json` рядом обязателен для .NET 8).

### Загрузка в AutoCAD 2026

1. `SECURELOAD` → `0`  
2. `TRUSTEDPATHS` ← папка с DLL  
3. `NETLOAD` → `AGPT.dll` **или** bundle в `%ProgramData%\Autodesk\ApplicationPlugins\AGPT.bundle\`  
4. Команды: `AGPT_ALBUM`, `AGPT_ZONE`, `AGPT_HYDRO2`, …

**Важно:** плагин **не** работает в AutoCAD 2021 (R24 / .NET Framework). Журнал с путём `...\AutoCAD 2021\R24.0\...` = неверная версия AutoCAD. Нужен `(getvar "ACADVER")` → 25.x / 26.x.

При блокировке файла при сборке: закрыть все `acad.exe` (DLL занята процессом).

---

## 5. Команды (зарегистрированные)

### Stage3 — расстановка / расчёт

| Команда | Назначение |
|---------|------------|
| `AGPT_ZONE` | зона тушения |
| `AGPT_MODULE` | модуль ГОТВ |
| `AGPT_NOZZLE` | насадок |
| `AGPT_AUTO` | автоматика (МПТ, УДП, табло) |
| `AGPT_KSID` | КСИД |
| `AGPT_ACC` | СДУ/РВД/аксессуары |
| `AGPT_PIPE` | участок трубы + XData |
| `AGPT_CALC` | расчёт массы / вывод |
| `AGPT_SPEC` | спецификация (базовая) |

### Stage4 — гидравлика / аксонометрия

| Команда | Назначение |
|---------|------------|
| `AGPT_HYDRO2` | гидравлика, граф, Dy, ΔP, tpd, NPSH, удар, ЗПУ |
| `AGPT_AXO2` | аксонометрия в модели |
| `AGPT_AXO_SHEET` | аксонометрия на лист+штамп |
| `AGPT_HYDRO_AXO_SHEET` | гидро + axo sheet |

### Drawings — альбом

| Команда | Назначение |
|---------|------------|
| `AGPT_ALBUM` | полный комплект листов в модели |
| `AGPT_SHEET_OD` | общие данные |
| `AGPT_SHEET_SS` | структурная схема |
| `AGPT_SHEET_AXO` | лист аксонометрии |
| `AGPT_SHEET_SPEC` | спецификация |
| `AGPT_SHEET_KZH` | кабельный журнал |
| `AGPT_SHEET_PLAN` | план |
| `AGPT_CALC_DRAW` | расчёт как чертёж |
| `AGPT_FRAME` / `AGPT_FRAME_LOAD` | рамка |
| `AGPT_UGO_*` | условные обозначения |

### APPT (порошок)

`APPT_ALBUM`, `APPT_CALC`, `APPT_ZONE`, `APPT_SHEET_OD/CALC/SPEC`

---

## 6. Модель данных (ядро)

Из graphify god-nodes и кода:

- **`ProjectData`** — агрегат проекта  
- **`ZoneData`** — зона (объём, ГОТВ, коэффициенты)  
- **`ModuleData`**, **`NozzleData`**, **`PipeSegData`**, **`AutoDeviceData`**, **`KsidData`**, **`AccData`**  
- **`MassCalc`**, **`KsidCalc`** — расчёты  
- **`NetworkGraph`**, **`Edge`**, **`Hydro`** — гидравлика Stage4  
- **`SheetGen`**, **`Draw`**, **`AgptDrawingCommands`** — генерация листов  
- Данные на примитивах: **XData** + слои (`Layers` / `Ly.Ensure`)

Порядок реализации (исторический Этап 3): ZONE → MODULE → NOZZLE → AUTO → KSID/ACC → PIPE+XData → CALC → HYDRO/AXO → ALBUM.

---

## 7. Что СДЕЛАНО

- [x] ТЗ, архитектура, JSON-библиотека блоков  
- [x] Прототип расчёта массы ГОТВ + КСИД (Excel)  
- [x] C# Stage3: блоки/команды расстановки и базовый расчёт  
- [x] C# Stage4 v2: граф сети, подбор Dy, гидропроверки, axo  
- [x] C# Drawings: AGPT_ALBUM и листы в модели; APPT-часть  
- [x] PackageContents / ApplicationPlugins bundle (R25–R26 → Windows/2026)  
- [x] runtimeconfig.json для NETLOAD .NET 8  
- [x] Сборка Release на машине пользователя (0 ошибок CS после фиксов GetCorner / Database)  
- [x] Graphify-карта кода; скрипты install Graphify + find-skills  

---

## 8. Что НЕ доведено / риски

1. **Стабильная загрузка на ПК пользователя** — часто открывали AutoCAD **2021** вместо 2026; NETLOAD «молчал», команды «неизвестны». Нужна проверка на чистом AutoCAD 2026 + ApplicationPlugins.  
2. **Качество альбома vs эталон ГОСТ** — генерация есть; визуальное соответствие РИН-ОК / Gasbase (штампы, УГО, ведомость) требует доводки.  
3. **Централизованное vs модульное** — ветвление логики не полностью закрыто продуктово.  
4. **Гидравлика** — инженерные формулы прототипного уровня; сверка с augpt.online / СП 485 обязательна перед продом.  
5. **УГО/блоки** — DXF/DWG эталоны (`AGPT_UGO.dxf`, `Садик_UGO_source.dwg`) подключены командами; полнота библиотеки.  
6. **AutoCAD 2021** — отдельная ветка `net48` **не** сделана (если нужна — отдельный TargetFramework + DLL 2021).  
7. Корневые `AGPT_Stage*.cs` в artifacts — не путать с `AGPT_Plugin/src/`.

---

## 9. Приоритетный backlog для агента

1. **Verify load path на AutoCAD 2026:** ApplicationPlugins + баннер `PluginInit` + `AGPT_ALBUM` на пустом DWG.  
2. **Сверить `AGPT_ALBUM` с эталоном** `ref_gapt` + альбом 39-26-1: состав листов, штамп, слои.  
3. **Усилить аксонометрию** по результатам `AGPT_HYDRO2` (подписи Dy, ΔP, насадки).  
4. **Связать XData** расстановки с `SheetGen` (реальные зоны/модули в OD/SPEC/KZH, не заглушки).  
5. **Регрессия команд** по `VERIFY.md`.  
6. Опционально: dual-target 2021+2026, если заказчик на R24.

---

## 10. Конвенции кода

- Namespaces: `AGPT`, `AGPT.Stage3`, `AGPT.Stage4v2`, `AGPT.Drawings`, `APPT.Drawings`  
- `[assembly: CommandClass(typeof(...))]` на каждый класс с `[CommandMethod]`  
- `[assembly: ExtensionApplication(typeof(AGPT.PluginInit))]`  
- Интерактив: `PromptCornerOptions` + `ed.GetCorner(co)` (не старый GetCorner string/Point3d)  
- Слои: хелперы `Ly.Ensure(db, tr, name, color)` — **Database**, не `Transaction.Document`  
- Документация пользователю — на русском  

---

## 11. Инструменты агента

```bash
# карта кода
export PATH="$HOME/.local/bin:$PATH"
graphify extract AGPT_Plugin --code-only
graphify explain "ZoneData" --graph AGPT_Plugin/graphify-out/graph.json

# поиск skills
npx skills find <query>
```

Скрипты: `install_agent_tools.bat` / `.ps1` / `.sh`, описание `AGENT_TOOLS.md`.

---

## 12. Минимальный промпт для стартующего агента

Скопировать агенту:

```
Продолжи разработку плагина AGPT (газовое/порошковое пожаротушение) для AutoCAD 2026 (.NET 8).

Читай обязательно:
- artifacts/HANDOFF_AGPT_FOR_AI_AGENT.md
- artifacts/AGPT_Plugin/README.md, BUILD.md, VERIFY.md
- artifacts/AGPT_Plugin/src/*.cs (актуальный код)
- artifacts/ref_gapt/Эталон_оформления_ГАПТ.md

Правила:
- Документация только как чертежи в model space
- Нормы СП 484/485/486, пуск алгоритм C
- Не путать AutoCAD 2021 и 2026
- Актуальный код только в AGPT_Plugin/src/

Приоритет: (1) стабильный NETLOAD/bundle на 2026, (2) AGPT_ALBUM ≈ эталон, (3) данные XData → листы.
```

---

## 13. Контакты контекста пользователя (факт сессии)

- Рабочая папка на ПК: `c:\Work\AGPT_Plugin`  
- AutoCAD 2026: `C:\Program Files\Autodesk\AutoCAD 2026`  
- VS: Visual Studio 18 Insiders, MSBuild `...\18\Insiders\MSBuild\Current\Bin\MSBuild.exe`  
- Ранее ошибочно тестировали в AutoCAD 2021 (лог `R24.0`)  

---

*Конец handoff. Обновляйте этот файл при смене архитектуры, команд или статуса backlog.*
