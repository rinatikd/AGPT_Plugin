// =============================================================================
// APPT AutoCAD Plugin — расчёт + генерация чертежей модульного порошкового ПТ
// Эталон: «Детский сад АППТ 09.02.26» (Астана, 39-25/1-АППТ)
// Нормы: СП РК 2.02-102-2022, СН РК 2.02-11-2002, СТ РК 1302
// Формула: N = (Vp / Vн) · k1 · k2 · k3 · k4
// .NET 8 / AutoCAD 2018–2026
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
using AGPT.Drawings; // FrameBlock (общая рамка/штамп MMX_ШТАМП)

[assembly: CommandClass(typeof(APPT.Drawings.ApptDrawingCommands))]

namespace APPT.Drawings
{
    // =========================================================================
    // Слои
    // =========================================================================
    public static class DL
    {
        public const string App = "APPT";
        public const string Frame = "APPT-FRAME";
        public const string Text = "APPT-TEXT";
        public const string Table = "APPT-TABLE";
        public const string Scheme = "APPT-SCHEME";
        public const string Eq = "APPT-EQ";
        public const string Mark = "APPT-MARK";
        public const string Zone = "APPT-ZONE";
        public const string Cable = "APPT-CABLE";

        public const double A3W = 420.0, A3H = 297.0;
        public const double Margin = 5.0;
        public const double BindLeft = 20.0;
    }

    // =========================================================================
    // XData
    // =========================================================================
    public static class XD
    {
        public static void EnsureApp(Database db, Transaction tr)
        {
            var rat = (RegAppTable)tr.GetObject(db.RegAppTableId, OpenMode.ForRead);
            if (rat.Has(DL.App)) return;
            rat.UpgradeOpen();
            var r = new RegAppTableRecord { Name = DL.App };
            rat.Add(r); tr.AddNewlyCreatedDBObject(r, true);
        }

        public static void Set(Entity e, params (string k, string v)[] pairs)
        {
            var list = new List<TypedValue> { new TypedValue((int)DxfCode.ExtendedDataRegAppName, DL.App) };
            foreach (var (k, v) in pairs)
                list.Add(new TypedValue((int)DxfCode.ExtendedDataAsciiString, k + "=" + v));
            e.XData = new ResultBuffer(list.ToArray());
        }

        public static string Get(Entity e, string key)
        {
            var rb = e.GetXDataForApplication(DL.App);
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
        public static void Ensure(Database db, Transaction tr, string name, short color)
        {
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (lt.Has(name)) return;
            lt.UpgradeOpen();
            var ltr = new LayerTableRecord
            {
                Name = name,
                Color = Color.FromColorIndex(ColorMethod.ByAci, color)
            };
            lt.Add(ltr); tr.AddNewlyCreatedDBObject(ltr, true);
        }

        public static void EnsureAll(Database db, Transaction tr)
        {
            Ensure(db, tr, DL.Frame, 7);
            Ensure(db, tr, DL.Text, 7);
            Ensure(db, tr, DL.Table, 7);
            Ensure(db, tr, DL.Scheme, 3);
            Ensure(db, tr, DL.Eq, 1);
            Ensure(db, tr, DL.Mark, 7);
            Ensure(db, tr, DL.Zone, 8);
            Ensure(db, tr, DL.Cable, 5);
        }
    }

    // =========================================================================
    // Примитивы
    // =========================================================================
    public static class Draw
    {
        public static ObjectId Line(BlockTableRecord btr, Transaction tr,
            double x0, double y0, double x1, double y1, string layer, short color = 7)
        {
            var pl = new Polyline(2);
            pl.AddVertexAt(0, new Point2d(x0, y0), 0, 0, 0);
            pl.AddVertexAt(1, new Point2d(x1, y1), 0, 0, 0);
            pl.Layer = layer;
            if (color != 7) pl.Color = Color.FromColorIndex(ColorMethod.ByAci, color);
            btr.AppendEntity(pl); tr.AddNewlyCreatedDBObject(pl, true);
            return pl.ObjectId;
        }

        public static ObjectId Rect(BlockTableRecord btr, Transaction tr,
            double x0, double y0, double x1, double y1, string layer, short color = 7)
        {
            var pl = new Polyline(4);
            pl.AddVertexAt(0, new Point2d(x0, y0), 0, 0, 0);
            pl.AddVertexAt(1, new Point2d(x1, y0), 0, 0, 0);
            pl.AddVertexAt(2, new Point2d(x1, y1), 0, 0, 0);
            pl.AddVertexAt(3, new Point2d(x0, y1), 0, 0, 0);
            pl.Closed = true; pl.Layer = layer;
            if (color != 7) pl.Color = Color.FromColorIndex(ColorMethod.ByAci, color);
            btr.AppendEntity(pl); tr.AddNewlyCreatedDBObject(pl, true);
            return pl.ObjectId;
        }

        public static ObjectId Text(BlockTableRecord btr, Transaction tr,
            double x, double y, double h, string s, string layer, short color = 7)
        {
            var t = new DBText
            {
                Position = new Point3d(x, y, 0),
                Height = h,
                TextString = s ?? "",
                Layer = layer
            };
            if (color != 7) t.Color = Color.FromColorIndex(ColorMethod.ByAci, color);
            btr.AppendEntity(t); tr.AddNewlyCreatedDBObject(t, true);
            return t.ObjectId;
        }

        public static ObjectId MText(BlockTableRecord btr, Transaction tr,
            double x, double y, double w, double h, string s, string layer)
        {
            var mt = new MText
            {
                Location = new Point3d(x, y, 0),
                TextHeight = h,
                Width = w,
                Contents = s ?? "",
                Layer = layer,
                Attachment = AttachmentPoint.TopLeft
            };
            btr.AppendEntity(mt); tr.AddNewlyCreatedDBObject(mt, true);
            return mt.ObjectId;
        }

