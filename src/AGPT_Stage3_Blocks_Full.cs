// =============================================================================
// AGPT Plugin for AutoCAD — Stage 3 FULL
// 1. ZONE  2. Modules  3. Nozzles  4. Automation  5. KSID/SDU/RVD  6. Pipes+XData
// .NET Framework 4.8 / AutoCAD 2018–2025
// Эталон: AGPT_BlockLibrary_v1.json
// =============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Colors;

[assembly: CommandClass(typeof(AGPT.Stage3.AgptCommandsFull))]

namespace AGPT.Stage3
{
    // =========================================================================
    // 0. Константы
    // =========================================================================
    public static class C
    {
        public const string App = "AGPT";
        public const string LEq = "AGPT-EQ", LZone = "AGPT-ZONE", LMark = "AGPT-MARK";
        public const string LPipe = "AGPT-PIPE", LKsid = "AGPT-KSID", LCable = "AGPT-CABLE";

        public static readonly Dictionary<string, short> LayerColor = new Dictionary<string, short>
        {
            { LEq, 1 }, { LZone, 8 }, { LMark, 7 }, { LPipe, 5 }, { LKsid, 6 }, { LCable, 3 }
        };

        public static readonly Dictionary<string, (short color, double lw, string desc)> PipeStyle =
            new Dictionary<string, (short, double, string)>
            {
                { "main",   (1, 0.35, "Магистраль") },
                { "branch", (5, 0.25, "Ветка") },
                { "drop",   (4, 0.18, "Спуск к насадку") }
            };
    }

    // =========================================================================
    // XData + Layers
    // =========================================================================
    public static class XD
    {
        public static void EnsureApp(Database db, Transaction tr)
        {
            var rat = (RegAppTable)tr.GetObject(db.RegAppTableId, OpenMode.ForRead);
            if (rat.Has(C.App)) return;
            rat.UpgradeOpen();
            var r = new RegAppTableRecord { Name = C.App };
            rat.Add(r); tr.AddNewlyCreatedDBObject(r, true);
        }

        public static void Set(Entity e, params (string k, string v)[] pairs)
        {
            var list = new List<TypedValue> { new TypedValue((int)DxfCode.ExtendedDataRegAppName, C.App) };
            var old = e.GetXDataForApplication(C.App);
            var keep = new HashSet<string>(pairs.Select(p => p.k));
            if (old != null)
                foreach (TypedValue tv in old)
                    if (tv.TypeCode == (int)DxfCode.ExtendedDataAsciiString)
                    {
                        var s = tv.Value.ToString();
                        var eq = s.IndexOf('=');
                        if (eq > 0 && !keep.Contains(s.Substring(0, eq)))
                            list.Add(tv);
                    }
            foreach (var (k, v) in pairs)
                list.Add(new TypedValue((int)DxfCode.ExtendedDataAsciiString, k + "=" + v));
            e.XData = new ResultBuffer(list.ToArray());
        }

        public static string Get(Entity e, string key)
        {
            var rb = e.GetXDataForApplication(C.App);
            if (rb == null) return null;
            string p = key + "=";
            foreach (TypedValue tv in rb)
                if (tv.TypeCode == (int)DxfCode.ExtendedDataAsciiString)
                {
                    var s = tv.Value.ToString();
                    if (s.StartsWith(p)) return s.Substring(p.Length);
                }
            return null;
        }

        public static double GetD(Entity e, string key, double def = 0)
        {
            var s = Get(e, key);
            if (string.IsNullOrEmpty(s)) return def;
            return double.TryParse(s.Replace(',', '.'), out var v) ? v : def;
        }
    }

    public static class Layers
    {
        public static void EnsureAll(Database db, Transaction tr)
        {
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            foreach (var kv in C.LayerColor)
            {
                if (lt.Has(kv.Key)) continue;
                lt.UpgradeOpen();
                var ltr = new LayerTableRecord
                {
                    Name = kv.Key,
                    Color = Color.FromColorIndex(ColorMethod.ByAci, kv.Value)
                };
                lt.Add(ltr); tr.AddNewlyCreatedDBObject(ltr, true);
            }
        }
    }

    // =========================================================================
    // 1–2. ZONE + MODULE models (кратко, см. Core)
    // =========================================================================
    public class ZoneData
    {
        public string Guid { get; set; } = System.Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "";
        public double Vp { get; set; }
        public double H { get; set; } = 3;
        public double Fh { get; set; }
        public double PParam { get; set; } = 0.4;
        public double Tm { get; set; } = 20;
        public double Cn { get; set; } = 7.2;
        public double Rho0 { get; set; } = 7.28;
        public double K4 { get; set; } = 1.0;
        public string Gotv { get; set; } = "HFC-227ea";
        public string InstallType { get; set; } = "with_pipes";
        public double Mp { get; set; }
        public double Mg { get; set; }
        public double Fc { get; set; }
    }

    public class ModuleData
    {
        public string Guid { get; set; } = System.Guid.NewGuid().ToString("N");
        public string Pos { get; set; } = "";
        public string ZoneGuid { get; set; } = "";
        public string Mark { get; set; } = "";
        public double VBal { get; set; } = 40;
        public string Gotv { get; set; } = "HFC-227ea";
        public double MFill { get; set; } = 20;
        public string Role { get; set; } = "Р";

        public static readonly Dictionary<int, (double fill, string mark)> Cat = new Dictionary<int, (double, string)>
        {
            { 40, (20, "Flamex 66-40-33") }, { 60, (30, "Flamex 66-60-33") },
            { 80, (40, "Flamex 66-80-33") }, { 100, (50, "Flamex 66-100-33") },
            { 120, (60, "Flamex 66-120-33") }
        };

