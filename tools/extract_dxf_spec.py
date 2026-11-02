#!/usr/bin/env python3
"""
extract_dxf_spec.py — вытаскивает из DXF-эталона компактную спецификацию оформления
(стили текста, слои, типы линий, блоки штампа/рамки, высоты текстов, прямоугольники рамок).

Без внешних зависимостей, потоковый разбор (файлы 25+ МБ ок).

Использование:
    python extract_dxf_spec.py <file.dxf> [more.dxf ...] -o reference_spec
Результат: reference_spec/<имя файла>.json  (десятки КБ — читается GitHub-коннектором)
"""
import sys, os, json, argparse
from collections import defaultdict, Counter


def open_dxf(path):
    # DXF R2007+ — UTF-8; старые — cp1251. Пробуем UTF-8, иначе cp1251.
    with open(path, "rb") as f:
        head = f.read(200000)
    try:
        head.decode("utf-8")
        enc = "utf-8"
    except UnicodeDecodeError:
        enc = "cp1251"
    return open(path, "r", encoding=enc, errors="replace", newline=None)


def pairs(f):
    while True:
        c = f.readline()
        if not c:
            return
        v = f.readline()
        if not v:
            return
        try:
            yield int(c.strip()), v.rstrip("\r\n")
        except ValueError:
            continue


def records(f):
    """Генератор: (section, type, {code: [values]}). SECTION/ENDSEC/EOF пропускаются."""
    section = None
    cur_type, cur = None, None
    for code, val in pairs(f):
        if code == 0:
            if cur_type is not None:
                yield section, cur_type, cur
            cur_type, cur = val, defaultdict(list)
            if val == "SECTION":
                pass
            elif val == "ENDSEC":
                cur_type, cur = None, None
                section = None
            elif val == "EOF":
                return
            continue
        if cur is None:
            continue
        if cur_type == "SECTION" and code == 2:
            section = val
            cur_type, cur = None, None
            continue
        cur[code].append(val)
    if cur_type is not None:
        yield section, cur_type, cur


def fnum(d, code, default=None, idx=0):
    try:
        return float(d[code][idx])
    except (KeyError, IndexError, ValueError):
        return default


def first(d, code, default=None):
    try:
        return d[code][0]
    except (KeyError, IndexError):
        return default


class BBox:
    def __init__(self):
        self.x0 = self.y0 = float("inf")
        self.x1 = self.y1 = float("-inf")

    def add(self, x, y):
        if x is None or y is None:
            return
        self.x0 = min(self.x0, x); self.x1 = max(self.x1, x)
        self.y0 = min(self.y0, y); self.y1 = max(self.y1, y)

    def out(self):
        if self.x0 == float("inf"):
            return None
        return [round(self.x0, 3), round(self.y0, 3), round(self.x1, 3), round(self.y1, 3)]