        /// <summary>Таблица AcDbTable (как в АГПТ). Возвращает высоту области.</summary>
        public static double Table(BlockTableRecord btr, Transaction tr,
            double x, double y, double[] colW, double rowH,
            string[] headers, List<string[]> rows, string layer)
        {
            int cols = colW.Length;
            int nData = rows != null ? rows.Count : 0;
            int nRows = 1 + nData;
            double height = nRows * rowH;
            try
            {
                var table = new Table();
                table.SetDatabaseDefaults(btr.Database);
                table.TableStyle = btr.Database.Tablestyle;
                table.Position = new Point3d(x, y, 0);
                table.SetSize(nRows, cols);
                table.Layer = layer;
                for (int c = 0; c < cols; c++)
                    table.SetColumnWidth(c, colW[c]);
                for (int r = 0; r < nRows; r++)
                    table.SetRowHeight(r, rowH);
                for (int c = 0; c < cols; c++)
                {
                    var cell = table.Cells[0, c];
                    cell.TextString = headers != null && c < headers.Length ? (headers[c] ?? "") : "";
                    cell.TextHeight = Math.Min(2.2, rowH * 0.45);
                    cell.Alignment = CellAlignment.MiddleCenter;
                }
                for (int r = 0; r < nData; r++)
                {
                    for (int c = 0; c < cols; c++)
                    {
                        string val = (rows[r] != null && c < rows[r].Length) ? (rows[r][c] ?? "") : "";
                        var cell = table.Cells[r + 1, c];
                        cell.TextString = val;
                        cell.TextHeight = Math.Min(2.0, rowH * 0.4);
                        cell.Alignment = CellAlignment.MiddleLeft;
                    }
                }
                table.GenerateLayout();
                btr.AppendEntity(table);
                tr.AddNewlyCreatedDBObject(table, true);
                return height;
            }
            catch { }

            double totalW = colW.Sum();
            for (int r = 0; r <= nRows; r++)
                Line(btr, tr, x, y - r * rowH, x + totalW, y - r * rowH, layer);
            double xx = x;
            for (int c = 0; c <= cols; c++)
            {
                Line(btr, tr, xx, y, xx, y - nRows * rowH, layer);
                if (c < cols) xx += colW[c];
            }
            xx = x;
            for (int c = 0; c < cols; c++)
            {
                Text(btr, tr, xx + 1, y - rowH + 1.2, 2.0, headers[c], layer);
                xx += colW[c];
            }
            for (int r = 0; r < nData; r++)
            {
                xx = x;
                for (int c = 0; c < cols && c < rows[r].Length; c++)
                {
                    Text(btr, tr, xx + 1, y - (r + 2) * rowH + 1.2, 1.9, rows[r][c], layer);
                    xx += colW[c];
                }
            }
            return height;
        }

        /// <summary>
        /// Рамка + штамп: тот же блок AGPT_Frame_A3_v2 / MMX_ШТАМП, что и у АГПТ.
        /// </summary>
        public static void SheetFrame(BlockTableRecord btr, Transaction tr,
            double ox, double oy, double w, double h, ProjectData pd,
            string sheetTitle, string sheetNo, string sheetsTotal)
        {
            double left = ox + DL.BindLeft;
            double bottom = oy + DL.Margin;
            double right = ox + w - DL.Margin;
            double top = oy + h - DL.Margin;

            bool usedBlock = false;
            try
            {
                if (btr.Database != null)
                    usedBlock = FrameBlock.TryInsert(btr, tr, btr.Database, ox, oy);
            }
            catch { usedBlock = false; }

            if (usedBlock)
            {
                string titleShort = sheetTitle ?? "";
                if (titleShort.Length > 40) titleShort = titleShort.Substring(0, 38) + "…";
                string objShort = pd != null ? (pd.ObjectName ?? "") : "";
                if (objShort.Length > 55) objShort = objShort.Substring(0, 52) + "…";
                Text(btr, tr, right - 183, bottom + 45, 2.4, titleShort, DL.Text);
                Text(btr, tr, right - 183, bottom + 30, 1.6, objShort, DL.Text);
                Text(btr, tr, right - 183, bottom + 17, 2.2, pd != null ? (pd.Cipher ?? "") : "", DL.Text);
                Text(btr, tr, right - 163, bottom + 4, 2.2, pd != null ? (pd.Stage ?? "РП") : "РП", DL.Text);
                Text(btr, tr, right - 117, bottom + 4, 2.2, sheetNo ?? "", DL.Text);
                Text(btr, tr, right - 70, bottom + 4, 2.2, sheetsTotal ?? "", DL.Text);
                return;
            }

            // Fallback — программная рамка (если блок не загрузился)
            Rect(btr, tr, ox, oy, ox + w, oy + h, DL.Frame);
            Rect(btr, tr, left, bottom, right, top, DL.Frame);
            double sw = 185, sh = 55;
            double sx = right - sw, sy = bottom;
            Rect(btr, tr, sx, sy, sx + sw, sy + sh, DL.Frame);
            Line(btr, tr, sx, sy + 15, sx + sw, sy + 15, DL.Frame);
            Line(btr, tr, sx, sy + 25, sx + sw, sy + 25, DL.Frame);
            Line(btr, tr, sx, sy + 40, sx + sw, sy + 40, DL.Frame);
            Text(btr, tr, sx + 2, sy + 45, 2.5, sheetTitle ?? "", DL.Text);
            Text(btr, tr, sx + 2, sy + 17, 2.0, pd != null ? (pd.Cipher ?? "") : "", DL.Text);
            Text(btr, tr, sx + 68, sy + 4, 2.2, sheetNo ?? "", DL.Text);
            Text(btr, tr, sx + 130, sy + 4, 2.2, sheetsTotal ?? "", DL.Text);
        }

    }

    // =========================================================================
    // Модель данных АППТ
    // =========================================================================
    public class ProjectData
    {
        public string Cipher = "39-25/1-АППТ";
        public string ObjectName = "Строительство детского сада, расположенного по адресу: город Астана, район «Есиль», ул. Е 779, уч. 26";
        public string ObjectShort = "Детский сад, г. Астана";
        public string Stage = "РП";
        public string CityYear = "г. Астана, 2026 г.";
        public string Company = "ТОО «ТехноСтройПроект»";
        public string CompanyGsl = "ГСЛ № 20013501";
        public string GenDesigner = "ТОО «КазНИПИ энергетики и связи» ГСЛ №02913";
        public string VolumeTitle = "Автоматическое порошковое пожаротушение";
        public string VolumeNo = "Том 4";
        public string Gip = "Гайсин Д.Б.";
        public string Checked = "Камал Р.";
        public string Drawn = "Ергибаев К.";
        public string NormCtrl = "Соболев М.";
        public List<ZoneCalc> Zones = new List<ZoneCalc>();
        public List<SpecRow> Spec = new List<SpecRow>();
        public List<CableRow> Cables = new List<CableRow>();
    }

