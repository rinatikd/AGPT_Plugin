// GasCalc.cs — расчёт массы ГОТВ по СП РК 2.02-102-2022, Приложение Г.
// Точный порт tools/gas_calc.py. Спецификация: docs/CALC_SPEC_RK.md, требования: docs/REQUIREMENTS.md.
// Чистая логика без зависимостей от AutoCAD; проверяется tests/GasCalc.Tests по tests/golden.
//
// Решения владельца: коэффициенты строго по норме (без ручного переопределения K3, K4, П, ρготв);
// правило K3 между строками таблицы Д.11 — настройка проекта; если одного модуля не хватает,
// набор модулей выбирает инженер (движок возвращает варианты и ждёт выбора в RoomInput.ChosenModules).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AGPT.Calc
{
    public enum GasKind { Liquefied, Compressed, Co2 }
    public enum OpeningsPos { BottomAndTop, TopOnly, BottomOnly, Other }
    public enum FireKind { ClassBHeptane, ClassAOther, ClassAPaperRolls }
    public enum K3Mode { LowerRow, Interpolate }
    public enum SelectionRule { MinCount, MinTotal }

    public class GotvSpec
    {
        public string Name = "Хладон 227еа";
        public GasKind Kind = GasKind.Liquefied;   // Liquefied -> Г.3, иначе Г.4
        public double Rho0 = 7.28;                 // кг/м3 при 293 К, 101.3 кПа
        public double CnHeptane = 7.2;             // % об., таблица Д.8
        public double TauPod = 10.0;               // с
        public double VaporDensityCap = 32.0;      // кг/м3, плотность насыщенных паров при 20 C (справочно, проверить по паспорту)
        public double LiquidDensity = 1400.0;      // кг/м3, плотность жидкой фазы при 20 C (справочно) — для проверки п. 5.4.3.9
    }

    // Настройки ПРОЕКТА (одинаковы для всех помещений)
    public class ProjectSettings
    {
        public double AltitudeM = 347.0;           // Астана
        public K3Mode K3Mode = K3Mode.LowerRow;    // норма правило между строками таблицы не оговаривает
    }

    public class Module
    {
        public string Name;
        public double FillKg;
        public double ResidualKg;                  // Мб по эксплуатационным документам; 0 — не задан

        public Module(string name, double fillKg, double residualKg)
        {
            Name = name; FillKg = fillKg; ResidualKg = residualKg;
        }
    }

    public class RoomInput
    {
        public string Name = "";
        public double Volume;                      // Vр, м3 (может включать подпольное/надпотолочное)
        public double Height = 3.0;                // H, м — высота для K2
        public double OpeningsArea;                // ΣFн, м2
        public double? DeltaVolume;                // объём для δ (если null — Vр)
        public OpeningsPos OpeningsPosition = OpeningsPos.BottomAndTop;
        public double TMinC = 20.0;                // минимальная температура воздуха, °C
        public FireKind Fire = FireKind.ClassAOther;   // вид горючего -> K4 (входные данные, не коэффициент)
        public double PipeVolume;                  // Vтр, м3
        public double? PResidualAbsMpa;            // остаточное давление в трубах (абс.), из гидравлического расчёта
        public List<string> ChosenModules;         // решение инженера: имена модулей набора (если одного не хватает)
    }

    public class CalcResult
    {
        public double Rho1, K3, Delta, K2, Pi, K4 = 1.0;
        public double MpBase, Mp, RhoResidual, Mtr, Mbn, Mg;
        public List<Module> Modules = new List<Module>();
        public bool NeedsEngineerDecision;
        public List<List<Module>> Candidates = new List<List<Module>>();
        public List<string> Warnings = new List<string>();
    }

    public static class GasCalc
    {
        public const double T0K = 293.0;
        public const double K1 = 1.05;             // Г.1 (норма)

        // Таблица Д.11: высота над уровнем моря (м) -> K3
        private static readonly double[] K3Alt = { 0, 300, 600, 900, 1200, 1500, 1800, 2100 };
        private static readonly double[] K3Val = { 1.00, 0.96, 0.93, 0.89, 0.86, 0.82, 0.78, 0.75 };

        // Таблица 9 (п. 5.4.1.10): верхняя граница объёма помещения (м3) -> допустимый параметр негерметичности (1/м)
        private static readonly double[] LeakVol = { 10, 20, 30, 50, 75, 100, 150, 200, 250, 300, 400, 500, 750, 1000, 1500, 2000, 2500, 3000, 4000 };
        private static readonly double[] LeakLim = { 0.0440, 0.0330, 0.0280, 0.0220, 0.0180, 0.0160, 0.0140, 0.0120, 0.0110, 0.0100, 0.0090, 0.0080, 0.0070, 0.0060, 0.0050, 0.0045, 0.0040, 0.0037, 0.0033 };

        public static double PiValue(OpeningsPos p)
        {
            switch (p)
            {
                case OpeningsPos.BottomAndTop: return 0.65;
                case OpeningsPos.TopOnly: return 0.10;
                case OpeningsPos.BottomOnly: return 0.25;
                default: return 0.40;
            }
        }

        public static double K4Value(FireKind f)
        {
            switch (f)
            {
                case FireKind.ClassAOther: return 1.5;
                case FireKind.ClassAPaperRolls: return 2.25;
                default: return 1.0;
            }
        }

        public static List<Module> DefaultCatalog()
        {
            var c = new List<Module>();
            c.Add(new Module("МПТГ «Proffex» 66-40-32 (28 кг)", 28.0, 0.0));
            c.Add(new Module("МПТГ «Proffex» 66-40-32 (42 кг)", 42.0, 0.0));
            c.Add(new Module("МПТГ «Proffex» 66-50-32 (50 кг)", 50.0, 0.0));
            c.Add(new Module("МПТГ «Proffex» 66-180-50 (184 кг)", 184.0, 0.0));
            c.Add(new Module("МПТГ «Proffex» 66-180-50 (192 кг)", 192.0, 0.0));
            return c;
        }

        public static double K3FromAltitude(double h, K3Mode mode)
        {
            if (h <= K3Alt[0]) return K3Val[0];
            int last = K3Alt.Length - 1;
            if (h >= K3Alt[last]) return K3Val[last];
            for (int i = 0; i < last; i++)
            {
                double a = K3Alt[i], b = K3Alt[i + 1];
                if (h >= a && h <= b)
                {
                    if (mode == K3Mode.Interpolate)
                        return K3Val[i] + (K3Val[i + 1] - K3Val[i]) * (h - a) / (b - a);
                    return K3Val[i];   // ближайшая нижняя строка (консервативно)
                }
            }
            return 1.0;
        }

        // Допустимый параметр негерметичности по объёму; null — выше 4000 м3 (продолжение таблицы 9 не закодировано)
        public static double? LeakageLimit(double volume)
        {
            for (int i = 0; i < LeakVol.Length; i++)
                if (volume <= LeakVol[i]) return LeakLim[i];
            return null;
        }

        // Плотность остатка: идеальный газ при давлении p, не выше плотности насыщенных паров (оценка, подтвердить паспортом).
        public static double EstimateRhoResidual(double pAbsMpa, double tMinC, GotvSpec g)
        {
            double rho = g.Rho0 * (T0K / (tMinC + 273.0)) * (pAbsMpa / 0.1013);
            return Math.Min(rho, g.VaporDensityCap);
        }

        private static List<Module> BestForCount(double mgRequired, List<Module> sizes, int k, out double bestTotal)
        {
            List<Module> best = null;
            double bt = double.MaxValue;
            List<Module> chosen = new List<Module>();

            void Rec(int idx, int left, double total)
            {
                if (left == 0)
                {
                    if (total >= mgRequired && total < bt)
                    {
                        bt = total;
                        best = new List<Module>(chosen);
                    }
                    return;
                }
                for (int i = idx; i < sizes.Count; i++)
                {
                    chosen.Add(sizes[i]);
                    Rec(i, left - 1, total + sizes[i].FillKg);
                    chosen.RemoveAt(chosen.Count - 1);
                }
            }

            Rec(0, k, 0.0);
            bestTotal = bt;
            return best;
        }

        // Набор модулей с суммарной заправкой >= mgRequired.
        // MinCount: сначала минимальное число модулей, затем минимальная заправка.
        // MinTotal: минимальная суммарная заправка, затем меньше модулей.
        public static List<Module> SelectModules(double mgRequired, List<Module> catalog, int maxCount, SelectionRule rule)
        {
            List<Module> sizes = new List<Module>(catalog);
            sizes.Sort(delegate (Module a, Module b) { return a.FillKg.CompareTo(b.FillKg); });

            if (rule == SelectionRule.MinTotal)
            {
                List<Module> bestSet = null;
                double bestTotal = double.MaxValue;
                for (int k = 1; k <= maxCount; k++)
                {
                    double t;
                    List<Module> cand = BestForCount(mgRequired, sizes, k, out t);
                    if (cand != null && Math.Round(t, 6) < Math.Round(bestTotal, 6))
                    {
                        bestTotal = t;
                        bestSet = cand;
                    }
                }
                return bestSet != null ? bestSet : new List<Module>();
            }

            for (int k = 1; k <= maxCount; k++)
            {
                double t;
                List<Module> cand = BestForCount(mgRequired, sizes, k, out t);
                if (cand != null) return cand;
            }
            return new List<Module>();
        }

        // Один модуль с заправкой >= mg (наименьший из подходящих); null — одного модуля не хватает
        public static Module SingleModuleFor(double mg, List<Module> catalog)
        {
            Module best = null;
            foreach (Module m in catalog)
                if (m.FillKg >= mg && (best == null || m.FillKg < best.FillKg)) best = m;
            return best;
        }

        private static bool SameModules(List<Module> a, List<Module> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (a[i].Name != b[i].Name) return false;
            return true;
        }

        private static string SetKey(List<Module> s)
        {
            StringBuilder sb = new StringBuilder();
            foreach (Module m in s) sb.Append(m.Name).Append('|');
            return sb.ToString();
        }

        public static CalcResult Compute(RoomInput room, ProjectSettings project, GotvSpec g, List<Module> catalog)
        {
            if (project == null) project = new ProjectSettings();
            if (g == null) g = new GotvSpec();
            if (catalog == null) catalog = DefaultCatalog();
            CultureInfo ci = CultureInfo.InvariantCulture;
            CalcResult r = new CalcResult();
            double tmK = room.TMinC + 273.0;

            r.K3 = K3FromAltitude(project.AltitudeM, project.K3Mode);
            r.Rho1 = g.Rho0 * (T0K / tmK) * r.K3;                                      // Г.5
            double vd = (room.DeltaVolume.HasValue && room.DeltaVolume.Value > 0.0) ? room.DeltaVolume.Value : room.Volume;
            r.Delta = room.OpeningsArea / vd;                                           // δ = ΣFн/Vр
            r.Pi = PiValue(room.OpeningsPosition);
            r.K2 = r.Pi * r.Delta * g.TauPod * Math.Sqrt(room.Height);                  // Г.6

            double cn = g.CnHeptane;
            double form = (g.Kind == GasKind.Liquefied)
                ? cn / (100.0 - cn)                                                     // Г.3
                : Math.Log(100.0 / (100.0 - cn));                                       // Г.4
            r.MpBase = room.Volume * r.Rho1 * (1.0 + r.K2) * form;
            r.K4 = K4Value(room.Fire);
            r.Mp = r.K4 * r.MpBase;                                                     // Г.7

            // Проверки негерметичности
            bool classA = room.Fire == FireKind.ClassAOther || room.Fire == FireKind.ClassAPaperRolls;
            if (classA && r.Delta > 0.001)
                r.Warnings.Add("δ = " + r.Delta.ToString("0.00000", ci) + " > 0.001 м^-1: для класса А не допускается (Г.7)");
            double? lim = LeakageLimit(room.Volume);
            if (lim.HasValue && r.Delta > lim.Value)
                r.Warnings.Add("δ = " + r.Delta.ToString("0.00000", ci) + " превышает допустимое " + lim.Value.ToString("0.0000", ci)
                    + " для объёма " + room.Volume.ToString("0.0", ci) + " м3 (табл. 9, п. 5.4.1.10)");

            // Mтр (Г.2): плотность остатка — из остаточного давления гидравлического расчёта
            if (room.PResidualAbsMpa.HasValue)
                r.RhoResidual = EstimateRhoResidual(room.PResidualAbsMpa.Value, room.TMinC, g);
            else
            {
                r.RhoResidual = 0.0;
                if (room.PipeVolume > 0.0)
                    r.Warnings.Add("Остаточное давление в трубах не задано (нужен гидравлический расчёт): Mтр принят 0");
            }
            r.Mtr = room.PipeVolume * r.RhoResidual;

            // Проверка п. 5.4.3.9: Vтр не более 80% объёма жидкой фазы расчётной массы ГОТВ
            if (room.PipeVolume > 0.0 && g.LiquidDensity > 0.0)
            {
                double vmax = 0.8 * r.Mp / g.LiquidDensity;
                if (room.PipeVolume > vmax)
                    r.Warnings.Add("Vтр = " + room.PipeVolume.ToString("0.0000", ci) + " м3 больше 80% объёма жидкой фазы ("
                        + vmax.ToString("0.0000", ci) + " м3), п. 5.4.3.9");
            }

            // Mг (Г.1) с итерацией по Mбn = Mб·n; выбор модулей
            List<Module> mods = new List<Module>();
            for (int it = 0; it < 6; it++)
            {
                double mbnIt = 0.0;
                foreach (Module m in mods) mbnIt += m.ResidualKg;
                double mgIt = K1 * (r.Mp + r.Mtr + mbnIt);
                Module m1 = SingleModuleFor(mgIt, catalog);
                List<Module> newMods = new List<Module>();
                List<List<Module>> cands = new List<List<Module>>();
                if (m1 != null)
                {
                    newMods.Add(m1);
                }
                else
                {
                    SelectionRule[] rules = { SelectionRule.MinCount, SelectionRule.MinTotal };
                    List<string> seen = new List<string>();
                    foreach (SelectionRule rule in rules)
                    {
                        List<Module> s = SelectModules(mgIt, catalog, 6, rule);
                        if (s.Count > 0 && !seen.Contains(SetKey(s)))
                        {
                            seen.Add(SetKey(s));
                            cands.Add(s);
                        }
                    }
                    if (room.ChosenModules != null)
                    {
                        foreach (string cname in room.ChosenModules)
                            foreach (Module cm in catalog)
                                if (cm.Name == cname) newMods.Add(cm);
                    }
                }
                r.Candidates = cands;
                if (it > 0 && SameModules(newMods, mods)) break;
                mods = newMods;
            }
            r.Mbn = 0.0;
            foreach (Module m in mods) r.Mbn += m.ResidualKg;
            r.Mg = K1 * (r.Mp + r.Mtr + r.Mbn);
            r.Modules = mods;

            if (mods.Count == 0)
            {
                r.NeedsEngineerDecision = true;
                double biggest = 0.0;
                foreach (Module m in catalog) if (m.FillKg > biggest) biggest = m.FillKg;
                r.Warnings.Add("Mг " + r.Mg.ToString("0.0", ci) + " кг больше самого крупного модуля (" + biggest.ToString("0", ci)
                    + " кг): набор модулей выбирает инженер (вариантов: " + r.Candidates.Count.ToString(ci) + ")");
            }
            else
            {
                double total = 0.0;
                bool allZero = true;
                foreach (Module m in mods)
                {
                    total += m.FillKg;
                    if (m.ResidualKg != 0.0) allZero = false;
                }
                if (room.ChosenModules != null && room.ChosenModules.Count > 0 && total < r.Mg)
                    r.Warnings.Add("Выбранный набор (" + total.ToString("0", ci) + " кг) меньше Mг = " + r.Mg.ToString("0.0", ci) + " кг");
                if (allZero)
                    r.Warnings.Add("Остаток в модуле Mб не задан (0): Mбn не учтён — взять из паспорта модуля");
            }
            return r;
        }
    }
}
