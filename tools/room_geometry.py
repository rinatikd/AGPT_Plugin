#!/usr/bin/env python3
"""
room_geometry.py — выгружает ГЕОМЕТРИЮ листа эталона, на котором найден заданный текст
(например «Помещение серверной»): линии, полилинии, размеры, подписи, вставки.
Нужен, чтобы по самому чертежу измерить площадь помещения (стены в слоях ®_КИРПИЧ_120 и т. п.).

Использование (из корня репозитория):
    python tools\room_geometry.py <file.dxf> --find "Помещение серверной" -o geom_dump
Результат: geom_dump/<имя>.geom.json — по одному элементу на лист, в котором найден текст.
Координаты в единицах модели (мм). Размеры DIMENSION содержат измеренное значение (код 42).
"""
import sys, os, json, argparse
from collections import defaultdict

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from extract_dxf_spec import records, open_dxf, fnum, first  # noqa: E402
from dump_sheets import BB, block_points, is_frame_name, text_of  # noqa: E402


def r2(v):
    return None if v is None else round(v, 2)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("files", nargs="+")
    ap.add_argument("--find", required=True, help="подстрока текста, по которой выбирается лист")
    ap.add_argument("-o", "--out", default="geom_dump")
    ap.add_argument("--max", type=int, default=6000, help="максимум примитивов на лист")
    a = ap.parse_args()
    os.makedirs(a.out, exist_ok=True)
    needle = a.find.lower()

    for path in a.files:
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
                        else:
                            items.append(("INSERT", lay, fnum(d, 10), fnum(d, 20), {"n": nm}))
                    elif typ == "LINE":
                        items.append(("LINE", lay, fnum(d, 10), fnum(d, 20),
                                      {"p": [r2(fnum(d, 10)), r2(fnum(d, 20)), r2(fnum(d, 11)), r2(fnum(d, 21))]}))
                    elif typ == "LWPOLYLINE":
                        xs, ys = d.get(10, []), d.get(20, [])
                        if xs:
                            items.append(("LWPOLYLINE", lay, float(xs[0]), float(ys[0]), {
                                "closed": (int(first(d, 70, 0)) & 1) == 1,
                                "v": [[r2(float(x)), r2(float(y))] for x, y in zip(xs, ys)],
                                "bulge": d.get(42)}))
                    elif typ == "DIMENSION":
                        items.append(("DIMENSION", lay, fnum(d, 10), fnum(d, 20), {
                            "measurement": fnum(d, 42), "text": first(d, 1),
                            "p13": [r2(fnum(d, 13)), r2(fnum(d, 23))], "p14": [r2(fnum(d, 14)), r2(fnum(d, 24))]}))
                    elif typ in ("TEXT", "MTEXT"):
                        items.append((typ, lay, fnum(d, 10), fnum(d, 20),
                                      {"h": r2(fnum(d, 40)), "txt": text_of(d)[:200]}))
                    elif typ == "HATCH":
                        items.append(("HATCH", lay, None, None, {"pattern": first(d, 2)}))

        sheets = []
        for nm, x, y, sx, sy, lay in frames:
            bb = block_bb[nm]
            x0, x1 = sorted((x + bb.x0 * sx, x + bb.x1 * sx))
            y0, y1 = sorted((y + bb.y0 * sy, y + bb.y1 * sy))
            sheets.append({"block": nm, "scale": sx, "rect": [r2(x0), r2(y0), r2(x1), r2(y1)], "items": []})
        for typ, lay, x, y, pl in items:
            if x is None or y is None:
                continue
            for s in sheets:
                x0, y0, x1, y1 = s["rect"]
                if x0 <= x <= x1 and y0 <= y <= y1:
                    s["items"].append({"t": typ, "l": lay, **pl})
                    break
        out = []
        for i, s in enumerate(sheets):
            hit = any(it["t"] in ("TEXT", "MTEXT") and needle in it.get("txt", "").lower() for it in s["items"])
            if hit:
                s["sheet_index"] = i
                s["items"] = s["items"][:a.max]
                counts = defaultdict(int)
                for it in s["items"]:
                    counts[(it["l"], it["t"])] += 1
                s["counts"] = {"%s|%s" % k: v for k, v in counts.items()}
                out.append(s)
        p = os.path.join(a.out, os.path.splitext(os.path.basename(path))[0] + ".geom.json")
        with open(p, "w", encoding="utf-8") as fh:
            json.dump(out, fh, ensure_ascii=False, separators=(",", ":"))
        print("   ", p, "%.0f КБ" % (os.path.getsize(p) / 1024), "листов найдено:", len(out))


if __name__ == "__main__":
    main()