        public static ModuleData Create(int vol, string role = "Р")
        {
            var m = new ModuleData { VBal = vol, Role = role };
            if (Cat.TryGetValue(vol, out var i)) { m.MFill = i.fill; m.Mark = i.mark; }
            return m;
        }
    }

    // =========================================================================
    // 3. NOZZLE
    // =========================================================================
    public class NozzleData
    {
        public string Guid { get; set; } = System.Guid.NewGuid().ToString("N");
        public string Pos { get; set; } = "";
        public string ZoneGuid { get; set; } = "";
        public double Orifice { get; set; } = 15;   // мм
        public double Cover { get; set; } = 3.0;    // м
        public string Orient { get; set; } = "вниз"; // вниз | вбок
        public string Mark { get; set; } = "PT-15A-15-1/2\"";
    }

    // =========================================================================
    // 4. AUTOMATION
    // =========================================================================
    public class AutoDeviceData
    {
        public string Guid { get; set; } = System.Guid.NewGuid().ToString("N");
        public string Pos { get; set; } = "";
        public string Kind { get; set; } = "";      // MPT | UDP | TABLO | IP | OPOP
        public string Mark { get; set; } = "";
        public string Code { get; set; } = "";
        public string Addr { get; set; } = "";
        public string Line { get; set; } = "1";
        public string Parent { get; set; } = "";
        public string Text { get; set; } = "";      // для табло
        public string ZoneGuid { get; set; } = "";

        public static readonly Dictionary<string, (string mark, string code, string label)> Catalog =
            new Dictionary<string, (string, string, string)>
            {
                { "MPT",   ("МПТ-1-R3",        "541-801-1604-0011", "МПТ") },
                { "UDP",   ("УДП 513-11-A-R3", "248-310-0108",      "УДП") },
                { "IP",    ("ИП 212-64-R3",    "248-302-0228",      "ИП") },
                { "TABLO_UHODI",   ("ОПОП 1-R3", "248-304-0174", "Газ! Уходи!") },
                { "TABLO_NEVHODI", ("ОПОП 1-R3", "248-304-0174", "Газ! Не входи!") },
                { "TABLO_AVTO",    ("ОПОП 1-R3", "248-304-0174", "Автоматика выключена") },
                { "OPOP",  ("ОПОП 124-R3",     "248-304-0232",      "ОПОП") }
            };
    }

    // =========================================================================
    // 5. KSID / SDU / RVD
    // =========================================================================
    public class KsidData
    {
        public string Guid { get; set; } = System.Guid.NewGuid().ToString("N");
        public string Pos { get; set; } = "";
        public double Fc { get; set; } = 0.06;      // м²
        public double POpen { get; set; } = 0.5;    // кПа
        public string ZoneGuid { get; set; } = "";
    }

    public class AccData
    {
        public string Guid { get; set; } = System.Guid.NewGuid().ToString("N");
        public string Pos { get; set; } = "";
        public string Kind { get; set; } = "";      // RVD | SDU | ZPU_EM
        public string Mark { get; set; } = "";
        public string Code { get; set; } = "";
        public double LengthMm { get; set; } = 650; // для RVD

        public static readonly Dictionary<string, (string mark, string code)> Cat =
            new Dictionary<string, (string, string)>
            {
                { "RVD",    ("RG-32G2/650", "248-305-1305") },
                { "SDU",    ("СДУ-М",       "248-305-1304") },
                { "ZPU_EM", ("ОДО 90N",     "248-305-1302") }
            };
    }

    // =========================================================================
    // 6. PIPE segment
    // =========================================================================
    public class PipeSegData
    {
        public string SegId { get; set; } = System.Guid.NewGuid().ToString("N");
        public string SegType { get; set; } = "branch"; // main | branch | drop
        public double Dy { get; set; } = 20;            // мм
        public double L { get; set; }                   // м
        public string ZoneGuid { get; set; } = "";
        public double Dp { get; set; }                  // бар (гидравлика)
        public double Q { get; set; }                   // кг/с
        public double VFlow { get; set; }               // м/с
    }

    // =========================================================================
    // Расчёт массы (СП 485)
    // =========================================================================
    public static class MassCalc
    {
        public static void Compute(ZoneData z, double vTr = 0.002, double k1 = 1.05, double tPod = 10)
        {
            double rho1 = z.Rho0 * (293.15 / (z.Tm + 273.15));
            double delta = z.Vp > 0 ? z.Fh / z.Vp : 0;
            double k2 = z.PParam * tPod * Math.Sqrt(Math.Max(0, z.H * delta));
            if (double.IsNaN(k2) || double.IsInfinity(k2)) k2 = 0;
            z.Mp = z.Vp * rho1 * (1 + k2) * Math.Log(100.0 / (100.0 - z.Cn));
            double mTr = vTr * rho1 * 2.5;
            z.Mg = k1 * (z.Mp * z.K4 + mTr);
        }

        public static int SuggestVol(double mg)
        {
            foreach (var v in new[] { 40, 60, 80, 100, 120 })
                if (ModuleData.Cat[v].fill >= mg) return v;
            return 120;
        }
    }

    // =========================================================================
    // КСИД (СП 485, Прил. Ж) — упрощённо
    // =========================================================================
    public static class KsidCalc
    {
        /// <summary>Fc = max(0, K2*K3*Mp/(ρ1*t*v) − ΣF)</summary>
        public static double Compute(ZoneData z, double tPod = 10, double pPr = 0.005, double pa = 0.1013, double rhoAir = 1.2, double k2 = 1.2, double k3 = 1.0)
        {
            double rho1 = z.Rho0 * (293.15 / (z.Tm + 273.15));
            double v = 0.7 * Math.Sqrt(2 * pPr * 1e6 / rhoAir);
            if (v < 1e-6) return 0;
            double fReq = k2 * k3 * z.Mp / (rho1 * tPod * v);
            return Math.Max(0, fReq - z.Fh);
        }

