// GasCalc.cs — расчёт массы ГОТВ по СП РК 2.02-102-2022, Приложение Г.
// Точный порт tools/gas_calc.py. Спецификация: docs/CALC_SPEC_RK.md.
// Чистая логика без зависимостей от AutoCAD; проверяется tests/GasCalc.Tests по tests/golden.
using System;
using System.Collections.Generic;
using System.Globalization;

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
        public double? PiOverride;
        public double TMinC = 20.0;                // минимальная температура, °C
        public double AltitudeM = 347.0;           // Астана
        public K3Mode K3Mode = K3Mode.LowerRow;
        public double? K3Override;
        public FireKind Fire = FireKind.ClassAOther;
        public double? K4Override;
        public double PipeVolume;                  // Vтр, м3
        public double? RhoResidual;                // плотность остатка ГОТВ, кг/м3
        public double? PResidualAbsMpa;            // остаточное давление в трубах (абс.), МПа
        public double K1 = 1.05;
        public SelectionRule Rule = SelectionRule.MinCount;
    }

    public class CalcResult
    {
        public double Rho1, K3, Delta, K2, Pi, K4 = 1.0;
        public double MpBase, Mp, RhoResidual, Mtr, Mbn, Mg;
        public List<Module> Modules = new List<Module>();
        public List<string> Warnings = new List<string>();
    }

    public static class GasCalc
    {
        public const double T0K = 293.0;

        // Таблица Д.11: высота над уровнем моря (м) -> K3
        private static readonly double[] K3Alt = { 0, 300, 600, 900, 1200, 1500, 1800, 2100 };
        private static readonly double[] K3Val = { 1.00, 0.96, 0.93, 0.89, 0.86, 0.82, 0.78, 0.75 };

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

        // Оценка плотности остатка: идеальный газ при давлении p, не выше плотности насыщенных паров.
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

        // Подбор набора модулей с суммарной заправкой >= mgRequired. Предварительное правило, утверждает инженер.
        // MinCount: сначала минимальное число модулей, затем минимальная суммарная заправка (по умолчанию).
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

        private static bool SameModules(List<Module> a, List<Module> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (a[i].Name != b[i].Name) return false;
            return true;
        }

        public static CalcResult Compute(RoomInput room, GotvSpec g, List<Module> catalog)
        {
            if (g == null) g = new GotvSpec();
            if (catalog == null) catalog = DefaultCatalog();
            CultureInfo ci = CultureInfo.InvariantCulture;
            CalcResult r = new CalcResult();
            double tmK = room.TMinC + 273.0;

            r.K3 = room.K3Override.HasValue ? room.K3Override.Value : K3FromAltitude(room.AltitudeM, room.K3Mode);
            r.Rho1 = g.Rho0 * (T0K / tmK) * r.K3;                                      // Г.5
            double vd = (room.DeltaVolume.HasValue && room.DeltaVolume.Value > 0.0) ? room.DeltaVolume.Value : room.Volume;
            r.Delta = room.OpeningsArea / vd;                                           // δ = ΣFн/Vр
            r.Pi = room.PiOverride.HasValue ? room.PiOverride.Value : PiValue(room.OpeningsPosition);
            r.K2 = r.Pi * r.Delta * g.TauPod * Math.Sqrt(room.Height);                  // Г.6

            double cn = g.CnHeptane;
            double form = (g.Kind == GasKind.Liquefied)
                ? cn / (100.0 - cn)                                                     // Г.3
                : Math.Log(100.0 / (100.0 - cn));                                       // Г.4
            r.MpBase = room.Volume * r.Rho1 * (1.0 + r.K2) * form;
            r.K4 = room.K4Override.HasValue ? room.K4Override.Value : K4Value(room.Fire);
            r.Mp = r.K4 * r.MpBase;                                                     // Г.7

            bool classA = room.Fire == FireKind.ClassAOther || room.Fire == FireKind.ClassAPaperRolls;
            if (classA && r.Delta > 0.001)
                r.Warnings.Add("δ = " + r.Delta.ToString("0.00000", ci) + " > 0.001 м^-1: для класса А не допускается (Г.7)");

            // Mтр (Г.2)
            if (room.RhoResidual.HasValue)
                r.RhoResidual = room.RhoResidual.Value;
            else if (room.PResidualAbsMpa.HasValue)
                r.RhoResidual = EstimateRhoResidual(room.PResidualAbsMpa.Value, room.TMinC, g);
            else
            {
                r.RhoResidual = 0.0;
                if (room.PipeVolume > 0.0)
                    r.Warnings.Add("ρготв (остаток в трубах) не задан: Mтр принят 0 — задайте давление остатка или плотность");
            }
            r.Mtr = room.PipeVolume * r.RhoResidual;

            // Mг (Г.1) с итерацией по Mбn = Mб·n
            List<Module> mods = new List<Module>();
            for (int it = 0; it < 6; it++)
            {
                double mbnIt = 0.0;
                foreach (Module m in mods) mbnIt += m.ResidualKg;
                double mgIt = room.K1 * (r.Mp + r.Mtr + mbnIt);
                List<Module> newMods = SelectModules(mgIt, catalog, 6, room.Rule);
                if (it > 0 && SameModules(newMods, mods)) break;
                mods = newMods;
            }
            r.Mbn = 0.0;
            foreach (Module m in mods) r.Mbn += m.ResidualKg;
            r.Mg = room.K1 * (r.Mp + r.Mtr + r.Mbn);
            r.Modules = mods;

            if (mods.Count > 1)
                r.Warnings.Add("Mг " + r.Mg.ToString("0.0", ci) + " кг больше одного модуля: набор из " + mods.Count.ToString(ci) + " модулей (правило подбора предварительное)");
            bool allZero = mods.Count > 0;
            foreach (Module m in mods) if (m.ResidualKg != 0.0) allZero = false;
            if (allZero)
                r.Warnings.Add("Остаток в модуле Mб не задан (0): Mбn не учтён — взять из паспорта модуля");
            return r;
        }
    }
}