    /// <summary>Зона порошкового ПТ (модульная установка).</summary>
    public class ZoneCalc
    {
        public string Name = "Венткамера";
        /// <summary>Расчётный объём защищаемого помещения, м³</summary>
        public double Vp = 50;
        /// <summary>Объём, защищаемый одним модулем, м³ (Тунгус-4 = 70)</summary>
        public double Vn = 70;
        /// <summary>k1 — неравномерность распыления</summary>
        public double K1 = 1.0;
        /// <summary>k2 — запас на затенённость</summary>
        public double K2 = 1.0;
        /// <summary>k3 — изменение огнетушащей эффективности</summary>
        public double K3 = 1.0;
        /// <summary>k4 — степень негерметичности</summary>
        public double K4 = 1.2;
        /// <summary>Тип модуля</summary>
        public string ModuleMark = "МПП(Н)-4(п)-И-ГЭ-У2 «Тунгус-4»";
        /// <summary>N — число рабочих модулей (после расчёта)</summary>
        public int NWork;
        /// <summary>Резерв 100%</summary>
        public int NRes;
        /// <summary>N сырое до округления вверх</summary>
        public double NRaw;
    }

    public class SpecRow
    {
        public int Pos;
        public string Name, Mark, Unit, Note;
        public double Qty;
    }

    public class CableRow
    {
        public string From, To, Mark;
        public double Length;
    }

    // =========================================================================
    // Расчёт АППТ: N = (Vp / Vн) · k1 · k2 · k3 · k4
    // Эталон: Венткамера 1 → N=1; Венткамера 2 → N=2
    // =========================================================================
    public static class Calc
    {
        public static void Modules(ZoneCalc z)
        {
            if (z.Vp <= 0) z.Vp = 1;
            if (z.Vn <= 0) z.Vn = 70;
            if (z.K1 <= 0) z.K1 = 1;
            if (z.K2 <= 0) z.K2 = 1;
            if (z.K3 <= 0) z.K3 = 1;
            if (z.K4 <= 0) z.K4 = 1.2;

            z.NRaw = (z.Vp / z.Vn) * z.K1 * z.K2 * z.K3 * z.K4;
            z.NWork = Math.Max(1, (int)Math.Ceiling(z.NRaw - 1e-9));
            z.NRes = z.NWork; // 100% резерв по эталону
        }
    }

    // =========================================================================
    // Генераторы листов
    // =========================================================================
    public static class SheetGen
    {
        public static double Pitch => Math.Max(450, FrameBlock.FrameWidth + 30.0);

        public static Point3d Origin(int i) => new Point3d(i * Pitch, 0, 0);

        public static void TitleSheet(BlockTableRecord ms, Transaction tr, ProjectData pd, int idx)
        {
            var o = Origin(idx);
            double left = o.X + DL.BindLeft;
            double bottom = o.Y + DL.Margin;
            double right = o.X + DL.A3W - DL.Margin;
            double top = o.Y + DL.A3H - DL.Margin;
            Draw.Rect(ms, tr, o.X, o.Y, o.X + DL.A3W, o.Y + DL.A3H, DL.Frame);
            Draw.Rect(ms, tr, left, bottom, right, top, DL.Frame);

            Draw.Text(ms, tr, o.X + 30, top - 20, 3.5, "Республика Казахстан", DL.Text);
            Draw.Text(ms, tr, o.X + 30, top - 35, 2.2,
                "Генеральный проектировщик: " + (pd.GenDesigner ?? ""), DL.Text);
            Draw.Text(ms, tr, o.X + 30, top - 42, 2.2,
                "Проектировщик: " + (pd.Company ?? "") + " " + (pd.CompanyGsl ?? ""), DL.Text);

            string orderNo = "39-25/1";
            if (!string.IsNullOrEmpty(pd.Cipher))
            {
                var parts = pd.Cipher.Split('-');
                if (parts.Length >= 2) orderNo = parts[0] + "-" + parts[1];
            }
            Draw.Text(ms, tr, o.X + 30, top - 49, 2.2, "Заказ: " + orderNo, DL.Text);

            Draw.MText(ms, tr, o.X + 60, o.Y + 200, 300, 3.2,
                "«" + (pd.ObjectName ?? "") + "»", DL.Text);
            Draw.Text(ms, tr, o.X + 140, o.Y + 155, 5, "РАБОЧИЙ ПРОЕКТ", DL.Text);
            Draw.Text(ms, tr, o.X + 150, o.Y + 140, 4, pd.Cipher ?? "", DL.Text);
            Draw.Text(ms, tr, o.X + 170, o.Y + 125, 3.5, pd.VolumeNo ?? "Том 4", DL.Text);
            Draw.Text(ms, tr, o.X + 90, o.Y + 110, 3.5,
                pd.VolumeTitle ?? "Автоматическое порошковое пожаротушение", DL.Text);
            Draw.Text(ms, tr, o.X + 155, o.Y + 40, 3, pd.CityYear ?? "", DL.Text);
            Draw.Text(ms, tr, right - 40, bottom + 5, 1.8, "Формат А3", DL.Text);
        }

