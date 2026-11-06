#!/usr/bin/env python3
"""
check_golden.py — прогоняет движок tools/gas_calc.py по контрольным примерам эталона
(tests/golden/mgimo_calc_golden.json) и сверяет с таблицами эталона.

Запуск из корня репозитория:  python tools/check_golden.py
Код возврата 0 — все проверки пройдены, 1 — есть расхождения.
"""
import json, os, sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from gas_calc import RoomInput, GotvSpec, compute  # noqa: E402

GOLDEN = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "tests", "golden", "mgimo_calc_golden.json")

# Входы, которые в таблице «Исходные данные» эталона не выражены напрямую
OVERRIDES = {
    "1117": {"volume": 57.0, "height": 3.85, "delta_volume": 12.0 * 3.85},   # объём с фальшполом; K2 по основному помещению
    "1435": {"volume": 221.0, "delta_volume": 221.0},                          # объём, подразумеваемый таблицей эталона
}
# Какой модуль выбран в эталоне (по спецификации): ключ — Mг из таблицы расчёта
REF_MODULE = {28: 28, 182: 184, 51: 50, 42: 42, 50: 50, 191: 192}

TOL_MP = 0.01      # 1% на Mр
TOL_RHO = 0.002    # 0.2% на ρ1


def key_of(room):
    return room.split()[0]


def main():
    with open(GOLDEN, encoding="utf-8") as f:
        g = json.load(f)
    fails = 0
    print("%-28s %8s %8s %8s %8s %9s  %s" % ("Помещение", "ρ1", "K2", "Mр", "Mр эт.", "откл.", "модуль (расчёт/эталон)"))
    for c in g["cases"]:
        i, e = c["input"], c["expected"]
        k = key_of(c["room"])
        ov = OVERRIDES.get(k, {})
        room = RoomInput(
            name=c["room"], volume=ov.get("volume", i["S"] * i["H"]), height=ov.get("height", i["H"]),
            openings_area=i["Fh"], delta_volume=ov.get("delta_volume"), pi_override=i["Pi"],
            t_min_c=i["Tm_C"], altitude_m=347.0, k3_mode="lower_row", fire="class_a_other",
            k4_override=i.get("K4", 1.5), pipe_volume=i["Vtr"])
        r = compute(room, GotvSpec())
        dev = r.mp / e["Mp"] - 1.0
        ok_mp = abs(dev) <= TOL_MP or (k == "1016" and abs(dev) <= 0.006)   # 1016: округление S в таблице эталона
        ok_rho = abs(r.rho1 / e["rho1"] - 1.0) <= TOL_RHO
        ref_mod = REF_MODULE.get(int(e["Mg"]))
        got_mod = sum(m.fill_kg for m in r.modules)
        ok_mod = (got_mod == ref_mod)
        status = "OK" if (ok_mp and ok_rho and ok_mod) else "FAIL"
        if status == "FAIL":
            fails += 1
        print("%-28s %8.3f %8.6f %8.2f %8.2f %+8.2f%%  %s/%s %s" % (
            c["room"][:28], r.rho1, r.k2, r.mp, e["Mp"], dev * 100, int(got_mod), ref_mod, status))
    # Контрольные значения по норме для архива (K4 = 2.25), информативно
    print("\nАрхив 1435 по норме (K4 = 2.25), Mтр/Mбn не заданы:")
    for v in (194.19, 221.0):
        for rule in ("min_count", "min_total"):
            room = RoomInput(name="1435", volume=v, height=4.3, openings_area=0.017, pi_override=0.65,
                             t_min_c=20, fire="class_a_paper_rolls", pipe_volume=0.0033, selection_rule=rule)
            r = compute(room)
            print("  V = %6.1f м3, правило %-9s: Mр = %6.1f кг, Mг = %6.1f кг, модулей: %d (%s) = %d кг" % (
                v, rule, r.mp, r.mg, len(r.modules), "+".join(str(int(m.fill_kg)) for m in r.modules),
                sum(m.fill_kg for m in r.modules)))
    print("\nИТОГО: %s" % ("все проверки пройдены" if fails == 0 else "расхождений: %d" % fails))
    return 1 if fails else 0


if __name__ == "__main__":
    sys.exit(main())