        public static double SuggestKsidSize(double fc)
        {
            if (fc <= 0) return 0;
            if (fc <= 0.06) return 0.06;
            if (fc <= 0.12) return 0.12;
            return 0.18;
        }
    }

    // =========================================================================
    // Утилита: простая графика УГО
    // =========================================================================
    public static class Ugo
    {
        public static ObjectId Rect(BlockTableRecord btr, Transaction tr, Point3d c, double w, double h, string layer)
        {
            var pl = new Polyline(4);
            pl.AddVertexAt(0, new Point2d(c.X - w / 2, c.Y), 0, 0, 0);
            pl.AddVertexAt(1, new Point2d(c.X + w / 2, c.Y), 0, 0, 0);
            pl.AddVertexAt(2, new Point2d(c.X + w / 2, c.Y + h), 0, 0, 0);
            pl.AddVertexAt(3, new Point2d(c.X - w / 2, c.Y + h), 0, 0, 0);
            pl.Closed = true; pl.Layer = layer;
            btr.AppendEntity(pl); tr.AddNewlyCreatedDBObject(pl, true);
            return pl.ObjectId;
        }

        public static ObjectId Circle(BlockTableRecord btr, Transaction tr, Point3d c, double r, string layer)
        {
            var cir = new Circle(c, Vector3d.ZAxis, r) { Layer = layer };
            btr.AppendEntity(cir); tr.AddNewlyCreatedDBObject(cir, true);
            return cir.ObjectId;
        }

        public static ObjectId Label(BlockTableRecord btr, Transaction tr, Point3d c, string text, double h, string layer)
        {
            var t = new DBText
            {
                Position = c, Height = h, TextString = text, Layer = layer,
                HorizontalMode = TextHorizontalMode.TextCenter,
                VerticalMode = TextVerticalMode.TextVerticalMid,
                AlignmentPoint = c
            };
            btr.AppendEntity(t); tr.AddNewlyCreatedDBObject(t, true);
            return t.ObjectId;
        }

        public static ObjectId Triangle(BlockTableRecord btr, Transaction tr, Point3d c, double s, string layer)
        {
            var pl = new Polyline(3);
            pl.AddVertexAt(0, new Point2d(c.X, c.Y + s), 0, 0, 0);
            pl.AddVertexAt(1, new Point2d(c.X - s * 0.87, c.Y - s * 0.5), 0, 0, 0);
            pl.AddVertexAt(2, new Point2d(c.X + s * 0.87, c.Y - s * 0.5), 0, 0, 0);
            pl.Closed = true; pl.Layer = layer;
            btr.AppendEntity(pl); tr.AddNewlyCreatedDBObject(pl, true);
            return pl.ObjectId;
        }
    }

    // =========================================================================
    // КОМАНДЫ — полный Этап 3
    // =========================================================================
    public class AgptCommandsFull
    {
        // ----- 1. ZONE: полилиния на плане → S → H → имя → Vp -----
        // Pline = выбрать существующий контур; Draw = обвести точками; Rect = прямоугольник
        [CommandMethod("AGPT_ZONE")]
        [CommandMethod("AGPT_ZONE_PLINE")]
        public void Zone()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            var db = doc.Database; var ed = doc.Editor;

            // Режим контура
            var modeOpts = new PromptKeywordOptions(
                "\nКонтур помещения [Pline/Draw/Rect] <Pline>: ");
            modeOpts.Keywords.Add("Pline");
            modeOpts.Keywords.Add("Draw");
            modeOpts.Keywords.Add("Rect");
            modeOpts.Keywords.Default = "Pline";
            modeOpts.AllowNone = true;
            var mode = ed.GetKeywords(modeOpts);
            string modeStr = (mode.Status == PromptStatus.OK) ? mode.StringResult : "Pline";
            bool useRect = modeStr == "Rect";
            bool useDraw = modeStr == "Draw";

            // Единицы плана: мм (типичный DWG) или метры
            var unitOpts = new PromptKeywordOptions("\nЕдиницы плана [mm/m] <mm>: ");
            unitOpts.Keywords.Add("mm");
            unitOpts.Keywords.Add("m");
            unitOpts.Keywords.Default = "mm";
            unitOpts.AllowNone = true;
            var uo = ed.GetKeywords(unitOpts);
            bool planInMm = !(uo.Status == PromptStatus.OK && uo.StringResult == "m");
            double areaToM2 = planInMm ? 1e-6 : 1.0; // мм²→м² или м²→м²

            ObjectId plId = ObjectId.Null;
            double areaDu = 0;
            Point3d labelPt = Point3d.Origin;