        public static void GeneralData(BlockTableRecord ms, Transaction tr, ProjectData pd, int idx)
        {
            var o = Origin(idx);
            Draw.SheetFrame(ms, tr, o.X, o.Y, DL.A3W, DL.A3H, pd, "Общие данные", "1", "4");

            double leftX = o.X + DL.BindLeft + 4;
            double rightX = o.X + DL.BindLeft + 200;
            double y = o.Y + DL.A3H - 14;

            Draw.Text(ms, tr, leftX, y, 2.7, "Ведомость рабочих чертежей основного комплекта", DL.Text);
            y -= 5;
            var ved = new List<string[]>
            {
                new[] { "1", "Общие данные", "" },
                new[] { "2", "Структурная схема.", "Схема подключения оборудования" },
                new[] { "3", "Расчет порошкового пожаротушения", "" },
                new[] { "4", "План расположения оборудования", "и прокладки кабеля АППТ" },
            };
            Draw.Table(ms, tr, leftX, y, new[] { 12.0, 100.0, 65.0 }, 5.5,
                new[] { "Лист", "Наименование", "Примечание" }, ved, DL.Table);
            y -= (ved.Count + 1) * 5.5 + 8;

            Draw.Text(ms, tr, leftX, y, 2.5, "Ведомость ссылочных и прилагаемых документов", DL.Text);
            y -= 5;
            var refs = new List<string[]>
            {
                new[] { "СНиП РК 3.02-10-2010", "Устр.систем связи, сигнализации...", "" },
                new[] { "СП РК 2.02-102-2022", "Пожарная автоматика зданий...", "" },
                new[] { "ПУЭ РК 2022", "Правила устройства электроустановок РК", "" },
                new[] { "СН РК 2.02-11-2002", "Нормы оборудования зданий...", "" },
                new[] { "39-25/1-АППТ.СО", "Спецификация оборудования...", "1 лист" },
            };
            Draw.Table(ms, tr, leftX, y, new[] { 42.0, 95.0, 30.0 }, 5.0,
                new[] { "Обозначение", "Наименование", "Примечание" }, refs, DL.Table);

            double ry = o.Y + DL.A3H - 14;
            Draw.Text(ms, tr, rightX, ry, 3.0, "Общие данные", DL.Text);
            ry -= 6;
            string od =
                "Настоящий альбом является составной частью проекта «" + (pd.ObjectShort ?? "Объект") + "». " +
                "Проектом предусматривается оснащение объекта системой модульного порошкового пожаротушения " +
                "на базе оборудования фирмы «РУБЕЖ» (модули «Тунгус»).\n\n" +
                "Раздел проекта выполнен на основании задания на проектирование и разработан в соответствии " +
                "с требованиями нормативов, действующих на территории Республики Казахстан.\n\n" +
                "Автоматические установки порошкового пожаротушения предназначены для выявления очага пожара, " +
                "передачи сигнала о его возникновении, а также подачи огнетушащего порошка в защищаемое помещение.\n\n" +
                "По способу порошкового тушения принята система модульного порошкового пожаротушения " +
                "с модулями МПП(Н)-4 «Тунгус-4».\n\n" +
                "В качестве прибора управления принято оборудование МПТ-1-R3 (система «РУБЕЖ»).\n\n" +
                "Пуск — по алгоритму C (два автоматических ИП). Задержка на эвакуацию ≥ 10 с. " +
                "Электроснабжение — I категория надёжности.";
            Draw.MText(ms, tr, rightX, ry, 195, 2.0, od, DL.Text);
        }

        public static void StructuralScheme(BlockTableRecord ms, Transaction tr, ProjectData pd, int idx)
        {
            var o = Origin(idx);
            Draw.SheetFrame(ms, tr, o.X, o.Y, DL.A3W, DL.A3H, pd,
                "Структурная схема. Схема подключения оборудования", "2", "5");

            // Рабочая область внутри рамки: x 25..400, y 70..280 (над штампом)
            void Box(double x, double y, double w, double h, string label, short col)
            {
                Draw.Rect(ms, tr, x - w / 2, y - h / 2, x + w / 2, y + h / 2, DL.Scheme, col);
                Draw.Text(ms, tr, x - w / 2 + 1.5, y - 1.2, 1.7, label, DL.Scheme);
            }
            void Arr(double x0, double y0, double x1, double y1)
            {
                Draw.Line(ms, tr, x0, y0, x1, y1, DL.Scheme, 3);
            }

            // === Левая половина: структура ===
            double cx = o.X + 100;
            double cy = o.Y + 220;
            Draw.Text(ms, tr, o.X + 28, o.Y + 275, 2.3, "Структурная схема АУПП", DL.Text);
            Box(cx, cy, 95, 14, "ППКУП / АПС", 3);
            Arr(cx, cy - 7, cx, cy - 16);
            Box(cx, cy - 24, 88, 14, "МПТ-1-R3 (ППУ)", 1);
            Arr(cx - 30, cy - 31, cx - 70, cy - 42);
            Arr(cx, cy - 31, cx, cy - 42);
            Arr(cx + 30, cy - 31, cx + 70, cy - 42);
            Box(cx - 70, cy - 50, 48, 12, "ИП ×2 (C)", 3);
            Box(cx, cy - 50, 36, 12, "УДП", 3);
            Box(cx + 70, cy - 50, 48, 12, "ОПОП", 6);
            Arr(cx, cy - 56, cx, cy - 66);
            Box(cx, cy - 76, 95, 14, "МПП Тунгус-4 (раб+рез)", 1);
            Arr(cx, cy - 83, cx, cy - 92);
            Box(cx, cy - 100, 70, 12, "ИВЭПР 24В I кат.", 2);

            // === Правая половина: подключение (не выходить за x=405) ===
            double rx = o.X + 220;
            double ry = o.Y + 270;
            Draw.Text(ms, tr, rx, ry, 2.3, "Схема подключения", DL.Text);
            // прибор
            Draw.Rect(ms, tr, rx, ry - 85, rx + 70, ry - 8, DL.Scheme, 1);
            Draw.Text(ms, tr, rx + 12, ry - 18, 1.9, "МПТ-1-R3", DL.Text);
            string[] terms = { "АЛС", "ЗПУ1", "ЗПУ2", "Питан." };
            for (int i = 0; i < terms.Length; i++)
            {
                double ty = ry - 30 - i * 14;
                Draw.Line(ms, tr, rx + 70, ty, rx + 85, ty, DL.Scheme, 3);
                Draw.Text(ms, tr, rx + 88, ty - 1, 1.4, terms[i], DL.Mark);
            }
            // оборудование справа (до x≈400)
            Draw.Rect(ms, tr, rx + 120, ry - 36, rx + 175, ry - 22, DL.Scheme, 3);
            Draw.Text(ms, tr, rx + 125, ry - 33, 1.4, "ИП×2", DL.Mark);
            Draw.Rect(ms, tr, rx + 120, ry - 54, rx + 175, ry - 40, DL.Scheme, 6);
            Draw.Text(ms, tr, rx + 122, ry - 51, 1.3, "ОПОП", DL.Mark);
            Draw.Rect(ms, tr, rx + 120, ry - 72, rx + 180, ry - 58, DL.Eq, 1);
            Draw.Text(ms, tr, rx + 122, ry - 69, 1.3, "МПП", DL.Mark);
            Draw.Line(ms, tr, rx + 85, ry - 30, rx + 120, ry - 29, DL.Scheme, 3);
            Draw.Line(ms, tr, rx + 85, ry - 44, rx + 120, ry - 47, DL.Scheme, 3);
            Draw.Line(ms, tr, rx + 85, ry - 58, rx + 120, ry - 65, DL.Scheme, 1);

            // Таблица цепей — НИЖЕ схем, над штампом (y 70…145)
            var chain = new List<string[]>
            {
                new[] { "1", "АЛС", "ИП 212-64-R3", "2", "алг. C" },
                new[] { "2", "АЛС", "УДП 513-11-A-R3", "1", "ручн." },
                new[] { "3", "АЛС", "ОПОП «Порошок! Уходи!»", "1", "" },
                new[] { "4", "АЛС", "ОПОП «Порошок! Не входи!»", "1", "" },
                new[] { "5", "ЗПУ1", "МПП рабочий Тунгус-4", "1", "" },
                new[] { "6", "ЗПУ2", "МПП резерв", "1", "100%" },
                new[] { "7", "Питан.", "ИВЭПР 24/2.5", "1", "I кат." },
            };
            Draw.Table(ms, tr, o.X + 28, o.Y + 145, new[] { 12.0, 26.0, 85.0, 14.0, 28.0 }, 3.8,
                new[] { "№", "Цепь", "Оборудование", "Кол.", "Прим." }, chain, DL.Table);

            Draw.Text(ms, tr, o.X + 28, o.Y + 68, 1.6,
                "Пуск: C → задержка ≥10 с → ЗПУ МПП → порошок. Кабель КПСнг(А)-FRLS 2×2×0,75.", DL.Text);
        }

