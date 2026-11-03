#!/usr/bin/env python3
"""
dump_sheets.py — раскладывает содержимое DXF-эталона по ЛИСТАМ.

Листом считается каждая вставка блока рамки (анонимные *U…, «РамкаА3», «Ramka» и т.п.):
прямоугольник листа = точка вставки + габариты блока × масштаб.
Каждый примитив модели относится к листу, в прямоугольник которого попадает его первая точка.

Для каждого листа в JSON попадает:
  rect, scale, блок рамки; число примитивов по слоям и типам; вставленные блоки (имя → число);
  ВСЕ тексты (TEXT/MTEXT/ATTRIB: стиль, высота, слой, позиция относительно угла листа, строка);
  таблицы ACAD_TABLE (позиция, строки/колонки, высоты/ширины, тексты ячеек);
  выноски MULTILEADER (текст, слой); число размеров.
Всё, что не попало ни на один лист, суммируется в "outside".
Позиции даны в миллиметрах БУМАГИ (смещение от левого нижнего угла листа / масштаб).

Использование:
    python dump_sheets.py <file.dxf> [ещё.dxf ...] -o sheets_dump [--text-len 4000]
Результат: sheets_dump/<имя>.sheets.json
По умолчанию длина текста одного примитива ограничена 200 символами; для чтения методики и
пояснений на листах «Общие данные» задайте --text-len 4000.
"""
import sys, os, json, argparse
from collections import defaultdict, Counter

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from extract_dxf_spec import records, open_dxf, fnum, first  # noqa: E402


def is_frame_name(n):
    return n.startswith("*U") or "амка" in n or "Ramka" in n or "штамп" in n.lower()


class BB:
    def __init__(self):
        self.x0 = self.y0 = 1e30
        self.x1 = self.y1 = -1e30

    def add(self, x, y):
        if x is None or y is None:
            return
        self.x0 = min(self.x0, x); self.x1 = max(self.x1, x)
        self.y0 = min(self.y0, y); self.y1 = max(self.y1, y)

    def ok(self):
        return self.x0 < 1e29


def block_points(typ, d, bb):
    if typ in ("LINE", "TEXT", "MTEXT", "ATTDEF", "INSERT", "POINT"):
        bb.add(fnum(d, 10), fnum(d, 20))
        if typ == "LINE":
            bb.add(fnum(d, 11), fnum(d, 21))
    elif typ == "LWPOLYLINE":
        for x, y in zip(d.get(10, []), d.get(20, [])):
            bb.add(float(x), float(y))
    elif typ in ("CIRCLE", "ARC"):
        cx, cy, r = fnum(d, 10), fnum(d, 20), fnum(d, 40)
        if None not in (cx, cy, r):
            bb.add(cx - r, cy - r); bb.add(cx + r, cy + r)


def first_point(typ, d):
    x, y = fnum(d, 10), fnum(d, 20)
    return x, y


