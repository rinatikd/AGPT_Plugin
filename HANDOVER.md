# HANDOVER — AGPT/APPT AutoCAD Plugin

**Дата передачи:** 2026-10-30  
**Версия кода:** v1.0.x (источник истины — эта папка `AGPT_Plugin/`)  
**Для кого:** следующий ИИ-агент / разработчик на машине с AutoCAD 2026  

---

## 1. Что это за проект

Плагин **AutoCAD .NET 8 (x64)** для выпуска альбома рабочей документации:

| Система | Команды альбома | Суть |
|---------|-----------------|------|
| **АГПТ** (газовое модульное ПТ) | `AGPT_ALBUM` | 12 листов в model space по оси X |
| **АППТ** (порошок) | `APPT_ALBUM` | Отдельный комплект |

**Принцип:** все ОД, расчёты, схемы, спецификации, КЖ — **чертежи в модели**, не Word/Excel.

**Эталоны оформления (обязательно ориентироваться):**

- МГИМО Астана `25/р-34-ПР-АГПТ` (блоки 1/3/4)
- Детский сад АГПТ/АППТ 09.02.26 (Астана)
- Нормы: **СП 484 / 485 / 486** (не СП 5), пуск **алгоритм C**, спецификация **ГОСТ 21.110**

Главный онлайн-эталон гидравлики/зон: https://augpt.online/

---

## 2. Структура репозитория

```
AGPT_Plugin/
  AGPT.csproj              # net8.0-windows, x64, ссылки на AutoCAD
  BUILD_AND_RUN.bat / build.bat
  README.md, BUILD.md, VERIFY.md
  HANDOVER.md              # этот файл
  .env.example             # только AcadDir (секретов нет)
  src/
    PluginInit.cs                 # IExtensionApplication, баннер NETLOAD
    AGPT_Plugin_Drawings.cs       # альбом АГПТ, Collect, Table, рамка, PlanSheet
    APPT_Plugin_Drawings.cs       # альбом АППТ
    AGPT_Stage3_Blocks_Full.cs    # AGPT_ZONE (полилиния→S→H→Vp), MODULE, PIPE, CALC
    AGPT_Stage4_Hydro_Axo_v2.cs   # AGPT_HYDRO2, AGPT_AXO2
  blocks/
    AGPT_Frame_A3.dwg / .dxf      # рамка A3
    ММХ_Штамп.dwg / MMX_*.dwg     # штамп из эталона
    Садик_UGO_source.dwg          # УГО R-CAD (~4 MB)
    AGPT_UGO.dxf
  bundle/                         # автозагрузка (опционально)
```

Доп. материалы рядом в `artifacts/` (вне ZIP плагина, но полезны):  
ТЗ, архитектура, JSON блоков, пример альбома PDF/DOCX, `ref_gapt/`, прототип Excel массы ГОТВ.

---

## 3. Стек

| Компонент | Требование |
|-----------|------------|
| C# / TFM | **net8.0-windows**, Platform **x64** |
| AutoCAD | **2026** (или 2025) — `AcCoreMgd`, `AcDbMgd`, `AcMgd` |
| SDK | .NET 8 SDK (`dotnet build`) |
| ОС | Windows only |
| Секреты / БД / облако | **нет** |

Сборка (AutoCAD **должен быть закрыт**, иначе DLL залочена):

```bat
cd c:\Work\AGPT_Plugin
dotnet build AGPT.csproj -c Release -p:Platform=x64 -p:AcadDir="C:\Program Files\Autodesk\AutoCAD 2026" --no-incremental
```

Результат: `bin\Release\AGPT.dll` → в AutoCAD: `NETLOAD`.

---

## 4. Команды (актуальные)

### АГПТ

| Команда | Назначение |
|---------|------------|
| **AGPT_ZONE** / **AGPT_ZONE_PLINE** | Контур помещения (Pline или Rect) → площадь S → высота H → имя → **Vp=S×H**, XData |
| **AGPT_ZONE_LIST** | Список зон в модели |
| **AGPT_CALC** | Mg, модуль, КСИД; запись MP/MG в XData |
| **AGPT_ALBUM** | 12 листов альбома; зоны из модели или ручной ввод L×W×H |
| AGPT_MODULE, AGPT_PIPE, AGPT_NOZZLE, AGPT_AUTO… | Расстановка оборудования |
| AGPT_HYDRO2, AGPT_AXO2 | Гидравлика / аксо |
| AGPT_FRAME, AGPT_FRAME_LOAD | Рамка A3+штамп |
| AGPT_UGO_LIST / IMPORT | УГО из Садик.dwg / R-CAD |
| AGPT_SHEET_* | Отдельные листы |

### АППТ

`APPT_ALBUM`, `APPT_ZONE`, `APPT_CALC`, `APPT_SHEET_OD/CALC/SPEC`

---

## 5. Типовой сценарий пользователя