        public static void CalcSheet(BlockTableRecord ms, Transaction tr, ProjectData pd, int idx)
        {
            var o = Origin(idx);
            Draw.SheetFrame(ms, tr, o.X, o.Y, DL.A3W, DL.A3H, pd,
                "Расчет порошкового пожаротушения", "3", "5");

            var zones = pd.Zones;
            if (zones.Count == 0)
            {
                zones = new List<ZoneCalc>
                {
                    new ZoneCalc { Name = "Венткамера 1", Vp = 52.08, Vn = 70, K1 = 1, K2 = 1, K3 = 1, K4 = 1.2 },
                    new ZoneCalc { Name = "Венткамера 2", Vp = 108.6, Vn = 70, K1 = 1, K2 = 1, K3 = 1, K4 = 1.2 },
                };
            }

            int n = Math.Min(4, zones.Count);
            // Две колонки: ширина таблицы 160, зазор 20 → без наезда
            double colPitch = 175;
            double x0 = o.X + DL.BindLeft + 8;
            double yTop = o.Y + DL.A3H - 18;

            for (int i = 0; i < n; i++)
            {
                var z = zones[i];
                Calc.Modules(z);
                int col = i % 2;
                int row = i / 2;
                // ряд 0: y=275, ряд 1: y=160 — жёстко, без наложения
                double x = x0 + col * colPitch;
                double y = (row == 0) ? yTop : (o.Y + 160);

                Draw.Text(ms, tr, x, y, 2.4, (i + 1) + ". " + z.Name, DL.Text);
                y -= 6;

                // Одна таблица: исходные + результаты
                var rows = new List<string[]>
                {
                    new[] { "Расчёт", "По объёму" },
                    new[] { "Vp, м³", z.Vp.ToString("F2") },
                    new[] { "Vн, м³", z.Vn.ToString("F0") },
                    new[] { "k1", z.K1.ToString("F2") },
                    new[] { "k2", z.K2.ToString("F2") },
                    new[] { "k3", z.K3.ToString("F2") },
                    new[] { "k4", z.K4.ToString("F2") },
                    new[] { "Модуль", "МПП(Н)-4 Тунгус-4" },
                    new[] { "N сырое", z.NRaw.ToString("F3") },
                    new[] { "N рабочих", z.NWork.ToString() + " шт" },
                    new[] { "N резерв", z.NRes.ToString() + " шт" },
                    new[] { "Всего", (z.NWork + z.NRes).ToString() + " шт" },
                };
                Draw.Table(ms, tr, x, y, new[] { 55.0, 95.0 }, 4.0,
                    new[] { "Параметр", "Значение" }, rows, DL.Table);
            }

            Draw.Text(ms, tr, o.X + DL.BindLeft + 8, o.Y + 68, 1.7,
                "N = (Vp/Vн)·k1·k2·k3·k4. Резерв 100%. Пуск — алгоритм C.", DL.Text);
        }

