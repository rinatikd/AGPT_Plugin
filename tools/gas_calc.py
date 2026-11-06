#!/usr/bin/env python3
"""
gas_calc.py — эталонная (reference) реализация расчёта массы ГОТВ по СП РК 2.02-102-2022,
Приложение Г. Спецификация: docs/CALC_SPEC_RK.md. C#-версия: src/Calc/GasCalc.cs (точный порт).

Все параметры, которые норма не фиксирует однозначно (правило K3 между строками таблицы,
плотность остатка ГОТВ, подбор модулей), вынесены во входы и помечены как «настройки проекта».
"""
import math
from dataclasses import dataclass, field
from typing import List, Optional

T0_K = 293.0

# Таблица Д.11: высота над уровнем моря (м) -> K3
K3_TABLE = [(0, 1.00), (300, 0.96), (600, 0.93), (900, 0.89),
            (1200, 0.86), (1500, 0.82), (1800, 0.78), (2100, 0.75)]

# Положение проёмов -> П (Г.6)
PI_VALUES = {"bottom_and_top": 0.65, "top_only": 0.10, "bottom_only": 0.25, "other": 0.40}

# K4 для пожаров класса А (Г.7)
K4_VALUES = {"class_b_heptane": 1.0, "class_a_other": 1.5, "class_a_paper_rolls": 2.25}


@dataclass
class GotvSpec:
    name: str = "Хладон 227еа"
    kind: str = "liquefied"          # liquefied -> Г.3, compressed/co2 -> Г.4
    rho0: float = 7.28               # кг/м3 при 293 К, 101.3 кПа
    cn_heptane: float = 7.2          # % об., таблица Д.8
    tau_pod: float = 10.0            # с
    vapor_density_cap: float = 32.0  # кг/м3, плотность насыщенных паров при 20 C (справочно, проверить по паспорту)


@dataclass
class Module:
    name: str
    fill_kg: float
    residual_kg: float = 0.0         # Мб по эксплуатационным документам (если неизвестно — 0, помечается предупреждением)


DEFAULT_CATALOG = [
    Module("МПТГ «Proffex» 66-40-32 (28 кг)", 28.0),
    Module("МПТГ «Proffex» 66-40-32 (42 кг)", 42.0),
    Module("МПТГ «Proffex» 66-50-32 (50 кг)", 50.0),
    Module("МПТГ «Proffex» 66-180-50 (184 кг)", 184.0),
    Module("МПТГ «Proffex» 66-180-50 (192 кг)", 192.0),
]


@dataclass
class RoomInput:
    name: str = ""
    volume: float = 0.0              # Vр, м3 (расчётный объём; может включать подпольное/надпотолочное)
    height: float = 3.0              # H, м — высота для K2
    openings_area: float = 0.0       # ΣFн, м2
    delta_volume: Optional[float] = None   # объём для δ (если None — Vр)
    openings_pos: str = "bottom_and_top"
    pi_override: Optional[float] = None
    t_min_c: float = 20.0            # минимальная температура, °C
    altitude_m: float = 347.0        # Астана
    k3_mode: str = "lower_row"       # lower_row | interpolate
    k3_override: Optional[float] = None
    fire: str = "class_a_other"
    k4_override: Optional[float] = None
    pipe_volume: float = 0.0         # Vтр, м3
    rho_residual: Optional[float] = None   # плотность остатка ГОТВ, кг/м3 (если None — оценка по p_residual)
    p_residual_abs_mpa: Optional[float] = None
    k1: float = 1.05
    selection_rule: str = "min_count"   # min_count | min_total


@dataclass
class CalcResult:
    rho1: float = 0.0
    k3: float = 0.0
    delta: float = 0.0
    k2: float = 0.0
    pi: float = 0.0
    k4: float = 1.0
    mp_base: float = 0.0             # Mр без K4 (по н-гептану)
    mp: float = 0.0
    rho_residual: float = 0.0
    mtr: float = 0.0
    mbn: float = 0.0
    mg: float = 0.0
    modules: List[Module] = field(default_factory=list)
    warnings: List[str] = field(default_factory=list)


def k3_from_altitude(h: float, mode: str = "lower_row") -> float:
    if h <= K3_TABLE[0][0]:
        return K3_TABLE[0][1]
    if h >= K3_TABLE[-1][0]:
        return K3_TABLE[-1][1]
    for (a, ka), (b, kb) in zip(K3_TABLE, K3_TABLE[1:]):
        if a <= h <= b:
            if mode == "interpolate":
                return ka + (kb - ka) * (h - a) / (b - a)
            return ka  # ближайшая нижняя строка (консервативно)
    return 1.0


