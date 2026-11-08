#!/usr/bin/env python3
"""
plan_geometry.py — выгружает ТОЧНУЮ геометрию листов-планов эталона (трубопровод, насадки, модули, выноски,
размеры, тексты, таблицы) в миллиметрах БУМАГИ, а также определения вставленных блоков (Геркон, 360 NOZZLE…).

Листом считается вставка блока рамки (как в dump_sheets.py). Для каждого листа координаты пересчитаны
в мм бумаги относительно левого нижнего угла листа: (x − x0) / масштаб.

Выбираются листы, на которых есть содержательная геометрия слоёв АГПТ/АСПТ (по умолчанию ≥ 8 примитивов
LINE/LWPOLYLINE/CIRCLE/ARC/INSERT/MULTILEADER). Листы «Общие данные» (только тексты и таблицы) пропускаются.

Использование (из корня репозитория):
    python tools/plan_geometry.py (Get-ChildItem *.dxf).FullName -o plan_dump
Результат: plan_dump/<файл>__sheet<N>.plan.json (по листу) и plan_dump/index.json (сводка).
"""
import sys, os, json, argparse
from collections import defaultdict, Counter

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from extract_dxf_spec import records, open_dxf, fnum, first  # noqa: E402
from dump_sheets import BB, block_points, is_frame_name, text_of  # noqa: E402

AGPT_LAYERS = ("АГПТ", "АСПТ")
GEOM_TYPES = ("LINE", "LWPOLYLINE", "CIRCLE", "ARC", "INSERT", "MULTILEADER")


def r2(v):
    return None if v is None else round(v, 2)


def short_tag(path):
    n = os.path.basename(path)
    for key, tag in (("первая", "1A"), ("вторая", "2"), ("третья", "3"), ("четверт", "4"), ("пятая", "5A")):
        if key in n:
            return tag
    return os.path.splitext(n)[0][:20]


def pt(d, cx, cy):
    x, y = fnum(d, cx), fnum(d, cy)
    return None if x is None or y is None else [x, y]


def convert(typ, d, lay):
    """Сырой примитив -> словарь в координатах МОДЕЛИ. Ключи 'P*' — точки, 'S*' — длины (масштабируются позже)."""
    o = {"t": typ, "l": lay}
    for code, key in ((62, "c"), (370, "lw"), (6, "lt")):
        v = first(d, code)
        if v is not None:
            o[key] = v.strip()
    if typ == "LINE":
        o["Pp"] = [pt(d, 10, 20), pt(d, 11, 21)]
    elif typ == "LWPOLYLINE":
        o["closed"] = (int(first(d, 70, 0)) & 1) == 1
        o["Pv"] = [[float(x), float(y)] for x, y in zip(d.get(10, []), d.get(20, []))]
        w = fnum(d, 43)
        if w:
            o["Sw"] = w
        if d.get(42):
            o["bulge"] = [float(b) for b in d.get(42)]
    elif typ == "CIRCLE":
        o["Pc"] = pt(d, 10, 20)
        o["Sr"] = fnum(d, 40)
    elif typ == "ARC":
        o["Pc"] = pt(d, 10, 20)
        o["Sr"] = fnum(d, 40)
        o["a"] = [fnum(d, 50), fnum(d, 51)]
    elif typ == "INSERT":
        o.update({"n": first(d, 2), "Pp": pt(d, 10, 20), "sc": [fnum(d, 41, 1.0), fnum(d, 42, 1.0), fnum(d, 43, 1.0)],
                  "rot": fnum(d, 50, 0.0)})
    elif typ in ("TEXT", "MTEXT", "ATTRIB", "ATTDEF"):
        o.update({"s": first(d, 7), "Sh": fnum(d, 40), "wf": fnum(d, 41) if typ != "MTEXT" else None,
                  "Sw": fnum(d, 41) if typ == "MTEXT" else None, "rot": fnum(d, 50), "att": first(d, 71),
                  "Pp": pt(d, 10, 20), "txt": text_of(d)[:3000]})
        if d.get(11):
            o["dir"] = [fnum(d, 11), fnum(d, 21)]
        if typ in ("ATTRIB", "ATTDEF"):
            o["tag"] = first(d, 2)
    elif typ == "DIMENSION":
        o.update({"dimstyle": first(d, 3), "text": first(d, 1), "meas": fnum(d, 42), "rot": fnum(d, 50),
                  "P10": pt(d, 10, 20), "P13": pt(d, 13, 23), "P14": pt(d, 14, 24)})
    elif typ == "MULTILEADER":
        o["txt"] = "".join(d.get(304, []))[:1500]
        o["Ppts"] = [[float(x), float(y)] for x, y in zip(d.get(10, []), d.get(20, []))]
        o["raw"] = {str(k): v[:40] for k, v in d.items() if k not in (8, 304)}
    elif typ == "HATCH":
        o.update({"pattern": first(d, 2), "solid": first(d, 70)})
    elif typ == "ACAD_TABLE":
        o.update({"Pp": pt(d, 10, 20), "rows": first(d, 91), "cols": first(d, 92),
                  "Srow_h": [float(v) for v in d.get(141, [])], "Scol_w": [float(v) for v in d.get(142, [])],
                  "cells": (d.get(302, []) + d.get(1, []))[:600]})
    else:
        return None
    return o