        public static void PlanSheet(BlockTableRecord ms, Transaction tr, ProjectData pd, int idx)
        {
            var o = Origin(idx);
            Draw.SheetFrame(ms, tr, o.X, o.Y, DL.A3W, DL.A3H, pd,
                "План расположения оборудования и прокладки кабеля АППТ", "4", "5");

            var zones = pd.Zones;
            if (zones.Count == 0)
            {
                zones = new List<ZoneCalc>
                {
                    new ZoneCalc { Name = "Венткамера 1", Vp = 52.08, Vn = 70, K1 = 1, K2 = 1, K3 = 1, K4 = 1.2 },
                    new ZoneCalc { Name = "Венткамера 2", Vp = 108.6, Vn = 70, K1 = 1, K2 = 1, K3 = 1, K4 = 1.2 },
                };
                foreach (var zz in zones) Calc.Modules(zz);
            }

            int n = Math.Min(4, zones.Count);
            const double sc = 18.0; // мм/м ≈ 1:55
            double[] oxs = { o.X + 35, o.X + 220, o.X + 35, o.X + 220 };
            double[] oys = { o.Y + 150, o.Y + 150, o.Y + 75, o.Y + 75 };

            for (int i = 0; i < n; i++)
            {
                var z = zones[i];
                Calc.Modules(z);
                double area = Math.Max(8, z.Vp / 3.0); // условная площадь при H≈3
                double Lm = Math.Sqrt(area * 1.3);
                double Wm = area / Lm;
                double pw = Math.Min(150, Math.Max(45, Lm * sc));
                double ph = Math.Min(65, Math.Max(30, Wm * sc));
                double bx = oxs[i], by = oys[i];

                // стены
                double t = 1.2;
                Draw.Rect(ms, tr, bx, by, bx + pw, by + ph, DL.Zone, 7);
                Draw.Rect(ms, tr, bx + t, by + t, bx + pw - t, by + ph - t, DL.Zone, 7);
                // дверь
                double doorW = Math.Min(10, pw * 0.14);
                Draw.Line(ms, tr, bx + pw * 0.4, by, bx + pw * 0.4, by - 4, DL.Mark, 7);
                Draw.Line(ms, tr, bx + pw * 0.4 + doorW, by, bx + pw * 0.4 + doorW, by - 4, DL.Mark, 7);

                Draw.Text(ms, tr, bx, by + ph + 4, 2.1, z.Name, DL.Text);

                // МПП
                double mx = bx + 8, my = by + 8;
                Draw.Rect(ms, tr, mx, my, mx + 9, my + 11, DL.Eq, 1);
                Draw.Text(ms, tr, mx, my + 13, 1.4, "МПП-" + (i + 1), DL.Mark);
                // резерв
                Draw.Rect(ms, tr, mx + 12, my, mx + 21, my + 11, DL.Eq, 1);
                Draw.Text(ms, tr, mx + 12, my + 13, 1.3, "рез.", DL.Mark);

                // ИП
                double ip1x = bx + pw * 0.3, ip1y = by + ph - 9;
                double ip2x = bx + pw * 0.7, ip2y = by + ph - 9;
                Draw.Rect(ms, tr, ip1x - 2, ip1y - 2, ip1x + 2, ip1y + 2, DL.Scheme, 3);
                Draw.Rect(ms, tr, ip2x - 2, ip2y - 2, ip2x + 2, ip2y + 2, DL.Scheme, 3);
                Draw.Text(ms, tr, ip1x - 3, ip1y + 4, 1.3, "ИП", DL.Mark);

                // кабель
                Draw.Line(ms, tr, ip1x, ip1y, ip2x, ip2y, DL.Scheme, 3);
                Draw.Line(ms, tr, ip2x, ip2y, mx + 9, my + 5, DL.Scheme, 3);

                // размеры
                Draw.Text(ms, tr, bx, by - 8, 1.5, Lm.ToString("F1") + "×" + Wm.ToString("F1") + " м", DL.Mark);
                Draw.Text(ms, tr, bx, by - 16, 1.5,
                    string.Format("Vp={0:F0} м³  N={1}+{2}", z.Vp, z.NWork, z.NRes), DL.Text);
            }

            // легенда
            var leg = new List<string[]>
            {
                new[] { "МПП", "Модуль порошкового ПТ «Тунгус-4»" },
                new[] { "ИП", "Извещатель пожарный дымовой" },
                new[] { "ОПОП", "Табло «Порошок! Уходи/Не входи!»" },
                new[] { "— —", "Кабель АЛС КПСнг(А)-FRLS" },
            };
            Draw.Table(ms, tr, o.X + DL.BindLeft + 6, o.Y + 58, new[] { 22.0, 100.0 }, 3.6,
                new[] { "Обозн.", "Наименование" }, leg, DL.Table);
            Draw.Text(ms, tr, o.X + 250, o.Y + 52, 1.7, "Масштаб планов условный ~1:50", DL.Text);
        }

        public static void Specification(BlockTableRecord ms, Transaction tr, ProjectData pd, int idx)
        {
            var o = Origin(idx);
            var cipherSpec = (pd.Cipher ?? "39-25/1-АППТ") + ".СО";
            // временно подменим cipher в копии для штампа
            var pdSpec = new ProjectData
            {
                Cipher = cipherSpec,
                ObjectName = pd.ObjectName,
                Stage = pd.Stage,
                Company = pd.Company,
                CompanyGsl = pd.CompanyGsl,
                CityYear = pd.CityYear,
                Gip = pd.Gip,
                Checked = pd.Checked,
                Drawn = pd.Drawn,
                NormCtrl = pd.NormCtrl
            };
            Draw.SheetFrame(ms, tr, o.X, o.Y, DL.A3W, DL.A3H, pdSpec,
                "Спецификация оборудования, изделий и материалов", "1", "1");

            double x = o.X + DL.BindLeft + 5;
            double y = o.Y + DL.A3H - 18;
            Draw.Text(ms, tr, x, y, 3.2, "СПЕЦИФИКАЦИЯ (ГОСТ 21.110)", DL.Text);
            y -= 6;

            var rows = pd.Spec;
            if (rows.Count == 0)
            {
                // По эталону «Детский сад АППТ»
                int nMod = 0;
                foreach (var z in pd.Zones) { Calc.Modules(z); nMod += z.NWork + z.NRes; }
                if (nMod == 0) nMod = 4; // 1+1 + 2+2 демо

                rows = new List<SpecRow>
                {
                    new SpecRow { Pos=1, Name="Извещатель пожарный дымовой оптико-электронный адресно-аналоговый", Mark="ИП 212-64-R3 W1.02", Unit="шт.", Qty=5, Note="+10% запас" },
                    new SpecRow { Pos=2, Name="Оповещатель охранно-пожарный комбинированный свето-звуковой адресный", Mark="ОПОП 124-R3", Unit="шт.", Qty=1, Note="" },
                    new SpecRow { Pos=3, Name="Устройство дистанционного пуска адресное «Запуск пожаротушения»", Mark="УДП 513-11-A-R3", Unit="шт.", Qty=1, Note="" },
                    new SpecRow { Pos=4, Name="Оповещатель световой адресный (Табло «Порошок уходи!»)", Mark="ОПОП 1-R3", Unit="шт.", Qty=1, Note="" },
                    new SpecRow { Pos=5, Name="Оповещатель световой адресный (Табло «Порошок не входи!»)", Mark="ОПОП 1-R3", Unit="шт.", Qty=1, Note="" },
                    new SpecRow { Pos=6, Name="Адресный модуль пожаротушения", Mark="МПТ-1-R3", Unit="шт.", Qty=1, Note="" },
                    new SpecRow { Pos=7, Name="Установка модульная автоматического порошкового пожаротушения МПП(Н)", Mark="МПП(Н)-4 «Тунгус-4»", Unit="шт.", Qty=nMod, Note="+100% резерв" },
                    new SpecRow { Pos=8, Name="Кабель для систем ПС и СОУЭ огнестойкий", Mark="КПСнг(А)-FRLS 2×2×0,75", Unit="м", Qty=50, Note="" },
                    new SpecRow { Pos=9, Name="Труба ПВХ гибкая d20 мм", Mark="ПВХ d20", Unit="м", Qty=40, Note="" },
                };
            }

            var data = rows.Select(r => new[]
            {
                r.Pos.ToString(), r.Name, r.Mark, r.Unit, r.Qty.ToString("G"), r.Note
            }).ToList();
            Draw.Table(ms, tr, x, y, new[] { 12.0, 100.0, 55.0, 14.0, 14.0, 40.0 }, 5.5,
                new[] { "Поз.", "Наименование", "Тип, марка", "Ед.", "Кол.", "Примечание" }, data, DL.Table);
        }

