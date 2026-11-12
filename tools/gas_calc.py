#!/usr/bin/env python3
"""
gas_calc.py — эталонная (reference) реализация расчёта массы ГОТВ по СП РК 2.02-102-2022,
Приложение Г. Спецификация: docs/CALC_SPEC_RK.md, требования: docs/REQUIREMENTS.md.
C#-версия: src/Calc/GasCalc.cs (точный порт).

Решения владельца (2026-11-06):
  * коэффициенты строго по норме — ручного переопределения K3, K4, П, ρготв нет;
  * правило K3 между строками таблицы Д.11 — настройка ПРОЕКТА (ProjectSettings), не помещения;
  * если одного модуля не хватает — набор выбирает инженер (движок возвращает варианты и ждёт выбора).
"""
import math
from dataclasses import dataclass, field
from typing import List, Optional

T0_K = 293.0
K1 = 1.05                                   # Г.1 (норма)

# Таблица Д.11: высота над уровнем моря (м) -> K3
K3_TABLE = [(0, 1.00), (300, 0.96), (600, 0.93), (900, 0.89),
            (1200, 0.86), (1500, 0.82), (1800, 0.78), (2100, 0.75)]

# Положение проёмов -> П (Г.6)
PI_VALUES = {"bottom_and_top": 0.65, "top_only": 0.10, "bottom_only": 0.25, "other": 0.40}

# K4 для пожаров класса А (Г.7)
K4_VALUES = {"class_b_heptane": 1.0, "class_a_other": 1.5, "class_a_paper_rolls": 2.25}

# Таблица 9 (п. 5.4.1.10): объём помещения (верхняя граница, м3) -> допустимый параметр негерметичности, 1/м
LEAKAGE_TABLE = [(10, 0.0440), (20, 0.0330), (30, 0.0280), (50, 0.0220), (75, 0.0180), (100, 0.0160),
                 (150, 0.0140), (200, 0.0120), (250, 0.0110), (300, 0.0100), (400, 0.0090), (500, 0.0080),
                 (750, 0.0070), (1000, 0.0060), (1500, 0.0050), (2000, 0.0045), (2500, 0.0040),
                 (3000, 0.0037), (4000, 0.0033)]


@dataclass
class GotvSpec:
    name: str = "Хладон 227еа"
    kind: str = "liquefied"          # liquefied -> Г.3, compressed/co2 -> Г.4
    rho0: float = 7.28               # кг/м3 при 293 К, 101.3 кПа
    cn_heptane: float = 7.2          # % об., таблица Д.8
    tau_pod: float = 10.0            # с
    vapor_density_cap: float = 32.0  # кг/м3, плотность насыщенных паров при 20 C (справочно, проверить по паспорту)
    liquid_density: float = 1400.0   # кг/м3, плотность жидкой фазы при 20 C (справочно) — для проверки п. 5.4.3.9


@dataclass
class ProjectSettings:
    """Настройки ПРОЕКТА (одинаковы для всех помещений)."""
    altitude_m: float = 347.0        # Астана
    k3_mode: str = "lower_row"       # lower_row (консервативно) | interpolate — норма правило не оговаривает


@dataclass
class Module:
    name: str
    fill_kg: float
    residual_kg: float = 0.0         # Мб по эксплуатационным документам (0 — не задан)


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
    t_min_c: float = 20.0            # минимальная температура воздуха, °C
    fire: str = "class_a_other"      # вид горючего -> K4 (входные данные, не коэффициент)
    pipe_volume: float = 0.0         # Vтр, м3
    p_residual_abs_mpa: Optional[float] = None   # остаточное давление в трубах (абс.), из гидравлического расчёта
    chosen_modules: Optional[List[str]] = None   # решение инженера: имена модулей набора (если одного не хватает)


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
    needs_engineer_decision: bool = False
    candidates: List[List[Module]] = field(default_factory=list)
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


def leakage_limit(volume: float) -> Optional[float]:
    for upper, lim in LEAKAGE_TABLE:
        if volume <= upper:
            return lim
    return None  # выше 4000 м3 — по продолжению таблицы 9 (не закодировано)


def estimate_rho_residual(p_abs_mpa: float, t_min_c: float, g: GotvSpec) -> float:
    """Плотность остатка: идеальный газ при давлении p, не выше плотности насыщенных паров (оценка, подтвердить паспортом)."""
    rho = g.rho0 * (T0_K / (t_min_c + 273.0)) * (p_abs_mpa / 0.1013)
    return min(rho, g.vapor_density_cap)


def select_modules(mg_required: float, catalog: List[Module], max_count: int = 6,
                   rule: str = "min_count") -> List[Module]:
    """Набор модулей с суммарной заправкой >= mg_required.
    min_count: меньше модулей, затем меньше заправки; min_total: меньше заправки, затем меньше модулей."""
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