def text_of(d):
    return ("".join(d.get(3, [])) + first(d, 1, "")).replace("\r", " ").replace("\n", " ")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("files", nargs="+")
    ap.add_argument("-o", "--out", default="sheets_dump")
    ap.add_argument("--max-texts", type=int, default=250)
    ap.add_argument("--text-len", type=int, default=200,
                    help="максимальная длина текста одного примитива (по умолчанию 200)")
    a = ap.parse_args()
    os.makedirs(a.out, exist_ok=True)

    for path in a.files:
        print("→", path)
        block_bb = {}
        cur = None
        frames = []          # вставки рамок
        pending = []         # примитивы модели: (typ, layer, x, y, payload)
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
                    x, y = first_point(typ, d)
                    if typ == "INSERT":
                        nm = first(d, 2)
                        if nm in block_bb and block_bb[nm].ok():
                            frames.append({"block": nm, "x": x, "y": y,
                                           "sx": fnum(d, 41, 1.0), "sy": fnum(d, 42, 1.0),
                                           "rot": fnum(d, 50, 0.0), "layer": lay})
                            continue
                        pending.append(("INSERT", lay, x, y, nm))
                    elif typ in ("TEXT", "MTEXT", "ATTRIB"):
                        pending.append((typ, lay, x, y, {"s": first(d, 7), "h": fnum(d, 40),
                                                         "wf": fnum(d, 41) if typ != "MTEXT" else None,
                                                         "txt": text_of(d)[:a.text_len]}))
                    elif typ == "ACAD_TABLE":
                        pending.append((typ, lay, x, y, {
                            "rows": first(d, 91), "cols": first(d, 92),
                            "row_h": [float(v) for v in d.get(141, [])],
                            "col_w": [float(v) for v in d.get(142, [])],
                            "cells": (d.get(302, []) + d.get(1, []))[:500]}))
                    elif typ == "MULTILEADER":
                        pending.append((typ, lay, x, y, {"txt": "".join(d.get(304, []))[:a.text_len]}))
                    elif typ == "LWPOLYLINE":
                        pending.append((typ, lay, x, y, None))
                    else:
                        pending.append((typ, lay, x, y, None))

        # прямоугольники листов
        sheets = []
        for i, fr in enumerate(frames):
            bb = block_bb[fr["block"]]
            sx, sy = fr["sx"], fr["sy"]
            x0, x1 = sorted((fr["x"] + bb.x0 * sx, fr["x"] + bb.x1 * sx))
            y0, y1 = sorted((fr["y"] + bb.y0 * sy, fr["y"] + bb.y1 * sy))
            sheets.append({"idx": i, "block": fr["block"], "layer": fr["layer"],
                           "insert": [round(fr["x"], 1), round(fr["y"], 1)], "scale": [sx, sy],
                           "rot": fr["rot"], "rect": [round(x0, 1), round(y0, 1), round(x1, 1), round(y1, 1)],
                           "paper_mm": [round((x1 - x0) / abs(sx), 1) if sx else None,
                                        round((y1 - y0) / abs(sy), 1) if sy else None],
                           "_bbox": (x0, y0, x1, y1), "types_by_layer": defaultdict(Counter),
                           "inserts": Counter(), "texts": [], "tables": [], "mleaders": [],
                           "dimensions": 0})
        outside = defaultdict(Counter)
        for typ, lay, x, y, pl in pending:
            tgt = None
            if x is not None and y is not None:
                for s in sheets:
                    x0, y0, x1, y1 = s["_bbox"]
                    if x0 <= x <= x1 and y0 <= y <= y1:
                        tgt = s
                        break
            if tgt is None:
                outside[lay][typ] += 1
                continue
            tgt["types_by_layer"][lay][typ] += 1
            x0, y0, _, _ = tgt["_bbox"]
            k = abs(tgt["scale"][0]) or 1.0
            rel = [round((x - x0) / k, 1), round((y - y0) / k, 1)]
            if typ == "INSERT":
                tgt["inserts"][pl] += 1
            elif typ in ("TEXT", "MTEXT", "ATTRIB") and len(tgt["texts"]) < a.max_texts:
                h = pl["h"]
                tgt["texts"].append({"t": typ, "l": lay, "s": pl["s"], "h_model": h,
                                     "h_paper": round(h / k, 2) if h else None, "wf": pl["wf"],
                                     "pos_paper": rel, "txt": pl["txt"]})
            elif typ == "ACAD_TABLE":
                pl["layer"] = lay
                pl["pos_paper"] = rel
                tgt["tables"].append(pl)
            elif typ == "MULTILEADER":
                tgt["mleaders"].append({"l": lay, "pos_paper": rel, "txt": pl["txt"]})
            elif typ == "DIMENSION":
                tgt["dimensions"] += 1

        for s in sheets:
            del s["_bbox"]
            s["types_by_layer"] = {k: dict(v) for k, v in s["types_by_layer"].items()}
            s["inserts"] = dict(s["inserts"])
        out = {"file": os.path.basename(path), "sheet_count": len(sheets), "sheets": sheets,
               "outside": {k: dict(v) for k, v in outside.items()}}
        p = os.path.join(a.out, os.path.splitext(os.path.basename(path))[0] + ".sheets.json")
        with open(p, "w", encoding="utf-8") as fh:
            json.dump(out, fh, ensure_ascii=False, separators=(",", ":"))
        print("   ", p, "%.0f КБ" % (os.path.getsize(p) / 1024), "листов:", len(sheets))


if __name__ == "__main__":
    main()
