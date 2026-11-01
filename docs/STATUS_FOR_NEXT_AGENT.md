# STATUS — передача AGPT 2026-10-30

## Источник истины

`AGPT_Plugin/src/` внутри архива `AGPT_Plugin_HANDOVER_20261030.zip`.

| Файл | ~строк | Правки |
|------|--------|--------|
| AGPT_Plugin_Drawings.cs | ~2960 | 2026-10-30 |
| AGPT_Stage3_Blocks_Full.cs | ~1020 | 2026-10-30 |
| PluginInit.cs | ~58 | 2026-10-30 |
| AGPT_Stage4_Hydro_Axo_v2.cs | ~1255 | 2026-10-28 |
| APPT_Plugin_Drawings.cs | ~1060 | 2026-10-28 |

## Последние изменения (29–30.10)

1. ContentBottom=72, Pitch=FrameW+80, без текста поверх штампа блока рамки
2. AGPT_ZONE: полилиния → S → H → имя → Vp (mm/m)
3. AGPT_ZONE_LIST; AGPT_CALC пишет MG/MP в XData
4. Collect только ZONE_NAME+VP; демо если зон нет

## Не закрыто

Оформление ≠ эталон МГИМО/Садик; УГО на планах; гидро vs augpt.online.

## Старт для агента

1. Прочитать `AGPT_Plugin/HANDOVER.md`
2. Распаковать в `c:\Work\AGPT_Plugin`
3. Сборка: см. HANDOVER §3
4. Приоритет: §8 HANDOVER