def extract(path):
    spec = {
        "file": os.path.basename(path),
        "size_bytes": os.path.getsize(path),
        "header": {},
        "styles": [], "layers": [], "linetypes": [], "dimstyles": [],
        "blocks": {}, "inserts": {}, "entity_counts": {},
        "layer_entity_counts": {},
        "text_styles_used": {},
        "rect_candidates": [],
        "tables_acad": 0,
    }
    ent_count = Counter()
    layer_ent = defaultdict(Counter)
    text_use = defaultdict(lambda: {"count": 0, "samples": []})
    inserts = defaultdict(lambda: {"count": 0, "bbox": BBox(), "scales": Counter(), "layers": Counter()})
    blocks = {}
    cur_block = None
    rects = []
    hdr_name = None
    hdr_vals = {}
    wanted_hdr = {"$INSUNITS", "$LTSCALE", "$DIMSCALE", "$TEXTSTYLE", "$DIMSTYLE", "$EXTMIN",
                  "$EXTMAX", "$LIMMIN", "$LIMMAX", "$MEASUREMENT", "$LWDISPLAY", "$ACADVER", "$DWGCODEPAGE"}

    with open_dxf(path) as f:
        # HEADER идёт отдельным проходом по сырым парам, т.к. там коды 9 (имя переменной)
        pass

    # --- HEADER (коды 9 + значения) ---
    with open_dxf(path) as f:
        in_header = False
        prev_sec = False
        name = None
        for code, val in pairs(f):
            if code == 0 and val == "SECTION":
                prev_sec = True
                continue
            if prev_sec and code == 2:
                in_header = (val == "HEADER")
                prev_sec = False
                if not in_header and val != "HEADER" and hdr_vals:
                    break
                continue
            if code == 0 and val == "ENDSEC" and in_header:
                break
            if in_header:
                if code == 9:
                    name = val
                    if name in wanted_hdr:
                        hdr_vals.setdefault(name, [])
                elif name in hdr_vals:
                    hdr_vals[name].append([code, val])
    spec["header"] = hdr_vals

    # --- основной проход ---
    with open_dxf(path) as f:
        for sec, typ, d in records(f):
            if sec == "TABLES":
                if typ == "STYLE":
                    spec["styles"].append({
                        "name": first(d, 2), "font": first(d, 3), "bigfont": first(d, 4),
                        "height": fnum(d, 40), "width_factor": fnum(d, 41),
                        "oblique": fnum(d, 50), "flags": first(d, 70)})
                elif typ == "LAYER":
                    spec["layers"].append({
                        "name": first(d, 2), "color": first(d, 62), "linetype": first(d, 6),
                        "lineweight": first(d, 370), "flags": first(d, 70),
                        "plot": first(d, 290)})
                elif typ == "LTYPE":
                    spec["linetypes"].append({
                        "name": first(d, 2), "desc": first(d, 3),
                        "pattern": [float(x) for x in d.get(49, [])][:12]})
                elif typ == "DIMSTYLE":
                    spec["dimstyles"].append({
                        "name": first(d, 2),
                        "DIMSCALE": fnum(d, 40), "DIMASZ": fnum(d, 41), "DIMEXO": fnum(d, 42),
                        "DIMEXE": fnum(d, 44), "DIMTXT": fnum(d, 140), "DIMGAP": fnum(d, 147),
                        "DIMTXSTY": first(d, 340), "DIMBLK": first(d, 342)})
                continue

            if sec == "BLOCKS":
                if typ == "BLOCK":
                    cur_block = first(d, 2)
                    blocks[cur_block] = {"base": [fnum(d, 10, 0), fnum(d, 20, 0)],
                                         "entities": Counter(), "attdefs": [], "bbox": BBox()}
                    continue
                if typ == "ENDBLK":
                    cur_block = None
                    continue
                if cur_block is None:
                    continue
                b = blocks[cur_block]
                b["entities"][typ] += 1
                if typ == "ATTDEF":
                    b["attdefs"].append({"tag": first(d, 2), "prompt": first(d, 3),
                                         "default": first(d, 1), "height": fnum(d, 40),
                                         "style": first(d, 7), "pos": [fnum(d, 10), fnum(d, 20)]})
                for xc, yc in ((10, 20), (11, 21)):
                    b["bbox"].add(fnum(d, xc), fnum(d, yc))
                if typ == "LWPOLYLINE":
                    for x, y in zip(d.get(10, []), d.get(20, [])):
                        b["bbox"].add(float(x), float(y))
                lay = first(d, 8, "0")
                layer_ent["BLOCKS:" + lay][typ] += 1
                continue

            if sec != "ENTITIES":
                continue

            ent_count[typ] += 1
            lay = first(d, 8, "0")
            layer_ent[lay][typ] += 1

            if typ in ("TEXT", "MTEXT", "ATTRIB"):
                style = first(d, 7, "STANDARD")
                h = fnum(d, 40)
                wf = fnum(d, 41) if typ != "MTEXT" else None
                key = "%s|h=%s|wf=%s|layer=%s" % (style, h, wf, lay)
                u = text_use[key]
                u["count"] += 1
                if len(u["samples"]) < 3:
                    s = "".join(d.get(3, [])) + first(d, 1, "")
                    u["samples"].append(s[:60])
            elif typ == "INSERT":
                nm = first(d, 2)
                x, y = fnum(d, 10), fnum(d, 20)
                r = inserts[nm]
                r["count"] += 1
                r["bbox"].add(x, y)
                r["scales"][(fnum(d, 41, 1.0), fnum(d, 42, 1.0))] += 1
                r["layers"][lay] += 1
            elif typ == "ACAD_TABLE":
                spec["tables_acad"] += 1
            elif typ == "LWPOLYLINE":
                xs, ys = d.get(10, []), d.get(20, [])
                closed = (int(first(d, 70, 0)) & 1) == 1
                if closed and len(xs) == 4:
                    x = [float(v) for v in xs]; y = [float(v) for v in ys]
                    w, h = max(x) - min(x), max(y) - min(y)
                    if w > 150 and h > 100:  # кандидаты на рамки/штампы по размеру
                        rects.append({"bbox": [round(min(x), 2), round(min(y), 2),
                                               round(max(x), 2), round(max(y), 2)],
                                      "w": round(w, 2), "h": round(h, 2), "layer": lay,
                                      "color": first(d, 62), "lineweight": first(d, 370),
                                      "const_width": fnum(d, 43)})

    spec["entity_counts"] = dict(ent_count)
    spec["layer_entity_counts"] = {k: dict(v) for k, v in layer_ent.items()}
    spec["text_styles_used"] = dict(sorted(text_use.items(), key=lambda kv: -kv[1]["count"])[:80])
    spec["inserts"] = {k: {"count": v["count"], "bbox": v["bbox"].out(),
                           "scales": {str(s): c for s, c in v["scales"].most_common(5)},
                           "layers": dict(v["layers"])}
                       for k, v in sorted(inserts.items(), key=lambda kv: -kv[1]["count"])[:80]}
    spec["blocks"] = {k: {"base": v["base"], "bbox": v["bbox"].out(),
                          "entities": dict(v["entities"]), "attdefs": v["attdefs"]}
                      for k, v in blocks.items()
                      if not k.startswith("*") and (v["attdefs"] or sum(v["entities"].values()) < 4000)}
    rects.sort(key=lambda r: -(r["w"] * r["h"]))
    spec["rect_candidates"] = rects[:60]
    return spec


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("files", nargs="+")
    ap.add_argument("-o", "--out", default="reference_spec")
    a = ap.parse_args()
    os.makedirs(a.out, exist_ok=True)
    for p in a.files:
        print("→", p)
        s = extract(p)
        out = os.path.join(a.out, os.path.splitext(os.path.basename(p))[0] + ".json")
        with open(out, "w", encoding="utf-8") as fh:
            json.dump(s, fh, ensure_ascii=False, indent=1)
        print("   ", out, "%.1f КБ" % (os.path.getsize(out) / 1024))


if __name__ == "__main__":
    main()
