// Тест движка GasCalc по контрольным примерам эталона МГИМО (tests/golden/mgimo_calc_golden.json).
// Запуск: dotnet run --project tests/GasCalc.Tests   (код возврата 0 — все проверки пройдены)
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using AGPT.Calc;

namespace AGPT.Calc.Tests
{
    public static class Program
    {
        private const double TolMp = 0.01;     // 1% на Mр
        private const double TolRho = 0.002;   // 0.2% на ρ1

        private static string FindGolden()
        {
            string dir = AppContext.BaseDirectory;
            for (int i = 0; i < 12 && dir != null; i++)
            {
                string p = Path.Combine(dir, "tests", "golden", "mgimo_calc_golden.json");
                if (File.Exists(p)) return p;
                DirectoryInfo di = Directory.GetParent(dir);
                dir = di == null ? null : di.FullName;
            }
            return null;
        }

        private static int RefModule(int mgTable)
        {
            // Mг из таблицы расчёта эталона -> модуль, выбранный по спецификации
            switch (mgTable)
            {
                case 28: return 28;
                case 182: return 184;
                case 51: return 50;
                case 42: return 42;
                case 50: return 50;
                case 191: return 192;
                default: return -1;
            }
        }

        public static int Main(string[] args)
        {
            CultureInfo ci = CultureInfo.InvariantCulture;
            string path = FindGolden();
            if (path == null)
            {
                Console.WriteLine("Не найден tests/golden/mgimo_calc_golden.json (запускайте из репозитория).");
                return 2;
            }
            int fails = 0;
            using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path)))
            {
                JsonElement cases = doc.RootElement.GetProperty("cases");
                foreach (JsonElement c in cases.EnumerateArray())
                {
                    string roomName = c.GetProperty("room").GetString();
                    string key = roomName.Split(' ')[0];
                    JsonElement inp = c.GetProperty("input");
                    JsonElement exp = c.GetProperty("expected");

                    double s = inp.GetProperty("S").GetDouble();
                    double h = inp.GetProperty("H").GetDouble();
                    RoomInput room = new RoomInput();
                    room.Name = roomName;
                    room.Volume = s * h;
                    room.Height = h;
                    room.OpeningsArea = inp.GetProperty("Fh").GetDouble();
                    room.TMinC = inp.GetProperty("Tm_C").GetDouble();
                    room.PipeVolume = inp.GetProperty("Vtr").GetDouble();
                    room.Fire = FireKind.ClassAOther;      // П = 0,65 (проёмы внизу и вверху) и K4 = 1,5 — значения нормы

                    // Входы, не выраженные напрямую в таблице «Исходные данные» эталона
                    if (key == "1117") { room.Volume = 57.0; room.Height = 3.85; room.DeltaVolume = 12.0 * 3.85; }
                    if (key == "1435") { room.Volume = 221.0; room.DeltaVolume = 221.0; }

                    CalcResult r = GasCalc.Compute(room, new ProjectSettings(), new GotvSpec(), null);
                    double expMp = exp.GetProperty("Mp").GetDouble();
                    double expRho = exp.GetProperty("rho1").GetDouble();
                    int expMg = (int)exp.GetProperty("Mg").GetDouble();

                    double dev = r.Mp / expMp - 1.0;
                    bool okMp = Math.Abs(dev) <= TolMp || (key == "1016" && Math.Abs(dev) <= 0.006);
                    bool okRho = Math.Abs(r.Rho1 / expRho - 1.0) <= TolRho;
                    double gotMod = 0.0;
                    foreach (Module m in r.Modules) gotMod += m.FillKg;
                    bool okMod = ((int)Math.Round(gotMod)) == RefModule(expMg);
                    bool ok = okMp && okRho && okMod;
                    if (!ok) fails++;
                    Console.WriteLine(roomName.PadRight(30) + " Mр=" + r.Mp.ToString("0.00", ci)
                        + " эт=" + expMp.ToString("0.00", ci) + " откл=" + (dev * 100.0).ToString("+0.00;-0.00", ci)
                        + "% модуль=" + ((int)Math.Round(gotMod)).ToString(ci) + "/" + RefModule(expMg).ToString(ci)
                        + " " + (ok ? "OK" : "FAIL"));
                }
            }

            // Архив 1435 по норме (K4 = 2,25): набор модулей выбирает инженер
            RoomInput arch = new RoomInput();
            arch.Name = "1435 архив (K4 = 2,25)";
            arch.Volume = 221.0; arch.Height = 4.3; arch.OpeningsArea = 0.017; arch.TMinC = 20.0;
            arch.Fire = FireKind.ClassAPaperRolls; arch.PipeVolume = 0.0033;
            CalcResult ra = GasCalc.Compute(arch, new ProjectSettings(), new GotvSpec(), null);
            Console.WriteLine(arch.Name + ": Mр=" + ra.Mp.ToString("0.0", ci) + " Mг=" + ra.Mg.ToString("0.0", ci)
                + " нужен выбор инженера: " + (ra.NeedsEngineerDecision ? "да" : "нет") + ", вариантов: " + ra.Candidates.Count.ToString(ci));
            if (!ra.NeedsEngineerDecision || ra.Candidates.Count == 0) fails++;
            Console.WriteLine(fails == 0 ? "ИТОГО: все проверки пройдены" : "ИТОГО: расхождений: " + fails.ToString(ci));
            return fails == 0 ? 0 : 1;
        }
    }
}