def to_paper(o, sheet):
    x0, y0, _, _ = sheet["rect"]
    k = abs(sheet["scale"]) or 1.0

    def p(v):
        return None if v is None else [r2((v[0] - x0) / k), r2((v[1] - y0) / k)]

    out = {}
    for key, val in o.items():
        if key in ("Srow_h", "Scol_w"):
            out[key[1:]] = [r2(v / k) for v in val]
        elif key.startswith("P"):
            nk = key[1:]
            if not val:
                out[nk] = None
            elif isinstance(val[0], list) or (len(val) > 1 and val[0] is None and isinstance(val[1], list)):
                out[nk] = [p(v) if v else None for v in val]
            else:
                out[nk] = p(val)
        elif key.startswith("S"):
            out[key[1:]] = None if val is None else r2(val / k)
        else:
            out[key] = val
    return out


def anchor(o):
    """Первая точка примитива — по ней он относится к листу."""
    for key in ("Pp", "Pv", "Pc", "Ppts", "P10"):
        v = o.get(key)
        if not v:
            continue
        if isinstance(v[0], list):
            v = v[0]
        if v and v[0] is not None:
            return v
    return None


def local(o):
    """Для определений блоков: координаты остаются локальными, только убираем служебные префиксы P/S."""
    out = {}
    for key, val in o.items():
        if key[0] in "PS" and len(key) > 1 and key[1:2].islower() or key in ("P10", "P13", "P14"):
            out[key[1:]] = val
        else:
            out[key] = val
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("files", nargs="+")
    ap.add_argument("-o", "--out", default="plan_dump")
    ap.add_argument("--min-geom", type=int, default=8, help="минимум геометрических примитивов АГПТ/АСПТ на листе")
    ap.add_argument("--max-items", type=int, default=6000)
    ap.add_argument("--block-limit", type=int, default=600, help="максимум примитивов в определении блока")
    a = ap.parse_args()
    os.makedirs(a.out, exist_ok=True)
    index = []

    for path in a.files:
        tag = short_tag(path)
        print("→", path)
        block_bb, cur = {}, None
        frames, items = [], []
        with open_dxf(path) as f:
            for sec, typ, d in records(f):
                if sec == "BLOCKS":
                    if typ == "BLOCK":
                        nm = first(d, 2)
                        cur = nm if is_frame_name(nm) else None
                        if cur:
                            block_bb[cur] = BB()
                    elif typ == "ENDBLK":
                        cur = None
                    elif cur:
                        block_points(typ, d, block_bb[cur])
                elif sec == "ENTITIES":
                    lay = first(d, 8, "0")
                    if typ == "INSERT":
                        nm = first(d, 2)
                        if nm in block_bb and block_bb[nm].ok():
                            frames.append((nm, fnum(d, 10), fnum(d, 20), fnum(d, 41, 1.0), fnum(d, 42, 1.0), lay))
                            continue
                    o = convert(typ, d, lay)
                    if o is None:
                        continue
                    xy = anchor(o)
                    if xy is None:
                        continue
                    items.append((xy[0], xy[1], o))

        sheets = []
        for nm, x, y, sx, sy, lay in frames:
            bb = block_bb[nm]
            x0, x1 = sorted((x + bb.x0 * sx, x + bb.x1 * sx))
            y0, y1 = sorted((y + bb.y0 * sy, y + bb.y1 * sy))
            sheets.append({"block": nm, "scale": sx, "rect": [x0, y0, x1, y1], "items": []})
        for x, y, o in items:
            for s in sheets:
                x0, y0, x1, y1 = s["rect"]
                if x0 <= x <= x1 and y0 <= y <= y1:
                    s["items"].append(o)
                    break

        picked = []
        for i, s in enumerate(sheets):
            g = sum(1 for o in s["items"] if o["l"] in AGPT_LAYERS and o["t"] in GEOM_TYPES)
            if g >= a.min_geom:
                picked.append((i, s, g))

        # определения блоков, вставленных на выбранных листах (один уровень)
        need = set()
        for _, s, _ in picked:
            for o in s["items"]:
                if o["t"] == "INSERT" and o.get("n"):
                    need.add(o["n"])
        blockdefs, curb = {}, None
        if need:
            with open_dxf(path) as f:
                seen = False
                for sec, typ, d in records(f):
                    if sec == "BLOCKS":
                        seen = True
                        if typ == "BLOCK":
                            nm = first(d, 2)
                            curb = nm if (nm in need and not is_frame_name(nm)) else None
                            if curb:
                                blockdefs[curb] = {"base": [fnum(d, 10, 0), fnum(d, 20, 0)], "ents": [], "truncated": False}
                        elif typ == "ENDBLK":
                            curb = None
                        elif curb:
                            b = blockdefs[curb]
                            if len(b["ents"]) >= a.block_limit:
                                b["truncated"] = True
                                continue
                            o = convert(typ, d, first(d, 8, "0"))
                            if o is not None:
                                b["ents"].append(local(o))
                    elif seen:
                        break

        for i, s, g in picked:
            paper = [to_paper(o, s) for o in s["items"][:a.max_items]]
            counts = defaultdict(int)
            for o in s["items"]:
                counts["%s|%s" % (o["l"], o["t"])] += 1
            title = ""
            for o in s["items"]:
                if o["t"] in ("MTEXT", "TEXT") and "Фрагмент плана" in o.get("txt", ""):
                    title = o["txt"][:200]
                    break
            x0, y0, x1, y1 = s["rect"]
            doc = {"file": os.path.basename(path), "sheet_index": i, "block": s["block"], "scale": s["scale"],
                   "rect_model": [r2(x0), r2(y0), r2(x1), r2(y1)],
                   "paper_mm": [r2((x1 - x0) / abs(s["scale"])), r2((y1 - y0) / abs(s["scale"]))],
                   "counts": dict(counts), "items": paper,
                   "blocks": {n: blockdefs[n] for n in {o.get("n") for o in s["items"]} if n in blockdefs}}
            fn = "%s__sheet%02d.plan.json" % (tag, i)
            with open(os.path.join(a.out, fn), "w", encoding="utf-8") as fh:
                json.dump(doc, fh, ensure_ascii=False, separators=(",", ":"))
            kb = os.path.getsize(os.path.join(a.out, fn)) / 1024
            index.append({"file": fn, "tag": tag, "sheet": i, "scale": s["scale"], "paper_mm": doc["paper_mm"],
                          "agpt_geom": g, "title": title, "kb": round(kb)})
            print("    %s  %.0f КБ  геом.АГПТ=%d  %s" % (fn, kb, g, title[:70]))

    with open(os.path.join(a.out, "index.json"), "w", encoding="utf-8") as fh:
        json.dump(index, fh, ensure_ascii=False, indent=1)
    print("Листов выгружено:", len(index))


if __name__ == "__main__":
    main()