        public static void FullAlbum(BlockTableRecord ms, Transaction tr, ProjectData pd)
        {
            FrameBlock.ResetCache();
            try { FrameBlock.EnsureDefinition(ms.Database, tr); }
            catch (System.Exception ex)
            {
                try
                {
                    var d = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
                    if (d != null) d.Editor.WriteMessage("\n[APPT] Рамка: fallback (" + ex.Message + ")");
                }
                catch { }
            }
            TitleSheet(ms, tr, pd, 0);
            GeneralData(ms, tr, pd, 1);
            StructuralScheme(ms, tr, pd, 2);
            CalcSheet(ms, tr, pd, 3);
            PlanSheet(ms, tr, pd, 4);
            Specification(ms, tr, pd, 5);
        }
    }

    // =========================================================================
    // Сбор зон из модели
    // =========================================================================
    public static class Collect
    {
        public static ProjectData FromModel(Database db, Transaction tr)
        {
            var pd = new ProjectData();
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

            foreach (ObjectId id in ms)
            {
                var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (ent == null) continue;
                if (ent.Layer == DL.Zone || XD.Get(ent, "ZONE_NAME") != null)
                {
                    var z = new ZoneCalc
                    {
                        Name = XD.Get(ent, "ZONE_NAME") ?? "Зона",
                        Vp = XD.GetD(ent, "VP", 50),
                        Vn = XD.GetD(ent, "VN", 70),
                        K1 = XD.GetD(ent, "K1", 1),
                        K2 = XD.GetD(ent, "K2", 1),
                        K3 = XD.GetD(ent, "K3", 1),
                        K4 = XD.GetD(ent, "K4", 1.2),
                        ModuleMark = XD.Get(ent, "MODULE") ?? "МПП(Н)-4 «Тунгус-4»"
                    };
                    Calc.Modules(z);
                    pd.Zones.Add(z);
                }
            }

            if (pd.Zones.Count == 0)
            {
                // Демо по эталону «Детский сад АППТ»
                foreach (var z in new[]
                {
                    new ZoneCalc { Name = "Венткамера 1", Vp = 52.08, Vn = 70, K1 = 1, K2 = 1, K3 = 1, K4 = 1.2 },
                    new ZoneCalc { Name = "Венткамера 2", Vp = 108.6, Vn = 70, K1 = 1, K2 = 1, K3 = 1, K4 = 1.2 },
                })
                {
                    Calc.Modules(z);
                    pd.Zones.Add(z);
                }
            }
            return pd;
        }
    }

    // =========================================================================
    // Команды АППТ
    // =========================================================================
    public class ApptDrawingCommands
    {
        [CommandMethod("APPT_ALBUM")]
        public void Album()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var db = doc.Database;
            var ed = doc.Editor;
            try
            {
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    XD.EnsureApp(db, tr);
                    Layers.EnsureAll(db, tr);
                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                    var pd = Collect.FromModel(db, tr);

                    var ciph = ed.GetString("\nШифр <" + pd.Cipher + ">: ");
                    if (ciph.Status == PromptStatus.OK && !string.IsNullOrWhiteSpace(ciph.StringResult))
                        pd.Cipher = ciph.StringResult.Trim();
                    var obj = ed.GetString("\nОбъект <" + pd.ObjectShort + ">: ");
                    if (obj.Status == PromptStatus.OK && !string.IsNullOrWhiteSpace(obj.StringResult))
                    {
                        pd.ObjectShort = obj.StringResult.Trim();
                        pd.ObjectName = obj.StringResult.Trim();
                    }

                    var ask = ed.GetKeywords(new PromptKeywordOptions(
                        "\nВвести свои помещения (L×W×H)? [Да/Нет] <Нет>: ", "Да Нет") { AllowNone = true });
                    if (ask.Status == PromptStatus.OK && ask.StringResult == "Да")
                    {
                        pd.Zones.Clear();
                        ed.WriteMessage("\nПустое имя — конец ввода.");
                        for (int i = 1; i <= 12; i++)
                        {
                            var nr = ed.GetString(string.Format("\n[{0}] Имя (Enter=готово): ", i));
                            if (nr.Status != PromptStatus.OK || string.IsNullOrWhiteSpace(nr.StringResult))
                                break;
                            double len = 5, wid = 10, hgt = 3, k4 = 1.2;
                            var o1 = ed.GetDouble(new PromptDoubleOptions("\n  L, м <5>: ") { DefaultValue = 5, UseDefaultValue = true, AllowNegative = false, AllowZero = false });
                            if (o1.Status == PromptStatus.OK) len = o1.Value;
                            var o2 = ed.GetDouble(new PromptDoubleOptions("\n  W, м <10>: ") { DefaultValue = 10, UseDefaultValue = true, AllowNegative = false, AllowZero = false });
                            if (o2.Status == PromptStatus.OK) wid = o2.Value;
                            var o3 = ed.GetDouble(new PromptDoubleOptions("\n  H, м <3>: ") { DefaultValue = 3, UseDefaultValue = true, AllowNegative = false, AllowZero = false });
                            if (o3.Status == PromptStatus.OK) hgt = o3.Value;
                            var o4 = ed.GetDouble(new PromptDoubleOptions("\n  k4 <1.2>: ") { DefaultValue = 1.2, UseDefaultValue = true, AllowNegative = false, AllowZero = false });
                            if (o4.Status == PromptStatus.OK) k4 = o4.Value;
                            var z = new ZoneCalc
                            {
                                Name = nr.StringResult.Trim(),
                                Vp = len * wid * hgt,
                                Vn = 70,
                                K1 = 1, K2 = 1, K3 = 1, K4 = k4,
                                ModuleMark = "МПП(Н)-4 «Тунгус-4»"
                            };
                            Calc.Modules(z);
                            pd.Zones.Add(z);
                            ed.WriteMessage(string.Format("\n  → {0}: Vp={1:F1} N={2}+{3}", z.Name, z.Vp, z.NWork, z.NRes));
                        }
                    }

                    SheetGen.FullAlbum(ms, tr, pd);
                    tr.Commit();
                    ed.WriteMessage("\n[APPT_ALBUM] Альбом АППТ построен в модели (шаг 500 мм).");
                    ed.WriteMessage("\n  0: Титул  1: Общие данные  2: Структурная схема");
                    ed.WriteMessage("\n  3: Расчёт порошка  4: План  5: Спецификация");
                    ed.WriteMessage("\nЭталон: «Детский сад АППТ» (Тунгус-4, РП).");
                }
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\n[APPT_ALBUM] Ошибка: " + ex.Message);
            }
        }