def single_module_for(mg: float, catalog: List[Module]) -> Optional[Module]:
    fits = [m for m in catalog if m.fill_kg >= mg]
    return min(fits, key=lambda m: m.fill_kg) if fits else None


def compute(room: RoomInput, project: ProjectSettings = None, g: GotvSpec = None,
            catalog: List[Module] = None) -> CalcResult:
    project = project or ProjectSettings()
    g = g or GotvSpec()
    catalog = DEFAULT_CATALOG if catalog is None else catalog
    r = CalcResult()
    tm_k = room.t_min_c + 273.0
    r.k3 = k3_from_altitude(project.altitude_m, project.k3_mode)
    r.rho1 = g.rho0 * (T0_K / tm_k) * r.k3                                   # Г.5
    vd = room.delta_volume if room.delta_volume else room.volume
    r.delta = room.openings_area / vd                                          # δ = ΣFн/Vр
    r.pi = PI_VALUES[room.openings_pos]
    r.k2 = r.pi * r.delta * g.tau_pod * math.sqrt(room.height)                 # Г.6
    cn = g.cn_heptane
    form = cn / (100.0 - cn) if g.kind == "liquefied" else math.log(100.0 / (100.0 - cn))   # Г.3 / Г.4
    r.mp_base = room.volume * r.rho1 * (1.0 + r.k2) * form
    r.k4 = K4_VALUES[room.fire]
    r.mp = r.k4 * r.mp_base                                                    # Г.7

    # Проверки негерметичности
    if room.fire.startswith("class_a") and r.delta > 0.001:
        r.warnings.append("δ = %.5f > 0.001 м^-1: для класса А не допускается (Г.7)" % r.delta)
    lim = leakage_limit(room.volume)
    if lim is not None and r.delta > lim:
        r.warnings.append("δ = %.5f превышает допустимое %.4f для объёма %.1f м3 (табл. 9, п. 5.4.1.10)" % (r.delta, lim, room.volume))

    # Mтр (Г.2): плотность остатка — из остаточного давления гидравлического расчёта
    if room.p_residual_abs_mpa is not None:
        r.rho_residual = estimate_rho_residual(room.p_residual_abs_mpa, room.t_min_c, g)
    else:
        r.rho_residual = 0.0
        if room.pipe_volume > 0:
            r.warnings.append("Остаточное давление в трубах не задано (нужен гидравлический расчёт): Mтр принят 0")
    r.mtr = room.pipe_volume * r.rho_residual

    # Проверка п. 5.4.3.9: Vтр не более 80% объёма жидкой фазы расчётной массы ГОТВ
    if room.pipe_volume > 0 and g.liquid_density > 0:
        vmax = 0.8 * r.mp / g.liquid_density
        if room.pipe_volume > vmax:
            r.warnings.append("Vтр = %.4f м3 больше 80%% объёма жидкой фазы (%.4f м3), п. 5.4.3.9" % (room.pipe_volume, vmax))

    # Mг (Г.1) с итерацией по Mбn = Mб·n; выбор модулей
    mods: List[Module] = []
    for it in range(6):
        mbn = sum(m.residual_kg for m in mods)
        mg = K1 * (r.mp + r.mtr + mbn)
        m1 = single_module_for(mg, catalog)
        if m1 is not None:
            new_mods, cands = [m1], []
        else:
            cands = []
            for rule in ("min_count", "min_total"):
                s = select_modules(mg, catalog, rule=rule)
                if s and [m.name for m in s] not in [[x.name for x in c] for c in cands]:
                    cands.append(s)
            new_mods = []
            if room.chosen_modules:
                by_name = {m.name: m for m in catalog}
                new_mods = [by_name[n] for n in room.chosen_modules if n in by_name]
        r.candidates = cands
        if it > 0 and [m.name for m in new_mods] == [m.name for m in mods]:
            break
        mods = new_mods
    r.mbn = sum(m.residual_kg for m in mods)
    r.mg = K1 * (r.mp + r.mtr + r.mbn)
    r.modules = mods
    if not mods:
        r.needs_engineer_decision = True
        biggest = max(m.fill_kg for m in catalog)
        r.warnings.append("Mг %.1f кг больше самого крупного модуля (%.0f кг): набор модулей выбирает инженер (%d варианта)" % (
            r.mg, biggest, len(r.candidates)))
    else:
        if room.chosen_modules and sum(m.fill_kg for m in mods) < r.mg:
            r.warnings.append("Выбранный набор (%.0f кг) меньше Mг = %.1f кг" % (sum(m.fill_kg for m in mods), r.mg))
        if all(m.residual_kg == 0 for m in mods):
            r.warnings.append("Остаток в модуле Mб не задан (0): Mбn не учтён — взять из паспорта модуля")
    return r