```
1. Открыть план (единицы чаще мм)
2. Нарисовать замкнутую LWPOLYLINE по помещению
3. AGPT_ZONE → Pline → mm → H (м) → имя → FH
4. Повторить для всех зон
5. AGPT_ZONE_LIST / AGPT_CALC
6. AGPT_ALBUM → шифр, объект → ZOOM E
```

`Collect.FromModel` читает только сущности с XData `ZONE_NAME` + `VP>0`.  
Если зон нет — подставляются **демо** (детсад); флаг `Collect.LastUsedDemoZones`.

---

## 6. Геометрия листа (важно)

- `DL.ContentBottom = 72` — контент **не** должен заходить в штамп  
- `SheetGen.Pitch = FrameWidth + 80` после `SyncPitchFromFrame`  
- При вставке **блока** рамки **не** рисовать текст поверх штампа (исправлено 2026-10-29)  
- Таблицы — `Draw.Table` (AcDbTable + fallback линиями)  
- Спецификация обрезает строки, чтобы не уходить ниже ContentBottom  

---

## 7. Что уже работает / что нет

### Относительно надёжно

- Сборка Release x64 под AutoCAD 2026  
- NETLOAD, баннер команд  
- AGPT_ZONE по полилинии → S, H, Vp, XData  
- AGPT_CALC → Mg в командную строку + XData  
- AGPT_ALBUM строит 12 «листов» в модели  
- Опрос L×W×H если зон нет  

### Прототип / не доведено до эталона

- Визуальное совпадение с МГИМО/«Садик» (штамп ГОСТ, УГО на планах, аксо Ду/L)  
- Рамка из внешнего DWG иногда сбоит (scale/base point) → есть программный fallback  
- Таблицы и планы всё ещё упрощены  
- Гидро vs augpt.online не валидирована численно  
- АППТ отстаёт от АГПТ по глубине  

### Известные грабли пользователя

1. **DLL залочена AutoCAD** — закрыть acad перед `dotnet build`  
2. **Два AutoCAD** (2021 vs 2026) — грузить DLL только в 2026  
3. План в **мм**: `AGPT_ZONE` → единицы **mm** (S = Area×1e-6)  
4. `build.bat` ломался из‑за Unix-синтаксиса — предпочитать `dotnet build`  

---

## 8. Приоритет для следующего агента

1. **Оформление = эталон**  
   - Рамка+штамп без наложений на всех 12 листах  
   - ОД: 2 колонки, ведомости как МГИМО  
   - Расчёт: таблицы по зонам без наложений  
   - План: контуры из AGPT_ZONE + УГО R-CAD  
   - Спецификация: графы ГОСТ 21.110  

2. **Данные**  
   - Альбом только из реальных зон модели (не демо, если есть ZONE)  
   - Связка ZONE → MODULE → PIPE → HYDRO2 → лист аксо  

3. **АППТ**  
   - Паритет команд и оформления с АГПТ  

4. **Не делать**  
   - Не уводить документацию в Word/Excel  
   - Не менять target на net48 без запроса (пользователь на 2026/.NET 8)  

---

## 9. Smoke-тест после передачи

| # | Действие | Ожидание |
|---|----------|----------|
| 1 | `dotnet build …` | `bin\Release\AGPT.dll` |
| 2 | AutoCAD 2026 → NETLOAD | `*** AGPT/APPT v1.0.0 LOADED OK ***` |
| 3 | Замкнутая pline → AGPT_ZONE | S, Vp в командной строке, XData |
| 4 | AGPT_ZONE_LIST | Список зон |
| 5 | AGPT_CALC | Mg, MODULE_VOL |
| 6 | AGPT_ALBUM | 12 блоков по X, Pitch≈500, ZOOM E |
| 7 | Визуально | Нет текста поверх штампа, листы не в одной точке |

---

## 10. Секреты и очистка

- Секретов, `.env` с ключами, JWT, сертификатов **нет**  
- `.env.example` — только `AcadDir`  
- В архив передачи **не** включать: `bin/`, `obj/`, `graphify-out/cache/`, `*.log`, `node_modules`  

---

## 11. Файлы-якоря для агента

| Тема | Где смотреть |
|------|----------------|
| Альбом / Table / ContentBottom | `src/AGPT_Plugin_Drawings.cs` → `SheetGen`, `Draw`, `DL` |
| Зона с полилинии | `src/AGPT_Stage3_Blocks_Full.cs` → `Zone()` |
| Сбор зон | `Collect.FromModel` в Drawings.cs |
| Баннер команд | `PluginInit.cs` |
| Эталон текст | `artifacts/ref_gapt/`, PDF МГИМО / детсад у пользователя |
| ТЗ | `TZ_Plugin_AUGP_AUPP_v1.docx` / `docs_md/` |

---

## 12. Контакты контекста проекта

- Пользователь работает в `C:\Work\AGPT_Plugin`  
- AutoCAD: `C:\Program Files\Autodesk\AutoCAD 2026`  
- Язык общения: **русский**  
- Цель: пользовательская версия альбома «как в эталоне», не демо-скетч  

**Конец HANDOVER 2026-10-30**