        [CommandMethod("APPT_CALC")]
        public void CalcCmd()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var ed = doc.Editor;
            try
            {
                using (var tr = doc.Database.TransactionManager.StartTransaction())
                {
                    XD.EnsureApp(doc.Database, tr);
                    var pd = Collect.FromModel(doc.Database, tr);
                    foreach (var z in pd.Zones)
                    {
                        Calc.Modules(z);
                        ed.WriteMessage(
                            $"\n[{z.Name}] Vp={z.Vp:F1} м³  Vн={z.Vn:F0}  N={z.NRaw:F3} → {z.NWork}+{z.NRes} шт ({z.ModuleMark})");
                    }
                    tr.Commit();
                }
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\n[APPT_CALC] Ошибка: " + ex.Message);
            }
        }

        [CommandMethod("APPT_ZONE")]
        public void ZoneCmd()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var db = doc.Database;
            var ed = doc.Editor;
            try
            {
                var nameRes = ed.GetString("\nИмя зоны <Венткамера>: ");
                string name = (nameRes.Status == PromptStatus.OK && !string.IsNullOrWhiteSpace(nameRes.StringResult))
                    ? nameRes.StringResult.Trim() : "Венткамера";

                var vpRes = ed.GetDouble(new PromptDoubleOptions("\nОбъём Vp, м³ <50>: ")
                {
                    DefaultValue = 50, UseDefaultValue = true, AllowNegative = false, AllowZero = false
                });
                double vp = vpRes.Status == PromptStatus.OK ? vpRes.Value : 50;

                var vnRes = ed.GetDouble(new PromptDoubleOptions("\nVн одного модуля, м³ <70>: ")
                {
                    DefaultValue = 70, UseDefaultValue = true, AllowNegative = false, AllowZero = false
                });
                double vn = vnRes.Status == PromptStatus.OK ? vnRes.Value : 70;

                var k4Res = ed.GetDouble(new PromptDoubleOptions("\nk4 (негерметичность) <1.2>: ")
                {
                    DefaultValue = 1.2, UseDefaultValue = true, AllowNegative = false, AllowZero = false
                });
                double k4 = k4Res.Status == PromptStatus.OK ? k4Res.Value : 1.2;

                var ptRes = ed.GetPoint("\nТочка вставки зоны: ");
                if (ptRes.Status != PromptStatus.OK) return;
                var p = ptRes.Value;

                using (var tr = db.TransactionManager.StartTransaction())
                {
                    XD.EnsureApp(db, tr);
                    Layers.EnsureAll(db, tr);
                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                    // Прямоугольник-маркер зоны 3000×2000 мм
                    double w = 3000, h = 2000;
                    var pl = new Polyline(4);
                    pl.AddVertexAt(0, new Point2d(p.X, p.Y), 0, 0, 0);
                    pl.AddVertexAt(1, new Point2d(p.X + w, p.Y), 0, 0, 0);
                    pl.AddVertexAt(2, new Point2d(p.X + w, p.Y + h), 0, 0, 0);
                    pl.AddVertexAt(3, new Point2d(p.X, p.Y + h), 0, 0, 0);
                    pl.Closed = true;
                    pl.Layer = DL.Zone;
                    pl.Color = Color.FromColorIndex(ColorMethod.ByAci, 8);
                    ms.AppendEntity(pl); tr.AddNewlyCreatedDBObject(pl, true);

                    var z = new ZoneCalc { Name = name, Vp = vp, Vn = vn, K4 = k4 };
                    Calc.Modules(z);

                    XD.Set(pl,
                        ("ZONE_NAME", name),
                        ("VP", vp.ToString("F2")),
                        ("VN", vn.ToString("F1")),
                        ("K1", "1"), ("K2", "1"), ("K3", "1"),
                        ("K4", k4.ToString("F2")),
                        ("MODULE", z.ModuleMark),
                        ("N", z.NWork.ToString()));

                    var t = new DBText
                    {
                        Position = new Point3d(p.X + 100, p.Y + h - 300, 0),
                        Height = 150,
                        TextString = $"{name}\nVp={vp:F1} N={z.NWork}+{z.NRes}",
                        Layer = DL.Mark
                    };
                    ms.AppendEntity(t); tr.AddNewlyCreatedDBObject(t, true);

                    tr.Commit();
                    ed.WriteMessage($"\n[APPT_ZONE] {name}: Vp={vp:F1} → N={z.NWork}+{z.NRes} шт");
                }
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\n[APPT_ZONE] Ошибка: " + ex.Message);
            }
        }

        [CommandMethod("APPT_SHEET_OD")]
        public void SheetOd() => RunSheet((ms, tr, pd) => SheetGen.GeneralData(ms, tr, pd, 0));

        [CommandMethod("APPT_SHEET_CALC")]
        public void SheetCalc() => RunSheet((ms, tr, pd) => SheetGen.CalcSheet(ms, tr, pd, 0));

        [CommandMethod("APPT_SHEET_SPEC")]
        public void SheetSpec() => RunSheet((ms, tr, pd) => SheetGen.Specification(ms, tr, pd, 0));

        void RunSheet(Action<BlockTableRecord, Transaction, ProjectData> build)
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var db = doc.Database;
            var ed = doc.Editor;
            try
            {
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    XD.EnsureApp(db, tr);
                    Layers.EnsureAll(db, tr);
                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                    var pd = Collect.FromModel(db, tr);
                    build(ms, tr, pd);
                    tr.Commit();
                    ed.WriteMessage("\n[APPT] Лист сформирован в модели.");
                }
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\n[APPT] Ошибка: " + ex.Message);
            }
        }
    }
}