            if (useRect)
            {
                var p1 = ed.GetPoint("\nПервый угол зоны: ");
                if (p1.Status != PromptStatus.OK) return;
                var co = new PromptCornerOptions("\nПротивоположный угол: ", p1.Value);
                var p2 = ed.GetCorner(co);
                if (p2.Status != PromptStatus.OK) return;

                using (var tr = db.TransactionManager.StartTransaction())
                {
                    Layers.EnsureAll(db, tr); XD.EnsureApp(db, tr);
                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                    double x0 = Math.Min(p1.Value.X, p2.Value.X), y0 = Math.Min(p1.Value.Y, p2.Value.Y);
                    double x1 = Math.Max(p1.Value.X, p2.Value.X), y1 = Math.Max(p1.Value.Y, p2.Value.Y);
                    areaDu = Math.Abs((x1 - x0) * (y1 - y0));
                    var pl = new Polyline(4);
                    pl.AddVertexAt(0, new Point2d(x0, y0), 0, 0, 0);
                    pl.AddVertexAt(1, new Point2d(x1, y0), 0, 0, 0);
                    pl.AddVertexAt(2, new Point2d(x1, y1), 0, 0, 0);
                    pl.AddVertexAt(3, new Point2d(x0, y1), 0, 0, 0);
                    pl.Closed = true;
                    pl.Layer = C.LZone;
                    pl.ColorIndex = 8;
                    ms.AppendEntity(pl); tr.AddNewlyCreatedDBObject(pl, true);
                    plId = pl.ObjectId;
                    labelPt = new Point3d((x0 + x1) / 2, (y0 + y1) / 2, 0);
                    tr.Commit();
                }
            }
            else if (useDraw)
            {
                // Обводка помещения точками (Enter / пробел — замкнуть)
                var pts = new List<Point2d>();
                Point3d? prev = null;
                for (; ; )
                {
                    PromptPointOptions ppo;
                    if (pts.Count == 0)
                        ppo = new PromptPointOptions("\nПервая точка контура: ");
                    else if (pts.Count == 1)
                        ppo = new PromptPointOptions("\nСледующая точка: ") { UseBasePoint = true, BasePoint = prev.Value };
                    else
                    {
                        ppo = new PromptPointOptions(
                            "\nСледующая точка (Enter — замкнуть контур): ")
                        {
                            UseBasePoint = true,
                            BasePoint = prev.Value,
                            AllowNone = true
                        };
                    }
                    var pr = ed.GetPoint(ppo);
                    if (pr.Status == PromptStatus.None)
                    {
                        if (pts.Count < 3)
                        {
                            ed.WriteMessage("\n[ZONE] Нужно минимум 3 точки.");
                            return;
                        }
                        break;
                    }
                    if (pr.Status != PromptStatus.OK) return;
                    pts.Add(new Point2d(pr.Value.X, pr.Value.Y));
                    prev = pr.Value;
                }

                using (var tr = db.TransactionManager.StartTransaction())
                {
                    Layers.EnsureAll(db, tr); XD.EnsureApp(db, tr);
                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                    var pl = new Polyline(pts.Count);
                    for (int i = 0; i < pts.Count; i++)
                        pl.AddVertexAt(i, pts[i], 0, 0, 0);
                    pl.Closed = true;
                    pl.Layer = C.LZone;
                    pl.ColorIndex = 8;
                    ms.AppendEntity(pl); tr.AddNewlyCreatedDBObject(pl, true);
                    plId = pl.ObjectId;
                    try { areaDu = Math.Abs(pl.Area); }
                    catch
                    {
                        ed.WriteMessage("\n[ZONE] Не удалось вычислить площадь контура.");
                        tr.Abort();
                        return;
                    }
                    try
                    {
                        var ext = pl.GeometricExtents;
                        labelPt = new Point3d(
                            (ext.MinPoint.X + ext.MaxPoint.X) / 2,
                            (ext.MinPoint.Y + ext.MaxPoint.Y) / 2, 0);
                    }
                    catch { labelPt = new Point3d(pts[0].X, pts[0].Y, 0); }
                    tr.Commit();
                }
            }
            else
            {
                // Выбор существующей замкнутой полилинии (контур помещения)
                var peo = new PromptEntityOptions(
                    "\nУкажите замкнутую полилинию контура помещения: ")
                {
                    AllowNone = false
                };
                peo.SetRejectMessage("\nНужна полилиния (LWPOLYLINE).");
                peo.AddAllowedClass(typeof(Polyline), exactMatch: true);
                var per = ed.GetEntity(peo);
                if (per.Status != PromptStatus.OK) return;

                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var pl = tr.GetObject(per.ObjectId, OpenMode.ForRead) as Polyline;
                    if (pl == null)
                    {
                        ed.WriteMessage("\n[ZONE] Это не полилиния.");
                        return;
                    }
                    if (!pl.Closed)
                        ed.WriteMessage("\n[ZONE] Полилиния не замкнута — площадь по Area (как замкнутая).");
                    try { areaDu = Math.Abs(pl.Area); }
                    catch
                    {
                        ed.WriteMessage("\n[ZONE] Не удалось вычислить площадь. Замкните контур.");
                        return;
                    }
                    if (areaDu < 1e-9)
                    {
                        ed.WriteMessage("\n[ZONE] Нулевая площадь.");
                        return;
                    }
                    plId = pl.ObjectId;
                    try
                    {
                        var ext = pl.GeometricExtents;
                        labelPt = new Point3d(
                            (ext.MinPoint.X + ext.MaxPoint.X) / 2,
                            (ext.MinPoint.Y + ext.MaxPoint.Y) / 2, 0);
                    }
                    catch { labelPt = pl.StartPoint; }
                    tr.Commit();
                }
            }

            if (plId.IsNull) return;
            if (areaDu < 1e-9)
            {
                ed.WriteMessage("\n[ZONE] Нулевая площадь.");
                return;
            }

            double areaM2 = areaDu * areaToM2;
            ed.WriteMessage(string.Format(
                "\n[ZONE] Площадь контура S = {0:F2} м²  (в единицах чертежа {1:F0})",
                areaM2, areaDu));

            // Высота помещения
            var ho = new PromptDoubleOptions("\nВысота помещения H, м <2.4>: ")
            {
                DefaultValue = 2.4,
                UseDefaultValue = true,
                AllowNegative = false,
                AllowZero = false
            };
            var hr = ed.GetDouble(ho);
            double H = (hr.Status == PromptStatus.OK) ? hr.Value : 2.4;

