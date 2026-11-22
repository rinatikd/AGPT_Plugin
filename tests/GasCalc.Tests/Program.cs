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

        private static bool ExpectValidation(string label, Action action, string expectedCode)
        {
            try
            {
                action();
                Console.WriteLine("[VALIDATION FAIL] " + label + ": exception was not thrown");
                return false;
            }
            catch (ArgumentException ex)
            {
                bool ok = ex.Message.StartsWith(expectedCode, StringComparison.Ordinal);
                Console.WriteLine("[VALIDATION " + (ok ? "OK" : "FAIL") + "] " + label
                    + ": " + ex.Message);
                return ok;
            }
            catch (Exception ex)
            {
                Console.WriteLine("[VALIDATION FAIL] " + label + ": unexpected " + ex.GetType().Name);
                return false;
            }
        }

        private static int RunInputValidationTests()
        {
            int fails = 0;
            RoomInput valid = new RoomInput
            {
                Name = "Input validation fixture",
                Volume = 30.0,
                Height = 3.0,
                OpeningsArea = 0.015,
                TMinC = 20.0,
                Fire = FireKind.ClassAOther,
                OpeningsPosition = OpeningsPos.BottomAndTop,
                PipeVolume = 0.0
            };
            ProjectSettings project = new ProjectSettings();
            GotvSpec gotv = new GotvSpec();
            List<Module> catalog = GasCalc.DefaultCatalog();

            if (!ExpectValidation("null room", () => GasCalc.Compute(null, project, gotv, catalog), "ROOM_INPUT_REQUIRED")) fails++;
            if (!ExpectValidation("NaN volume", () =>
            {
                RoomInput x = Clone(valid); x.Volume = double.NaN;
                GasCalc.Compute(x, project, gotv, catalog);
            }, "ROOM_VOLUME_INVALID")) fails++;
            if (!ExpectValidation("zero height", () =>
            {
                RoomInput x = Clone(valid); x.Height = 0.0;
                GasCalc.Compute(x, project, gotv, catalog);
            }, "ROOM_HEIGHT_INVALID")) fails++;
            if (!ExpectValidation("below absolute zero", () =>
            {
                RoomInput x = Clone(valid); x.TMinC = -273.0;
                GasCalc.Compute(x, project, gotv, catalog);
            }, "ROOM_TEMPERATURE_INVALID")) fails++;
            if (!ExpectValidation("unknown openings enum", () =>
            {
                RoomInput x = Clone(valid); x.OpeningsPosition = (OpeningsPos)99;
                GasCalc.Compute(x, project, gotv, catalog);
            }, "OPENINGS_POSITION_INVALID")) fails++;
            if (!ExpectValidation("unknown fire enum", () =>
            {
                RoomInput x = Clone(valid); x.Fire = (FireKind)99;
                GasCalc.Compute(x, project, gotv, catalog);
            }, "FIRE_KIND_INVALID")) fails++;
            if (!ExpectValidation("altitude beyond K3 table", () =>
            {
                ProjectSettings x = new ProjectSettings(); x.AltitudeM = 2500.0;
                GasCalc.Compute(Clone(valid), x, gotv, catalog);
            }, "PROJECT_ALTITUDE_INVALID")) fails++;
            if (!ExpectValidation("zero residual pressure", () =>
            {
                RoomInput x = Clone(valid); x.PResidualAbsMpa = 0.0;
                GasCalc.Compute(x, project, gotv, catalog);
            }, "RESIDUAL_PRESSURE_INVALID")) fails++;
            if (!ExpectValidation("zero delta volume", () =>
            {
                RoomInput x = Clone(valid); x.DeltaVolume = 0.0;
                GasCalc.Compute(x, project, gotv, catalog);
            }, "DELTA_VOLUME_INVALID")) fails++;
            if (!ExpectValidation("empty module catalogue", () =>
                GasCalc.Compute(Clone(valid), project, gotv, new List<Module>()), "MODULE_CATALOG_EMPTY")) fails++;
            if (!ExpectValidation("NaN module fill", () =>
            {
                List<Module> x = new List<Module> { new Module("Invalid fill", double.NaN, 0.0) };
                GasCalc.Compute(Clone(valid), project, gotv, x);
            }, "MODULE_FILL_INVALID")) fails++;
            if (!ExpectValidation("negative module residual", () =>
            {
                List<Module> x = new List<Module> { new Module("Invalid residual", 40.0, -1.0) };
                GasCalc.Compute(Clone(valid), project, gotv, x);
            }, "MODULE_RESIDUAL_INVALID")) fails++;
            if (!ExpectValidation("unknown engineer module choice", () =>
            {
                RoomInput x = Clone(valid); x.ChosenModules = new List<string> { "Not in verified catalogue" };
                GasCalc.Compute(x, project, gotv, catalog);
            }, "CHOSEN_MODULE_UNKNOWN")) fails++;
            if (!ExpectValidation("direct K3 helper rejects NaN altitude", () =>
                GasCalc.K3FromAltitude(double.NaN, K3Mode.Interpolate), "PROJECT_ALTITUDE_INVALID")) fails++;
            if (!ExpectValidation("direct Pi helper rejects unknown enum", () =>
                GasCalc.PiValue((OpeningsPos)99), "OPENINGS_POSITION_INVALID")) fails++;
            if (!ExpectValidation("direct K4 helper rejects unknown enum", () =>
                GasCalc.K4Value((FireKind)99), "FIRE_KIND_INVALID")) fails++;
            if (!ExpectValidation("residual density rejects zero pressure", () =>
                GasCalc.EstimateRhoResidual(0.0, 20.0, gotv), "RESIDUAL_PRESSURE_INVALID")) fails++;

            // A valid room still computes a finite result after strict validation.
            CalcResult good = GasCalc.Compute(Clone(valid), project, gotv, catalog);
            bool finite = !double.IsNaN(good.Mg) && !double.IsInfinity(good.Mg)
                && good.Mg > 0.0;
            Console.WriteLine("[VALIDATION " + (finite ? "OK" : "FAIL") + "] valid fixture returns finite Mg");
            if (!finite) fails++;
            return fails;
        }

        private static RoomInput Clone(RoomInput source)
        {
            return new RoomInput
            {
                Name = source.Name,
                Volume = source.Volume,
                Height = source.Height,
                OpeningsArea = source.OpeningsArea,
                DeltaVolume = source.DeltaVolume,
                OpeningsPosition = source.OpeningsPosition,
                TMinC = source.TMinC,
                Fire = source.Fire,
                PipeVolume = source.PipeVolume,
                PResidualAbsMpa = source.PResidualAbsMpa,
                ChosenModules = source.ChosenModules == null ? null : new List<string>(source.ChosenModules)
            };
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
            fails += RunInputValidationTests();
            Console.WriteLine(fails == 0 ? "ИТОГО: все проверки пройдены" : "ИТОГО: расхождений: " + fails.ToString(ci));
            return fails == 0 ? 0 : 1;
        }
    }
}