def estimate_rho_residual(p_abs_mpa: float, t_min_c: float, g: GotvSpec) -> float:
    """Оценка плотности остатка: идеальный газ при давлении p, не выше плотности насыщенных паров."""
    rho = g.rho0 * (T0_K / (t_min_c + 273.0)) * (p_abs_mpa / 0.1013)
    return min(rho, g.vapor_density_cap)


def select_modules(mg_required: float, catalog: List[Module], max_count: int = 6,
                   rule: str = "min_count") -> List[Module]:
    """Подбор набора модулей с суммарной заправкой >= mg_required. (Предварительное правило, утверждает инженер.)
    rule = "min_count": сначала минимальное число модулей, затем минимальная суммарная заправка (по умолчанию);
    rule = "min_total": минимальная суммарная заправка, затем меньше модулей."""
    sizes = sorted(catalog, key=lambda m: m.fill_kg)

    def best_for_count(k):
        best = None

        def rec(idx, left, total, chosen):
            nonlocal best
            if left == 0:
                if total >= mg_required and (best is None or total < best[0]):
                    best = (total, list(chosen))
                return
            for i in range(idx, len(sizes)):
                chosen.append(sizes[i])
                rec(i, left - 1, total + sizes[i].fill_kg, chosen)
                chosen.pop()

        rec(0, k, 0.0, [])
        return best

    if rule == "min_total":
        cands = [best_for_count(k) for k in range(1, max_count + 1)]
        cands = [c for c in cands if c]
        if not cands:
            return []
        cands.sort(key=lambda c: (round(c[0], 6), len(c[1])))
        return cands[0][1]
    for k in range(1, max_count + 1):
        b = best_for_count(k)
        if b:
            return b[1]
    return []


def compute(room: RoomInput, g: GotvSpec = None, catalog: List[Module] = None) -> CalcResult:
    g = g or GotvSpec()
    catalog = DEFAULT_CATALOG if catalog is None else catalog
    r = CalcResult()
    tm_k = room.t_min_c + 273.0
    r.k3 = room.k3_override if room.k3_override is not None else k3_from_altitude(room.altitude_m, room.k3_mode)
    r.rho1 = g.rho0 * (T0_K / tm_k) * r.k3                                   # Г.5
    vd = room.delta_volume if room.delta_volume else room.volume
    r.delta = room.openings_area / vd                                          # δ = ΣFн/Vр
    r.pi = room.pi_override if room.pi_override is not None else PI_VALUES[room.openings_pos]
    r.k2 = r.pi * r.delta * g.tau_pod * math.sqrt(room.height)                 # Г.6
    cn = g.cn_heptane
    if g.kind == "liquefied":
        form = cn / (100.0 - cn)                                               # Г.3
    else:
        form = math.log(100.0 / (100.0 - cn))                                  # Г.4
    r.mp_base = room.volume * r.rho1 * (1.0 + r.k2) * form
    r.k4 = room.k4_override if room.k4_override is not None else K4_VALUES[room.fire]
    r.mp = r.k4 * r.mp_base                                                    # Г.7
    if room.fire.startswith("class_a") and r.delta > 0.001:
        r.warnings.append("δ = %.5f > 0.001 м^-1: для класса А не допускается (Г.7)" % r.delta)
    # Mтр (Г.2)
    if room.rho_residual is not None:
        r.rho_residual = room.rho_residual
    elif room.p_residual_abs_mpa is not None:
        r.rho_residual = estimate_rho_residual(room.p_residual_abs_mpa, room.t_min_c, g)
    else:
        r.rho_residual = 0.0
        if room.pipe_volume > 0:
            r.warnings.append("ρготв (остаток в трубах) не задан: Mтр принят 0 — задайте давление остатка или плотность")
    r.mtr = room.pipe_volume * r.rho_residual
    # Mг (Г.1) с итерацией по Mбn = Mб·n
    mods: List[Module] = []
    for it in range(6):
        mbn = sum(m.residual_kg for m in mods)
        mg = room.k1 * (r.mp + r.mtr + mbn)
        new_mods = select_modules(mg, catalog, rule=room.selection_rule)
        if it > 0 and [m.name for m in new_mods] == [m.name for m in mods]:
            break
        mods = new_mods
    r.mbn = sum(m.residual_kg for m in mods)
    r.mg = room.k1 * (r.mp + r.mtr + r.mbn)
    r.modules = mods
    if len(mods) > 1:
        r.warnings.append("Mг %.1f кг больше одного модуля: набор из %d модулей (правило подбора предварительное)" % (r.mg, len(mods)))
    if mods and all(m.residual_kg == 0 for m in mods):
        r.warnings.append("Остаток в модуле Mб не задан (0): Mбn не учтён — взять из паспорта модуля")
    return r