            // Название
            var nm = ed.GetString("\nНазвание помещения: ");
            if (nm.Status != PromptStatus.OK || string.IsNullOrWhiteSpace(nm.StringResult))
            {
                ed.WriteMessage("\n[ZONE] Отменено (нет имени).");
                return;
            }
            string name = nm.StringResult.Trim();

            // Негерметичность (опционально)
            var fho = new PromptDoubleOptions("\nПлощадь негерметичности FH, м² <0.011>: ")
            {
                DefaultValue = 0.011,
                UseDefaultValue = true,
                AllowNegative = false
            };
            var fhr = ed.GetDouble(fho);
            double fh = (fhr.Status == PromptStatus.OK) ? fhr.Value : 0.011;

            double Vp = areaM2 * H;

            using (var tr = db.TransactionManager.StartTransaction())
            {
                Layers.EnsureAll(db, tr); XD.EnsureApp(db, tr);
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                var pl = tr.GetObject(plId, OpenMode.ForWrite) as Polyline;
                if (pl == null) { ed.WriteMessage("\n[ZONE] Контур потерян."); return; }

                if (!pl.Closed)
                {
                    try { pl.Closed = true; } catch { }
                }
                pl.Layer = C.LZone;
                pl.ColorIndex = 8;

                var z = new ZoneData
                {
                    Name = name,
                    Vp = Vp,
                    H = H,
                    Fh = fh
                };

                try
                {
                    Ugo.Label(ms, tr, labelPt, z.Name, planInMm ? 2.5 : 0.25, C.LMark);
                }
                catch { }

                var mt = new MText();
                mt.SetDatabaseDefaults(db);
                mt.Location = new Point3d(labelPt.X, labelPt.Y - (planInMm ? 4 : 0.4), 0);
                mt.TextHeight = planInMm ? 2.0 : 0.2;
                mt.Contents = string.Format("S={0:F2} м²; H={1:F2} м; V={2:F2} м³", areaM2, H, Vp);
                mt.Layer = C.LMark;
                ms.AppendEntity(mt);
                tr.AddNewlyCreatedDBObject(mt, true);

                XD.Set(pl,
                    ("GUID", z.Guid),
                    ("ZONE_NAME", z.Name),
                    ("S", areaM2.ToString("F4")),
                    ("VP", z.Vp.ToString("F4")),
                    ("H", z.H.ToString("F3")),
                    ("FH", z.Fh.ToString("F4")),
                    ("CN", z.Cn.ToString("F1")),
                    ("RHO0", z.Rho0.ToString("F2")),
                    ("GOTV", z.Gotv),
                    ("INSTALL_TYPE", z.InstallType),
                    ("UNITS", planInMm ? "mm" : "m"));

                tr.Commit();
                ed.WriteMessage(string.Format(
                    "\n[ZONE] «{0}»: S={1:F2} м² × H={2:F2} м → Vp={3:F2} м³  GUID={4}",
                    z.Name, areaM2, z.H, z.Vp, z.Guid));
                ed.WriteMessage("\n  Далее: AGPT_CALC → AGPT_ALBUM (зоны из XData контуров).");
            }
        }

        // ----- 2. MODULE -----
        [CommandMethod("AGPT_MODULE")]
        public void Module()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            var db = doc.Database; var ed = doc.Editor;

            var vo = new PromptIntegerOptions("\nОбъём [40/60/80/100/120]") { DefaultValue = 40, UseDefaultValue = true };
            var vr = ed.GetInteger(vo);
            if (vr.Status != PromptStatus.OK || !ModuleData.Cat.ContainsKey(vr.Value)) { ed.WriteMessage("\nОбъём: 40/60/80/100/120"); return; }

            var ro = new PromptKeywordOptions("\nРоль [Рабочий/Резерв]", "Рабочий Резерв") { Keywords = { Default = "Рабочий" } };
            var rr = ed.GetKeywords(ro);
            string role = (rr.Status == PromptStatus.OK && rr.StringResult == "Резерв") ? "Рез" : "Р";

            var pt = ed.GetPoint("\nТочка вставки: ");
            if (pt.Status != PromptStatus.OK) return;

            // Привязка к зоне (опционально)
            string zoneGuid = "", zoneName = "";
            var zs = ed.GetEntity("\nУкажите зону (Enter — пропустить): ");
            if (zs.Status == PromptStatus.OK)
            {
                using (var tr0 = db.TransactionManager.StartTransaction())
                {
                    var ent = tr0.GetObject(zs.ObjectId, OpenMode.ForRead) as Entity;
                    if (ent != null) { zoneGuid = XD.Get(ent, "GUID") ?? ""; zoneName = XD.Get(ent, "ZONE_NAME") ?? ""; }
                    tr0.Commit();
                }
            }

            var mod = ModuleData.Create(vr.Value, role);
            mod.ZoneGuid = zoneGuid;

            using (var tr = db.TransactionManager.StartTransaction())
            {
                Layers.EnsureAll(db, tr); XD.EnsureApp(db, tr);
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                var id = Ugo.Rect(ms, tr, pt.Value, 400, 1200, C.LEq);
                Ugo.Circle(ms, tr, new Point3d(pt.Value.X, pt.Value.Y + 1350, 0), 120, C.LEq);
                Ugo.Label(ms, tr, new Point3d(pt.Value.X, pt.Value.Y + 600, 0),
                    $"МГП {vr.Value}л ({role})", 80, C.LMark);

                var ent = (Entity)tr.GetObject(id, OpenMode.ForWrite);
                XD.Set(ent,
                    ("GUID", mod.Guid), ("KIND", "MODULE"), ("MARK", mod.Mark),
                    ("V_BAL", mod.VBal.ToString()), ("M_FILL", mod.MFill.ToString()),
                    ("GOTV", mod.Gotv), ("ROLE", mod.Role),
                    ("ZONE_GUID", mod.ZoneGuid), ("ZONE_NAME", zoneName));

                tr.Commit();
                ed.WriteMessage($"\n[MODULE] {mod.Mark} ({role}) {mod.MFill} кг  zone={zoneName}");
            }
        }

        // ----- 3. NOZZLE -----
        [CommandMethod("AGPT_NOZZLE")]
        public void Nozzle()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            var db = doc.Database; var ed = doc.Editor;

            var oo = new PromptKeywordOptions("\nОтверстие [15/20]", "15 20") { Keywords = { Default = "15" } };
            var or = ed.GetKeywords(oo);
            double orifice = (or.Status == PromptStatus.OK && or.StringResult == "20") ? 20 : 15;

            var pt = ed.GetPoint("\nТочка вставки насадка: ");
            if (pt.Status != PromptStatus.OK) return;

            var n = new NozzleData
            {
                Orifice = orifice,
                Cover = orifice == 20 ? 4.0 : 3.0,
                Mark = orifice == 20 ? "PT-15A-20-1/2\"" : "PT-15A-15-1/2\""
            };

            using (var tr = db.TransactionManager.StartTransaction())
            {
                Layers.EnsureAll(db, tr); XD.EnsureApp(db, tr);
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                // УГО: треугольник (выпуск вниз)
                var id = Ugo.Triangle(ms, tr, pt.Value, 150, C.LEq);
                Ugo.Label(ms, tr, new Point3d(pt.Value.X, pt.Value.Y - 220, 0), $"Н Ф{orifice}", 60, C.LMark);

                var ent = (Entity)tr.GetObject(id, OpenMode.ForWrite);
                XD.Set(ent,
                    ("GUID", n.Guid), ("KIND", "NOZZLE"), ("MARK", n.Mark),
                    ("ORIFICE", n.Orifice.ToString()), ("COVER", n.Cover.ToString("F1")),
                    ("ORIENT", n.Orient));

                tr.Commit();
                ed.WriteMessage($"\n[NOZZLE] Ф{orifice}  покрытие {n.Cover} м");
            }
        }

        // ----- 4. AUTOMATION -----
        [CommandMethod("AGPT_AUTO")]
        public void Auto()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            var db = doc.Database; var ed = doc.Editor;

            var ko = new PromptKeywordOptions(
                "\nТип [MPT/UDP/IP/TABLO_UHODI/TABLO_NEVHODI/TABLO_AVTO/OPOP]",
                "MPT UDP IP TABLO_UHODI TABLO_NEVHODI TABLO_AVTO OPOP");
            ko.Keywords.Default = "MPT";
            var kr = ed.GetKeywords(ko);
            if (kr.Status != PromptStatus.OK) return;
            string kind = kr.StringResult;

            if (!AutoDeviceData.Catalog.ContainsKey(kind))
            { ed.WriteMessage("\nНеизвестный тип"); return; }

            var pt = ed.GetPoint("\nТочка вставки: ");
            if (pt.Status != PromptStatus.OK) return;

            var info = AutoDeviceData.Catalog[kind];
            var d = new AutoDeviceData
            {
                Kind = kind, Mark = info.mark, Code = info.code, Text = info.label
            };

            // Адрес (для адресных)
            if (kind == "MPT" || kind == "UDP" || kind == "IP" || kind == "OPOP")
            {
                var ar = ed.GetString("\nАдрес в АЛС (Enter — пусто): ");
                if (ar.Status == PromptStatus.OK) d.Addr = ar.StringResult;
            }

            using (var tr = db.TransactionManager.StartTransaction())
            {
                Layers.EnsureAll(db, tr); XD.EnsureApp(db, tr);
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                ObjectId id;
                if (kind.StartsWith("TABLO"))
                {
                    id = Ugo.Rect(ms, tr, pt.Value, 600, 200, C.LEq);
                    Ugo.Label(ms, tr, new Point3d(pt.Value.X, pt.Value.Y + 100, 0), d.Text, 50, C.LMark);
                }
                else
                {
                    id = Ugo.Rect(ms, tr, pt.Value, 300, 200, C.LEq);
                    Ugo.Label(ms, tr, new Point3d(pt.Value.X, pt.Value.Y + 100, 0), info.label, 50, C.LMark);
                }

                var ent = (Entity)tr.GetObject(id, OpenMode.ForWrite);
                XD.Set(ent,
                    ("GUID", d.Guid), ("KIND", d.Kind), ("MARK", d.Mark), ("CODE", d.Code),
                    ("ADDR", d.Addr), ("LINE", d.Line), ("TEXT", d.Text));

                tr.Commit();
                ed.WriteMessage($"\n[AUTO] {d.Mark}" + (string.IsNullOrEmpty(d.Addr) ? "" : $" addr={d.Addr}"));
            }
        }

        // ----- 5. KSID -----
        [CommandMethod("AGPT_KSID")]
        public void Ksid()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            var db = doc.Database; var ed = doc.Editor;

            var ko = new PromptKeywordOptions("\nПлощадь [0.06/0.12/0.18]", "0.06 0.12 0.18");
            ko.Keywords.Default = "0.06";
            var kr = ed.GetKeywords(ko);
            if (kr.Status != PromptStatus.OK) return;
            double fc = double.Parse(kr.StringResult.Replace(',', '.'));

            var pt = ed.GetPoint("\nТочка вставки КСИД: ");
            if (pt.Status != PromptStatus.OK) return;

            var k = new KsidData { Fc = fc };

            using (var tr = db.TransactionManager.StartTransaction())
            {
                Layers.EnsureAll(db, tr); XD.EnsureApp(db, tr);
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                var id = Ugo.Rect(ms, tr, pt.Value, 400, 300, C.LKsid);
                Ugo.Label(ms, tr, new Point3d(pt.Value.X, pt.Value.Y + 150, 0), $"КСИД {fc}", 50, C.LMark);

                var ent = (Entity)tr.GetObject(id, OpenMode.ForWrite);
                XD.Set(ent, ("GUID", k.Guid), ("KIND", "KSID"), ("FC", fc.ToString("F2")), ("P_OPEN", "0.5"));

                tr.Commit();
                ed.WriteMessage($"\n[KSID] Fc={fc} м²");
            }
        }

        // ----- 5b. SDU / RVD / ZPU_EM -----
        [CommandMethod("AGPT_ACC")]
        public void Acc()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            var db = doc.Database; var ed = doc.Editor;

            var ko = new PromptKeywordOptions("\nТип [RVD/SDU/ZPU_EM]", "RVD SDU ZPU_EM");
            ko.Keywords.Default = "RVD";
            var kr = ed.GetKeywords(ko);
            if (kr.Status != PromptStatus.OK) return;
            string kind = kr.StringResult;
            if (!AccData.Cat.ContainsKey(kind)) return;

            var pt = ed.GetPoint("\nТочка вставки: ");
            if (pt.Status != PromptStatus.OK) return;

            var info = AccData.Cat[kind];
            var a = new AccData { Kind = kind, Mark = info.mark, Code = info.code };

            using (var tr = db.TransactionManager.StartTransaction())
            {
                Layers.EnsureAll(db, tr); XD.EnsureApp(db, tr);
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                var id = Ugo.Rect(ms, tr, pt.Value, 250, 150, C.LEq);
                Ugo.Label(ms, tr, new Point3d(pt.Value.X, pt.Value.Y + 75, 0), kind, 40, C.LMark);

                var ent = (Entity)tr.GetObject(id, OpenMode.ForWrite);
                XD.Set(ent, ("GUID", a.Guid), ("KIND", a.Kind), ("MARK", a.Mark), ("CODE", a.Code),
                    ("L_RVD", a.LengthMm.ToString()));

                tr.Commit();
                ed.WriteMessage($"\n[ACC] {a.Mark}");
            }
        }

        // ----- 6. PIPE -----
        [CommandMethod("AGPT_PIPE")]
        public void Pipe()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            var db = doc.Database; var ed = doc.Editor;

            var ko = new PromptKeywordOptions("\nТип [main/branch/drop]", "main branch drop");
            ko.Keywords.Default = "branch";
            var kr = ed.GetKeywords(ko);
            if (kr.Status != PromptStatus.OK) return;
            string segType = kr.StringResult;

            var dyo = new PromptIntegerOptions("\nDy мм [15/20/25/32/40/50]") { DefaultValue = 20, UseDefaultValue = true };
            var dyr = ed.GetInteger(dyo);
            if (dyr.Status != PromptStatus.OK) return;
            int dy = dyr.Value;

            var p1 = ed.GetPoint("\nНачало сегмента: ");
            if (p1.Status != PromptStatus.OK) return;
            var p2 = ed.GetPoint("\nКонец сегмента: ");
            if (p2.Status != PromptStatus.OK) return;

            double len = p1.Value.DistanceTo(p2.Value) / 1000.0; // мм → м (если чертёж в мм)
            var seg = new PipeSegData { SegType = segType, Dy = dy, L = len };
            var style = C.PipeStyle[segType];

            using (var tr = db.TransactionManager.StartTransaction())
            {
                Layers.EnsureAll(db, tr); XD.EnsureApp(db, tr);
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                var pl = new Polyline(2);
                pl.AddVertexAt(0, new Point2d(p1.Value.X, p1.Value.Y), 0, 0, 0);
                pl.AddVertexAt(1, new Point2d(p2.Value.X, p2.Value.Y), 0, 0, 0);
                pl.Layer = C.LPipe;
                pl.Color = Color.FromColorIndex(ColorMethod.ByAci, style.color);
                ms.AppendEntity(pl); tr.AddNewlyCreatedDBObject(pl, true);

                XD.Set(pl,
                    ("SEG_ID", seg.SegId), ("SEG_TYPE", seg.SegType),
                    ("DY", dy.ToString()), ("L", len.ToString("F3")),
                    ("DP", "0"), ("Q", "0"), ("V_FLOW", "0"));

                // Подпись Dy
                var mid = new Point3d((p1.Value.X + p2.Value.X) / 2, (p1.Value.Y + p2.Value.Y) / 2, 0);
                Ugo.Label(ms, tr, mid, $"Ø{dy}", 40, C.LMark);

                tr.Commit();
                ed.WriteMessage($"\n[PIPE] {style.desc} Ø{dy}  L={len:F2} м");
            }
        }

        // ----- CALC (зона → Mg + КСИД, запись в XData) -----
        [CommandMethod("AGPT_CALC")]
        public void Calc()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            var db = doc.Database; var ed = doc.Editor;

            using (var tr = db.TransactionManager.StartTransaction())
            {
                XD.EnsureApp(db, tr);
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                int n = 0;
                double sumMg = 0;
                foreach (ObjectId id in ms)
                {
                    var ent = tr.GetObject(id, OpenMode.ForWrite) as Entity;
                    if (ent == null) continue;
                    if (string.IsNullOrEmpty(XD.Get(ent, "ZONE_NAME"))) continue;
                    if (!(ent is Polyline) && ent.Layer != C.LZone) continue;

                    var z = new ZoneData
                    {
                        Guid = XD.Get(ent, "GUID") ?? System.Guid.NewGuid().ToString("N"),
                        Name = XD.Get(ent, "ZONE_NAME") ?? "?",
                        Vp = XD.GetD(ent, "VP"),
                        H = XD.GetD(ent, "H", 3),
                        Fh = XD.GetD(ent, "FH", 0.011),
                        Cn = XD.GetD(ent, "CN", 7.2),
                        Rho0 = XD.GetD(ent, "RHO0", 7.28)
                    };
                    if (z.Vp <= 0) continue;

                    MassCalc.Compute(z);
                    z.Fc = KsidCalc.Compute(z);
                    int vol = MassCalc.SuggestVol(z.Mg);
                    double ksidSize = KsidCalc.SuggestKsidSize(z.Fc);

                    XD.Set(ent,
                        ("GUID", z.Guid),
                        ("ZONE_NAME", z.Name),
                        ("VP", z.Vp.ToString("F4")),
                        ("H", z.H.ToString("F3")),
                        ("FH", z.Fh.ToString("F4")),
                        ("MP", z.Mp.ToString("F4")),
                        ("MG", z.Mg.ToString("F4")),
                        ("FC", z.Fc.ToString("F6")),
                        ("MODULE_VOL", vol.ToString()),
                        ("CN", z.Cn.ToString("F1")),
                        ("RHO0", z.Rho0.ToString("F2")));

                    ed.WriteMessage($"\n[{z.Name}] S·H→Vp={z.Vp:F2} м³ → Mp={z.Mp:F2} Mg={z.Mg:F2} кг → модуль {vol}л ×(раб+рез) | Fc={z.Fc:F4} м² КСИД {(ksidSize > 0 ? ksidSize.ToString("F2") + " м²" : "—")}");
                    sumMg += z.Mg;
                    n++;
                }
                tr.Commit();
                if (n == 0)
                    ed.WriteMessage("\n[AGPT_CALC] Зон нет. Сначала AGPT_ZONE (контур полилинией).");
                else
                    ed.WriteMessage($"\n[AGPT_CALC] Зон: {n}, ΣMg≈{sumMg:F1} кг (без 100% резерва модулей). Далее AGPT_ALBUM.");
            }
        }

        /// <summary>Список зон из модели (контуры AGPT_ZONE).</summary>
        [CommandMethod("AGPT_ZONE_LIST")]
        public void ZoneList()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            var db = doc.Database; var ed = doc.Editor;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                int n = 0;
                ed.WriteMessage("\n--- Зоны АГПТ в модели ---");
                foreach (ObjectId id in ms)
                {
                    var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent == null) continue;
                    string name = XD.Get(ent, "ZONE_NAME");
                    if (string.IsNullOrEmpty(name)) continue;
                    double s = XD.GetD(ent, "S");
                    double vp = XD.GetD(ent, "VP");
                    double h = XD.GetD(ent, "H");
                    double mg = XD.GetD(ent, "MG");
                    ed.WriteMessage(string.Format(
                        "\n  {0}. {1}: S={2:F2} м² H={3:F2} м Vp={4:F2} м³ Mg={5}",
                        ++n, name, s > 0 ? s : (h > 0 ? vp / h : 0), h, vp,
                        mg > 0 ? mg.ToString("F2") + " кг" : "(запустите AGPT_CALC)"));
                }
                tr.Commit();
                if (n == 0)
                    ed.WriteMessage("\n  (пусто) — нарисуйте контур и выполните AGPT_ZONE");
                else
                    ed.WriteMessage($"\nВсего: {n}");
            }
        }

        // ----- SPEC (свод по XData) -----
        [CommandMethod("AGPT_SPEC")]
        public void Spec()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            var db = doc.Database; var ed = doc.Editor;

            var rows = new Dictionary<string, (string mark, int qty, string unit)>();

            using (var tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                foreach (ObjectId id in ms)
                {
                    var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent == null) continue;
                    string kind = XD.Get(ent, "KIND");
                    if (string.IsNullOrEmpty(kind)) continue;
                    string mark = XD.Get(ent, "MARK") ?? kind;

                    // Модули: +100% резерв уже как отдельные вхождения ROLE=Рез
                    string key = kind + "|" + mark;
                    if (!rows.ContainsKey(key))
                        rows[key] = (mark, 0, "шт.");
                    var r = rows[key];
                    rows[key] = (r.mark, r.qty + 1, r.unit);
                }

                // Трубы: суммарная длина по Dy
                var pipeLen = new Dictionary<int, double>();
                foreach (ObjectId id in ms)
                {
                    var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent == null || ent.Layer != C.LPipe) continue;
                    int dy = (int)XD.GetD(ent, "DY", 0);
                    double l = XD.GetD(ent, "L");
                    if (dy <= 0) continue;
                    if (!pipeLen.ContainsKey(dy)) pipeLen[dy] = 0;
                    pipeLen[dy] += l;
                }
                foreach (var kv in pipeLen)
                {
                    string key = "PIPE|" + kv.Key;
                    rows[key] = ($"Труба Ду{kv.Key}", (int)Math.Ceiling(kv.Value * 1.1), "м"); // +10%
                }

                tr.Commit();
            }

            ed.WriteMessage("\n=== СПЕЦИФИКАЦИЯ (черновик) ===");
            int i = 1;
            foreach (var kv in rows.OrderBy(x => x.Key))
            {
                ed.WriteMessage($"\n{i}. {kv.Value.mark}  —  {kv.Value.qty} {kv.Value.unit}");
                i++;
            }
            ed.WriteMessage($"\nПозиций: {rows.Count}");
        }
    }
}
