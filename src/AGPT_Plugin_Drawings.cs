// =============================================================================
// AGPT AutoCAD Plugin — расчёт + генерация чертежей В МОДЕЛИ
// Принцип: вся графика и текст (общие данные, расчёты, спецификация, КЖ,
// аксонометрия, структурная схема) собираются как ЧЕРТЕЖИ в Model/Layout,
// а не как внешние Word/Excel.
// Эталон оформления: РИН-ОК-НН1-Р-ГАПТ, ГОСТ Р 21.101, ГОСТ 21.110
// .NET Framework 4.8 / AutoCAD 2018–2025
// =============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Colors;

[assembly: CommandClass(typeof(AGPT.Drawings.AgptDrawingCommands))]

namespace AGPT.Drawings
{
    // =========================================================================
    // Константы слоёв чертежа альбома
    // =========================================================================
    public static class DL
    {
        public const string App = "AGPT";
        public const string Frame = "AGPT-FRAME";   // рамка, штамп
        public const string Text = "AGPT-TEXT";     // текст общих данных / расчётов
        public const string Table = "AGPT-TABLE";   // таблицы
        public const string Scheme = "AGPT-SCHEME"; // структурная / электрическая
        public const string Axo = "AGPT-AXO";
        public const string Eq = "AGPT-EQ";
        public const string Pipe = "AGPT-PIPE";
        public const string Mark = "AGPT-MARK";
        public const string Zone = "AGPT-ZONE";

        // Форматы листов (мм, модель в мм)
        public const double A3W = 420.0, A3H = 297.0;
        public const double A2W = 594.0, A2H = 420.0;
        public const double Margin = 5.0;      // поле слева для подшивки — 20 мм по ГОСТ, упрощённо 5+20
        public const double BindLeft = 20.0;
        /// <summary>
        /// Низ рабочей области (мм). Штамп ~55 + зазор. Было 72 — контент заходил на штамп.
        /// Поднято до 90 (приоритет Frame offset по мосту с ChatGPT).
        /// </summary>
        public const double ContentBottom = 90.0;
        public const double ContentTop = 288.0;
        /// <summary>Зазор между соседними листами по X сверх ширины рамки.</summary>
        public const double SheetGapX = 80.0;
        /// <summary>Доп. зазор контента над верхом зоны штампа (мм).</summary>
        public const double StampClearance = 6.0;
    }

    /// <summary>Рабочая область листа (над штампом, внутри полей).</summary>
    public static class SheetArea
    {
        public static void Bounds(Point3d origin, out double x0, out double y0, out double x1, out double y1)
        {
            x0 = origin.X + DL.BindLeft + 4;
            y0 = origin.Y + DL.ContentBottom + DL.StampClearance;
            x1 = origin.X + DL.A3W - DL.Margin - 4;
            y1 = origin.Y + DL.ContentTop - 2;
        }

        /// <summary>Y-пол: контент и низ таблиц не ниже этой линии.</summary>
        public static double FloorY(Point3d origin) =>
            origin.Y + DL.ContentBottom + DL.StampClearance;

        /// <summary>true, если низ объекта не заходит в зону штампа.</summary>
        public static bool FitsAboveStamp(Point3d origin, double yBottom) =>
            yBottom >= FloorY(origin);

        /// <summary>Поднять yBottom, если пересекает зону штампа.</summary>
        public static double ClampAboveStamp(Point3d origin, double yBottom)
        {
            double floor = FloorY(origin);
            return yBottom < floor ? floor : yBottom;
        }
    }

    /// <summary>Диагностика в командную строку AutoCAD (не бросает исключений).</summary>
    public static class Diag
    {
        public static void Msg(string text)
        {
            try
            {
                var doc = Application.DocumentManager.MdiActiveDocument;
                if (doc != null)
                    doc.Editor.WriteMessage("\n[AGPT] " + text);
            }
            catch { }
        }
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
            Ensure(db, tr, DL.Axo, 4);
            Ensure(db, tr, DL.Eq, 1);
            Ensure(db, tr, DL.Pipe, 5);
            Ensure(db, tr, DL.Mark, 7);
            Ensure(db, tr, DL.Zone, 8);
        }
    }

    // =========================================================================
    // Примитивы чертежа (линии, текст, рамка, штамп, таблица)
    // =========================================================================
    /// <summary>
    /// Вставка рамки/штампа из эталонного DWG (WBLOCK).
    /// Ищет: c:\Work\AGPT_Plugin\blocks\AGPT_Frame_A3.dwg и рядом с DLL.
    /// </summary>
    public static class FrameBlock
    {
        /// <summary>Имя блока рамки. Сначала ищем ММХ_Штамп (из эталона), иначе AGPT_Frame_A3_v2.</summary>
        public const string BlockNamePreferred = "ММХ_Штамп";
        public const string BlockName = "AGPT_Frame_A3_v2";
        static string _resolvedPath;
        static ObjectId _cachedDefId = ObjectId.Null;

        public static void ResetCache()
        {
            _cachedDefId = ObjectId.Null;
        }

        public static string FindPath()
        {
            if (_resolvedPath != null && File.Exists(_resolvedPath)) return _resolvedPath;
            _resolvedPath = null;
            var candidates = new List<string>();
            try
            {
                string dll = Assembly.GetExecutingAssembly().Location;
                string dir = Path.GetDirectoryName(dll) ?? "";
                candidates.Add(Path.Combine(dir, "blocks", "ММХ_Штамп.dwg"));
                candidates.Add(Path.Combine(dir, "blocks", "MMH_Stamp.dwg"));
                candidates.Add(Path.Combine(dir, "blocks", "AGPT_Frame_A3.dwg"));
                candidates.Add(Path.Combine(dir, "blocks", "AGPT_Frame_A3.dxf"));
                candidates.Add(Path.Combine(dir, "..", "blocks", "AGPT_Frame_A3.dwg"));
                candidates.Add(Path.Combine(dir, "..", "blocks", "AGPT_Frame_A3.dxf"));
                candidates.Add(Path.Combine(dir, "..", "..", "blocks", "AGPT_Frame_A3.dwg"));
                candidates.Add(Path.Combine(dir, "..", "..", "blocks", "AGPT_Frame_A3.dxf"));
                candidates.Add(Path.Combine(dir, "AGPT_Frame_A3.dxf"));
                candidates.Add(Path.Combine(dir, "AGPT_Frame_A3.dwg"));
            }
            catch { }
            candidates.Add(@"c:\Work\AGPT_Plugin\blocks\MMX_ШТАМП.dwg");
            candidates.Add(@"C:\Work\AGPT_Plugin\blocks\MMX_ШТАМП.dwg");
            candidates.Add(@"c:\Work\AGPT_Plugin\blocks\ММХ_Штамп.dwg");
            candidates.Add(@"C:\Work\AGPT_Plugin\blocks\ММХ_Штамп.dwg");
            candidates.Add(@"c:\Work\AGPT_Plugin\blocks\MMH_Stamp.dwg");
            candidates.Add(@"c:\Work\AGPT_Plugin\blocks\AGPT_Frame_A3.dwg");
            candidates.Add(@"C:\Work\AGPT_Plugin\blocks\AGPT_Frame_A3.dwg");
            candidates.Add(@"c:\Work\AGPT_Plugin\blocks\AGPT_Frame_A3.dxf");
            candidates.Add(@"C:\Work\AGPT_Plugin\blocks\AGPT_Frame_A3.dxf");
            foreach (var p in candidates)
            {
                try
                {
                    string full = Path.GetFullPath(p);
                    if (File.Exists(full))
                    {
                        _resolvedPath = full;
                        return full;
                    }
                }
                catch { }
            }
            return null;
        }

        /// <summary>
        /// Импорт DWG как блок: габарит нормализуется, min → (0,0).
        /// Работает даже при неверной базовой точке WBLOCK.
        /// </summary>
        public static ObjectId EnsureDefinition(Database db, Transaction tr)
        {
            if (_cachedDefId != ObjectId.Null && !_cachedDefId.IsErased)
                return _cachedDefId;

            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            // 0) Блок уже в чертеже (вставили эталон / WBLOCK / копирование)
            foreach (string nm in new[] { BlockNamePreferred, "ММХ_Штамп", "MMH_Stamp", BlockName })
            {
                if (bt.Has(nm))
                {
                    _cachedDefId = bt[nm];
                    try
                    {
                        var btr0 = (BlockTableRecord)tr.GetObject(_cachedDefId, OpenMode.ForRead);
                        // оценить ширину по объектам блока
                        Extents3d? be = null;
                        foreach (ObjectId cid in btr0)
                        {
                            var e = tr.GetObject(cid, OpenMode.ForRead) as Entity;
                            if (e == null) continue;
                            try
                            {
                                var ee = e.GeometricExtents;
                                if (be == null) be = ee;
                                else { var x = be.Value; x.AddExtents(ee); be = x; }
                            }
                            catch { }
                        }
                        if (be.HasValue)
                        {
                            SetMeasuredSize(
                                be.Value.MaxPoint.X - be.Value.MinPoint.X,
                                be.Value.MaxPoint.Y - be.Value.MinPoint.Y);
                        }
                    }
                    catch { }
                    return _cachedDefId;
                }
            }

            // 1) Попытка загрузить DXF/DWG
            string path = FindPath();
            if (path != null)
            {
                try
                {
                    using (var src = new Database(false, true))
                    {
                        if (path.EndsWith(".dxf", StringComparison.OrdinalIgnoreCase))
                            src.DxfIn(path, null);
                        else
                            src.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, true, "");

                        var ids = new ObjectIdCollection();
                        Extents3d? ext = null;
                        using (var trSrc = src.TransactionManager.StartTransaction())
                        {
                            var btSrc = (BlockTable)trSrc.GetObject(src.BlockTableId, OpenMode.ForRead);
                            var msSrc = (BlockTableRecord)trSrc.GetObject(btSrc[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                            foreach (ObjectId eid in msSrc)
                            {
                                var ent = trSrc.GetObject(eid, OpenMode.ForRead) as Entity;
                                if (ent == null) continue;
                                ids.Add(eid);
                                try
                                {
                                    var ee = ent.GeometricExtents;
                                    if (ext == null) ext = ee;
                                    else { var x = ext.Value; x.AddExtents(ee); ext = x; }
                                }
                                catch { }
                            }
                            trSrc.Commit();
                        }

                        if (ids.Count > 0)
                        {
                            bt.UpgradeOpen();
                            var newBtr = new BlockTableRecord { Name = BlockName };
                            ObjectId newBtrId = bt.Add(newBtr);
                            tr.AddNewlyCreatedDBObject(newBtr, true);
                            var map = new IdMapping();
                            src.WblockCloneObjects(ids, newBtrId, map, DuplicateRecordCloning.Replace, false);
                            if (ext.HasValue)
                            {
                                var min = ext.Value.MinPoint;
                                var disp = Matrix3d.Displacement(new Vector3d(-min.X, -min.Y, 0));
                                var btrWrite = (BlockTableRecord)tr.GetObject(newBtrId, OpenMode.ForWrite);
                                foreach (ObjectId cid in btrWrite)
                                {
                                    var e = tr.GetObject(cid, OpenMode.ForWrite) as Entity;
                                    if (e != null) e.TransformBy(disp);
                                }
                                SetMeasuredSize(
                                    ext.Value.MaxPoint.X - ext.Value.MinPoint.X,
                                    ext.Value.MaxPoint.Y - ext.Value.MinPoint.Y);
                            }
                            _cachedDefId = newBtrId;
                            return _cachedDefId;
                        }
                    }
                }
                catch
                {
                    // DXF/DWG не прочитался — ниже программный блок
                }
            }

            // 2) Программное определение блока рамки A3 + штамп (всегда работает)
            try
            {
                _cachedDefId = BuildFrameBlockDef(db, tr, bt);
            }
            catch
            {
                _cachedDefId = ObjectId.Null;
            }
            return _cachedDefId;
        }

        /// <summary>Создать блок рамки A3+штамп геометрией (без внешнего файла).</summary>
        static ObjectId BuildFrameBlockDef(Database db, Transaction tr, BlockTable bt)
        {
            if (bt.Has(BlockName)) return bt[BlockName];
            bt.UpgradeOpen();
            var btr = new BlockTableRecord { Name = BlockName };
            ObjectId id = bt.Add(btr);
            tr.AddNewlyCreatedDBObject(btr, true);

            void AddLine(double x1, double y1, double x2, double y2)
            {
                var l = new Line(new Point3d(x1, y1, 0), new Point3d(x2, y2, 0)) { Layer = DL.Frame };
                btr.AppendEntity(l); tr.AddNewlyCreatedDBObject(l, true);
            }
            void AddRect(double x1, double y1, double x2, double y2)
            {
                AddLine(x1, y1, x2, y1); AddLine(x2, y1, x2, y2);
                AddLine(x2, y2, x1, y2); AddLine(x1, y2, x1, y1);
            }
            void AddText(double x, double y, double h, string s)
            {
                var t = new DBText
                {
                    Position = new Point3d(x, y, 0),
                    Height = h,
                    TextString = s ?? "",
                    Layer = DL.Text
                };
                btr.AppendEntity(t); tr.AddNewlyCreatedDBObject(t, true);
            }

            double W = DL.A3W, H = DL.A3H, M = DL.Margin, B = DL.BindLeft;
            AddRect(0, 0, W, H);
            AddRect(B, M, W - M, H - M);

            double sw = 185, sh = 55;
            double sx = W - M - sw, sy = M;
            AddRect(sx, sy, sx + sw, sy + sh);
            AddLine(sx, sy + 15, sx + sw, sy + 15);
            AddLine(sx, sy + 25, sx + sw, sy + 25);
            AddLine(sx, sy + 40, sx + sw, sy + 40);
            AddLine(sx + 55, sy, sx + 55, sy + 15);
            AddLine(sx + 95, sy, sx + 95, sy + 15);
            AddLine(sx + 130, sy, sx + 130, sy + 15);
            AddLine(sx + 115, sy + 25, sx + 115, sy + sh);
            AddText(sx + 2, sy + 45, 2.5, "Наименование");
            AddText(sx + 117, sy + 48, 1.5, "ТОО ТехноСтройПроект");
            AddText(sx + 117, sy + 41, 1.3, "г. Астана, 2026");
            AddText(sx + 2, sy + 30, 1.5, "Объект");
            AddText(sx + 2, sy + 17, 2.0, "Шифр");
            AddText(sx + 2, sy + 4, 1.4, "Стадия");
            AddText(sx + 20, sy + 4, 2.0, "РП");
            AddText(sx + 57, sy + 4, 1.4, "Лист");
            AddText(sx + 97, sy + 4, 1.4, "Листов");

            double sigW = 120, sigH = 55;
            double sigX = sx - sigW, sigY = sy;
            AddRect(sigX, sigY, sigX + sigW, sigY + sigH);
            AddLine(sigX, sigY + 15, sigX + sigW, sigY + 15);
            foreach (double x in new[] { 18.0, 40, 58, 78, 100 })
                AddLine(sigX + x, sigY, sigX + x, sigY + 15);
            AddText(sigX + 1, sigY + 4, 1.3, "Изм.");
            AddText(sigX + 19, sigY + 4, 1.3, "Кол.");
            AddText(sigX + 41, sigY + 4, 1.3, "Лист");
            AddText(sigX + 59, sigY + 4, 1.3, "№док");
            AddText(sigX + 79, sigY + 4, 1.3, "Подп.");
            AddText(sigX + 101, sigY + 4, 1.3, "Дата");
            string[] roles = { "Н.контр.", "Выполнил", "Проверил", "ГИП" };
            string[] names = { "Соболев М.", "Ергибаев К.", "Камал Р.", "Гайсин Д.Б." };
            double rowH = (sigH - 15) / 4.0;
            for (int i = 0; i < 4; i++)
            {
                double y1 = sigY + 15 + i * rowH;
                if (i > 0) AddLine(sigX, y1, sigX + sigW, y1);
                AddText(sigX + 1, y1 + 2.5, 1.5, roles[i]);
                AddText(sigX + 32, y1 + 2.5, 1.5, names[i]);
                AddText(sigX + 100, y1 + 2.5, 1.3, "03.26");
            }
            AddLine(sigX + 30, sigY + 15, sigX + 30, sigY + sigH);
            AddLine(sigX + 95, sigY + 15, sigX + 95, sigY + sigH);
            AddText(B + 2, M + 2, 1.4, "Инв. № подл.");
            AddText(B + 2, M + 12, 1.4, "Подп. и дата");
            AddText(B + 2, M + 22, 1.4, "Взам. инв. №");

            InsertScale = 1.0;
            FrameWidth = W;
            FrameHeight = H;
            SheetGen.Pitch = Math.Max(500.0, W + 40.0);
            return id;
        }

        /// <summary>Ширина рамки после нормализации (для шага листов).</summary>
        public static double FrameWidth = DL.A3W;
        public static double FrameHeight = DL.A3H;
        /// <summary>Масштаб вставки, если геометрия блока не в мм A3 (например 1:100).</summary>
        public static double InsertScale = 1.0;

        /// <summary>
        /// Нормализация габарита: A3 ≈ 420×297 мм.
        /// Если блок нарисован в «бумажных» единицах ×100 — масштабируем.
        /// </summary>
        public static void SetMeasuredSize(double w, double h)
        {
            if (w < 1) w = DL.A3W;
            if (h < 1) h = DL.A3H;
            // Слишком большой (часто 42000 ≈ A3×100)
            if (w > 2000 || h > 1500)
            {
                InsertScale = DL.A3W / w;
                FrameWidth = DL.A3W;
                FrameHeight = DL.A3H;
                SheetGen.Pitch = Math.Max(500.0, FrameWidth + 40.0);
                return;
            }
            // Слишком маленький
            if (w < 50)
            {
                InsertScale = DL.A3W / w;
                FrameWidth = DL.A3W;
                FrameHeight = DL.A3H;
                SheetGen.Pitch = Math.Max(500.0, FrameWidth + 40.0);
                return;
            }
            InsertScale = 1.0;
            FrameWidth = w;
            FrameHeight = h;
            SheetGen.Pitch = Math.Max(500.0, w + 40.0);
        }

        /// <summary>
        /// Одна вставка BlockReference в (ox, oy). Определение блока — один раз.
        /// Шаг между листами = FrameWidth + 30 мм.
        /// </summary>
        public static bool TryInsert(BlockTableRecord ms, Transaction tr, Database db,
            double ox, double oy)
        {
            try
            {
                ObjectId defId = EnsureDefinition(db, tr);
                if (defId == ObjectId.Null || defId.IsNull) return false;

                // Уточнить габарит блока
                try
                {
                    var btr = (BlockTableRecord)tr.GetObject(defId, OpenMode.ForRead);
                    Extents3d? be = null;
                    foreach (ObjectId cid in btr)
                    {
                        var e = tr.GetObject(cid, OpenMode.ForRead) as Entity;
                        if (e == null) continue;
                        try
                        {
                            var ee = e.GeometricExtents;
                            if (be == null) be = ee;
                            else { var x = be.Value; x.AddExtents(ee); be = x; }
                        }
                        catch { }
                    }
                    if (be.HasValue)
                    {
                        SetMeasuredSize(
                            be.Value.MaxPoint.X - be.Value.MinPoint.X,
                            be.Value.MaxPoint.Y - be.Value.MinPoint.Y);
                    }
                }
                catch { }

                var br = new BlockReference(new Point3d(ox, oy, 0), defId);
                double sc = InsertScale > 1e-9 ? InsertScale : 1.0;
                br.ScaleFactors = new Scale3d(sc, sc, 1);
                try { br.Layer = DL.Frame; } catch { }
                ms.AppendEntity(br);
                tr.AddNewlyCreatedDBObject(br, true);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

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

        /// <summary>Стиль текста AGPT: Arial (близок к эталону) или Standard.</summary>
        public static ObjectId EnsureTextStyle(Database db, Transaction tr)
        {
            var tst = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
            if (tst.Has("AGPT"))
                return tst["AGPT"];
            tst.UpgradeOpen();
            var tsr = new TextStyleTableRecord
            {
                Name = "AGPT",
                FileName = "Arial.ttf",
                TextSize = 0, // annotative / set per entity
                XScale = 1.0
            };
            try
            {
                tst.Add(tsr);
                tr.AddNewlyCreatedDBObject(tsr, true);
            }
            catch
            {
                // fallback already Standard
                return db.Textstyle;
            }
            return tsr.ObjectId;
        }

        public static ObjectId Text(BlockTableRecord btr, Transaction tr,
            double x, double y, double h, string s, string layer,
            AttachmentPoint align = AttachmentPoint.BottomLeft, short color = 7)
        {
            var t = new DBText
            {
                Position = new Point3d(x, y, 0),
                Height = h,
                TextString = s ?? "",
                Layer = layer
            };
            try
            {
                var db = btr.Database;
                if (db != null)
                {
                    using (var t2 = db.TransactionManager.StartTransaction())
                    {
                        // style may already be in parent tr; set by name if exists
                        t2.Commit();
                    }
                    var tst = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
                    if (tst.Has("AGPT"))
                        t.TextStyleId = tst["AGPT"];
                }
            }
            catch { }
            if (color != 7) t.Color = Color.FromColorIndex(ColorMethod.ByAci, color);
            if (align != AttachmentPoint.BottomLeft)
            {
                t.HorizontalMode = TextHorizontalMode.TextLeft;
                t.VerticalMode = TextVerticalMode.TextBottom;
                if (align == AttachmentPoint.BottomCenter || align == AttachmentPoint.MiddleCenter)
                {
                    t.HorizontalMode = TextHorizontalMode.TextCenter;
                    t.AlignmentPoint = new Point3d(x, y, 0);
                }
            }
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
            try
            {
                var db = btr.Database;
                var tst = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
                if (tst.Has("AGPT"))
                    mt.TextStyleId = tst["AGPT"];
            }
            catch { }
            btr.AppendEntity(mt); tr.AddNewlyCreatedDBObject(mt, true);
            return mt.ObjectId;
        }

        public static ObjectId Circle(BlockTableRecord btr, Transaction tr,
            double cx, double cy, double r, string layer, short color = 7)
        {
            var c = new Circle(new Point3d(cx, cy, 0), Vector3d.ZAxis, r);
            c.Layer = layer;
            if (color != 7) c.Color = Color.FromColorIndex(ColorMethod.ByAci, color);
            btr.AppendEntity(c); tr.AddNewlyCreatedDBObject(c, true);
            return c.ObjectId;
        }

        /// <summary>Ось: кружок с буквой/цифрой + штриховая линия сетки.</summary>
        public static void AxisBubble(BlockTableRecord btr, Transaction tr,
            double cx, double cy, string mark, bool horizontalLine, double lineLen, string layer)
        {
            double r = 3.2;
            Circle(btr, tr, cx, cy, r, layer, 7);
            Text(btr, tr, cx - mark.Length * 0.9, cy - 1.2, 2.2, mark, layer);
            // ось — пунктир сегментами
            int n = Math.Max(4, (int)(lineLen / 4.0));
            double step = lineLen / n;
            for (int i = 0; i < n; i += 2)
            {
                double a = i * step, b = Math.Min((i + 1) * step, lineLen);
                if (horizontalLine)
                    Line(btr, tr, cx + a, cy, cx + b, cy, layer, 8);
                else
                    Line(btr, tr, cx, cy - a, cx, cy - b, layer, 8);
            }
        }

        /// <summary>Отметка уровня: стрелка + текст (например −2.500).</summary>
        public static void ElevationMark(BlockTableRecord btr, Transaction tr,
            double x, double y, string elev, string layer)
        {
            // горизонталь + треугольник
            Line(btr, tr, x, y, x + 12, y, layer, 1);
            Line(btr, tr, x, y, x + 3, y + 2, layer, 1);
            Line(btr, tr, x, y, x + 3, y - 2, layer, 1);
            Line(btr, tr, x + 3, y + 2, x + 3, y - 2, layer, 1);
            Text(btr, tr, x + 14, y - 1.2, 2.0, elev, layer, AttachmentPoint.BottomLeft, 1);
        }

        /// <summary>Размерная линия с подписью (мм).</summary>
        public static void DimLine(BlockTableRecord btr, Transaction tr,
            double x0, double y0, double x1, double y1, string label, string layer)
        {
            Line(btr, tr, x0, y0, x1, y1, layer, 7);
            // засечки
            double dx = x1 - x0, dy = y1 - y0;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-6) return;
            double px = -dy / len * 1.5, py = dx / len * 1.5;
            Line(btr, tr, x0 - px, y0 - py, x0 + px, y0 + py, layer, 7);
            Line(btr, tr, x1 - px, y1 - py, x1 + px, y1 + py, layer, 7);
            Text(btr, tr, (x0 + x1) / 2 - 4, (y0 + y1) / 2 + 1.5, 1.6, label, layer);
        }


        /// <summary>
        /// Рамка + основная надпись по образцу альбома «Детский сад АГПТ 09.02.26»
        /// (ТОО ТехноСтройПроект / РП / подписи ГИП-Проверил-Выполнил-Н.контр.).
        /// </summary>

        /// <summary>
        /// Блок основной надписи с атрибутами (автозамена полей).
        /// Имя блока: AGPT_STAMP. Атрибуты: TITLE, OBJECT, CIPHER, STAGE, SHEET, SHEETS, COMPANY, CITY, GSL, GIP, CHECKED, DRAWN, NCTRL.
        /// </summary>
        public static ObjectId EnsureStampBlock(Database db, Transaction tr)
        {
            const string name = "AGPT_STAMP";
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            if (bt.Has(name)) return bt[name];

            bt.UpgradeOpen();
            var btr = new BlockTableRecord { Name = name };
            ObjectId id = bt.Add(btr);
            tr.AddNewlyCreatedDBObject(btr, true);

            double sw = 185, sh = 55;
            // outer
            var pl = new Polyline(4);
            pl.AddVertexAt(0, new Point2d(0, 0), 0, 0, 0);
            pl.AddVertexAt(1, new Point2d(sw, 0), 0, 0, 0);
            pl.AddVertexAt(2, new Point2d(sw, sh), 0, 0, 0);
            pl.AddVertexAt(3, new Point2d(0, sh), 0, 0, 0);
            pl.Closed = true;
            pl.Layer = DL.Frame;
            btr.AppendEntity(pl); tr.AddNewlyCreatedDBObject(pl, true);

            void HL(double y) {
                var l = new Line(new Point3d(0, y, 0), new Point3d(sw, y, 0)) { Layer = DL.Frame };
                btr.AppendEntity(l); tr.AddNewlyCreatedDBObject(l, true);
            }
            void VL(double x, double y0, double y1) {
                var l = new Line(new Point3d(x, y0, 0), new Point3d(x, y1, 0)) { Layer = DL.Frame };
                btr.AppendEntity(l); tr.AddNewlyCreatedDBObject(l, true);
            }
            HL(15); HL(25); HL(40);
            VL(50, 0, 15); VL(90, 0, 15); VL(125, 0, 15);
            VL(110, 25, 55);

            // Attribute defs
            void Attr(string tag, string prompt, double ax, double ay, double h, string def)
            {
                var ad = new AttributeDefinition();
                ad.Position = new Point3d(ax, ay, 0);
                ad.Tag = tag;
                ad.Prompt = prompt;
                ad.TextString = def;
                ad.Height = h;
                ad.Layer = DL.Text;
                ad.HorizontalMode = TextHorizontalMode.TextLeft;
                ad.VerticalMode = TextVerticalMode.TextBottom;
                ad.AlignmentPoint = ad.Position;
                btr.AppendEntity(ad); tr.AddNewlyCreatedDBObject(ad, true);
            }
            Attr("TITLE", "Наименование листа", 2, 45, 2.5, "Лист");
            Attr("COMPANY", "Организация", 112, 48, 1.6, "ТОО ТехноСтройПроект");
            Attr("CITY", "Город", 112, 41, 1.4, "г. Астана, 2026 г.");
            Attr("GSL", "ГСЛ", 112, 28, 1.4, "ГСЛ N 20013501");
            Attr("OBJECT", "Объект", 2, 30, 1.6, "Объект");
            Attr("CIPHER", "Шифр", 2, 17, 2.2, "XX-XX/X-АГПТ");
            Attr("STAGE", "Стадия", 22, 4, 2.2, "РП");
            Attr("SHEET", "Лист", 68, 4, 2.2, "1");
            Attr("SHEETS", "Листов", 130, 4, 2.2, "9");

            // Signature block attached to left of stamp in same block? separate - keep in sheet frame
            return id;
        }

        public static void InsertStamp(BlockTableRecord ms, Transaction tr, Database db,
            double sx, double sy,
            string title, string obj, string cipher, string stage, string sheet, string sheets,
            string company, string city, string gsl)
        {
            try
            {
                ObjectId defId = EnsureStampBlock(db, tr);
                var br = new BlockReference(new Point3d(sx, sy, 0), defId);
                br.Layer = DL.Frame;
                ms.AppendEntity(br);
                tr.AddNewlyCreatedDBObject(br, true);

                var btr = (BlockTableRecord)tr.GetObject(defId, OpenMode.ForRead);
                if (!btr.HasAttributeDefinitions) return;
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    { "TITLE", title ?? "" },
                    { "OBJECT", obj ?? "" },
                    { "CIPHER", cipher ?? "" },
                    { "STAGE", stage ?? "РП" },
                    { "SHEET", sheet ?? "" },
                    { "SHEETS", sheets ?? "" },
                    { "COMPANY", company ?? "" },
                    { "CITY", city ?? "" },
                    { "GSL", gsl ?? "" },
                };
                foreach (ObjectId aid in btr)
                {
                    var ad = tr.GetObject(aid, OpenMode.ForRead) as AttributeDefinition;
                    if (ad == null || ad.Constant) continue;
                    var ar = new AttributeReference();
                    ar.SetAttributeFromBlock(ad, br.BlockTransform);
                    if (map.ContainsKey(ad.Tag)) ar.TextString = map[ad.Tag];
                    br.AttributeCollection.AppendAttribute(ar);
                    tr.AddNewlyCreatedDBObject(ar, true);
                }
            }
            catch { }
        }


        public static void SheetFrame(BlockTableRecord btr, Transaction tr,
            double ox, double oy, double w, double h,
            string cipher, string sheetTitle, string sheetNo, string sheetsTotal,
            string objectName, string stage = "РП",
            string company = "ТОО «ТехноСтройПроект»",
            string companyGsl = "ГСЛ № 20013501",
            string cityYear = "г. Астана, 2026 г.",
            string gip = "Гайсин Д.Б.", string checkedBy = "Камал Р.",
            string drawn = "Ергибаев К.", string normCtrl = "Соболев М.")
        {
            double left = ox + DL.BindLeft;
            double bottom = oy + DL.Margin;
            double right = ox + w - DL.Margin;
            double top = oy + h - DL.Margin;

            // Основной путь: блок AGPT_Frame_A3 (DXF/DWG) — рамка + штамп по ГОСТ
            bool usedBlock = false;
            try
            {
                var db = btr.Database;
                if (db != null)
                    usedBlock = FrameBlock.TryInsert(btr, tr, db, ox, oy);
            }
            catch { usedBlock = false; }

            if (usedBlock)
            {
                // Блок уже содержит рамку и основную надпись.
                // НЕ рисуем текст поверх штампа — это давало «наложение на угловой штамп».
                // Заголовок листа — только в рабочей зоне над штампом (один раз, мелко).
                string titleShort = sheetTitle ?? "";
                if (titleShort.Length > 48) titleShort = titleShort.Substring(0, 46) + "…";
                Text(btr, tr, left + 2, oy + DL.ContentTop - 3, 2.0,
                    (cipher ?? "") + "  ·  " + titleShort + "  ·  л." + (sheetNo ?? ""), DL.Text);
                return;
            }

            // Fallback: программная рамка
            Rect(btr, tr, ox, oy, ox + w, oy + h, DL.Frame);
            Rect(btr, tr, left, bottom, right, top, DL.Frame);

            // ===== Основная надпись (ГОСТ) + атрибутный блок AGPT_STAMP =====
            double sw = 185, sh = 55;
            double sx = right - sw;
            double sy = bottom;

            // Атрибутный штамп (поля редактируются через ЕАТТ / диспетчер атрибутов)
            InsertStamp(btr, tr, btr.Database, sx, sy,
                sheetTitle, objectName, cipher, stage, sheetNo, sheetsTotal,
                company, cityYear, companyGsl);

            // Подписи «Стадия/Лист» как статичный текст (атрибуты в блоке AGPT_STAMP)
            Text(btr, tr, sx + 2, sy + 4, 1.5, "Стадия", DL.Text);
            Text(btr, tr, sx + 52, sy + 4, 1.5, "Лист", DL.Text);
            Text(btr, tr, sx + 92, sy + 4, 1.5, "Листов", DL.Text);

            // --- Блок подписей слева от штампа (форма изменений + ГИП…) ---
            double sigW = 105, sigH = 55;
            double sigX = sx - sigW - 1;
            double sigY = sy;
            Rect(btr, tr, sigX, sigY, sigX + sigW, sigY + sigH, DL.Frame);
            // Строка изменений
            Line(btr, tr, sigX, sigY + 12, sigX + sigW, sigY + 12, DL.Frame);
            Text(btr, tr, sigX + 1, sigY + 3, 1.4, "Изм.", DL.Text);
            Text(btr, tr, sigX + 18, sigY + 3, 1.4, "Кол.уч", DL.Text);
            Text(btr, tr, sigX + 42, sigY + 3, 1.4, "Лист", DL.Text);
            Text(btr, tr, sigX + 62, sigY + 3, 1.4, "№док.", DL.Text);
            Text(btr, tr, sigX + 85, sigY + 3, 1.4, "Подп.", DL.Text);
            Line(btr, tr, sigX + 16, sigY, sigX + 16, sigY + 12, DL.Frame);
            Line(btr, tr, sigX + 40, sigY, sigX + 40, sigY + 12, DL.Frame);
            Line(btr, tr, sigX + 58, sigY, sigX + 58, sigY + 12, DL.Frame);
            Line(btr, tr, sigX + 80, sigY, sigX + 80, sigY + 12, DL.Frame);

            // Подписи: 4 строки × (должность | ФИО | дата)
            double rowH = (sigH - 12) / 4.0;
            string[] roles = { "ГИП", "Проверил", "Выполнил", "Н.контр." };
            string[] names = { gip ?? "", checkedBy ?? "", drawn ?? "", normCtrl ?? "" };
            for (int i = 0; i < 4; i++)
            {
                double y1 = sigY + 12 + i * rowH;
                double y2 = y1 + rowH;
                if (i > 0) Line(btr, tr, sigX, y1, sigX + sigW, y1, DL.Frame);
                Text(btr, tr, sigX + 1, y1 + 2.5, 1.6, roles[3 - i], DL.Text);
                Text(btr, tr, sigX + 32, y1 + 2.5, 1.6, names[3 - i], DL.Text);
                Text(btr, tr, sigX + 85, y1 + 2.5, 1.4, "03.26", DL.Text);
            }
            Line(btr, tr, sigX + 30, sigY + 12, sigX + 30, sigY + sigH, DL.Frame);
            Line(btr, tr, sigX + 82, sigY + 12, sigX + 82, sigY + sigH, DL.Frame);

            // Угловые надписи слева (инвентарные)
            Text(btr, tr, left + 2, bottom + 2, 1.5, "Инв. № подл.", DL.Text);
            Text(btr, tr, left + 2, bottom + 12, 1.5, "Подп. и дата", DL.Text);
            Text(btr, tr, left + 2, bottom + 22, 1.5, "Взам. инв. №", DL.Text);
        }

        /// <summary>Упрощённый вызов штампа из ProjectData.</summary>
        public static void SheetFrame(BlockTableRecord btr, Transaction tr,
            double ox, double oy, double w, double h,
            ProjectData pd, string sheetTitle, string sheetNo, string sheetsTotal)
        {
            SheetFrame(btr, tr, ox, oy, w, h,
                pd.Cipher, sheetTitle, sheetNo, sheetsTotal,
                pd.ObjectName, pd.Stage,
                pd.Company, pd.CompanyGsl, pd.CityYear,
                pd.Gip, pd.Checked, pd.Drawn, pd.NormCtrl);
        }

        /// <summary>
        /// Таблица AutoCAD (AcDbTable). (x,y) — левый верх.
        /// При ошибке API — fallback линиями.
        /// </summary>
        /// <summary>Таблица. Возвращает высоту занятой области (мм) для позиционирования следующих элементов.</summary>
        public static double Table(BlockTableRecord btr, Transaction tr,
            double x, double y, double[] colW, double rowH,
            string[] headers, List<string[]> rows, string layer)
        {
            int cols = colW.Length;
            int nData = rows != null ? rows.Count : 0;
            int nRows = 1 + nData;
            double height = nRows * rowH;
            // Frame offset: если таблица уходит ниже ContentBottom — поднять верх
            // (y = верх таблицы; низ = y - height). origin листа ≈ (floor(x/pitch)*pitch, 0).
            try
            {
                double sheetOx = Math.Floor((x + 1.0) / Math.Max(1.0, SheetGen.Pitch)) * SheetGen.Pitch;
                var origin = new Point3d(sheetOx, 0, 0);
                double floor = SheetArea.FloorY(origin);
                if (y - height < floor)
                    y = floor + height;
            }
            catch { }
            try
            {
                var table = new Table();
                table.SetDatabaseDefaults(btr.Database);
                table.TableStyle = btr.Database.Tablestyle;
                // Position = верхний левый угол таблицы
                table.Position = new Point3d(x, y, 0);
                table.SetSize(nRows, cols);
                table.Layer = layer;
                for (int c = 0; c < cols; c++)
                {
                    try { table.Columns[c].Width = colW[c]; }
                    catch { table.SetColumnWidth(c, colW[c]); }
                }
                for (int r = 0; r < nRows; r++)
                {
                    try { table.Rows[r].Height = rowH; }
                    catch { table.SetRowHeight(r, rowH); }
                }

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
            catch
            {
                // fallback — линии
            }

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
                Text(btr, tr, xx + 1, y - rowH + 1.2, 2.2, headers[c], layer);
                xx += colW[c];
            }
            for (int r = 0; r < nData; r++)
            {
                xx = x;
                for (int c = 0; c < cols && c < rows[r].Length; c++)
                {
                    Text(btr, tr, xx + 1, y - (r + 2) * rowH + 1.2, 2.0, rows[r][c], layer);
                    xx += colW[c];
                }
            }
            return height;
        }

        // ----- УГО пожарной автоматики (упрощённо по ГОСТ/практике Рубеж) -----

        /// <summary>Извещатель дымовой — круг с крестом.</summary>
        public static void UgoIp(BlockTableRecord btr, Transaction tr, double cx, double cy, double r, string mark)
        {
            // Сначала блок из Садик.dwg / R-CAD (если загружен в чертёж)
            try
            {
                if (UgoLib.TryInsert(btr, tr, btr.Database, UgoLib.IpNames, cx, cy, 0))
                {
                    if (!string.IsNullOrEmpty(mark))
                        Text(btr, tr, cx + r + 0.8, cy - 0.5, Math.Max(1.2, r * 0.9), mark, DL.Mark);
                    return;
                }
            }
            catch { }
            try
            {
                var cir = new Circle(new Point3d(cx, cy, 0), Vector3d.ZAxis, r) { Layer = DL.Scheme };
                btr.AppendEntity(cir); tr.AddNewlyCreatedDBObject(cir, true);
            }
            catch
            {
                Rect(btr, tr, cx - r, cy - r, cx + r, cy + r, DL.Scheme, 3);
            }
            Line(btr, tr, cx - r * 0.7, cy, cx + r * 0.7, cy, DL.Scheme, 3);
            Line(btr, tr, cx, cy - r * 0.7, cx, cy + r * 0.7, DL.Scheme, 3);
            if (!string.IsNullOrEmpty(mark))
                Text(btr, tr, cx + r + 0.8, cy - 0.5, Math.Max(1.2, r * 0.9), mark, DL.Mark);
        }

        /// <summary>Модуль газовый — прямоугольник + баллон.</summary>
        public static void UgoModule(BlockTableRecord btr, Transaction tr, double x, double y, string mark)
        {
            try {
                if (UgoLib.TryInsert(btr, tr, btr.Database, UgoLib.ModuleNames, x + 4, y + 6, 0))
                {
                    if (!string.IsNullOrEmpty(mark))
                        Text(btr, tr, x, y + 16.5, 1.4, mark, DL.Mark);
                    return;
                }
            } catch { }

            // корпус
            Rect(btr, tr, x, y, x + 8, y + 12, DL.Eq, 1);
            // горловина
            Line(btr, tr, x + 2, y + 12, x + 2, y + 15, DL.Eq, 1);
            Line(btr, tr, x + 6, y + 12, x + 6, y + 15, DL.Eq, 1);
            Line(btr, tr, x + 2, y + 15, x + 6, y + 15, DL.Eq, 1);
            Text(btr, tr, x, y + 16.5, 1.4, mark ?? "МГП", DL.Mark);
        }

        /// <summary>Насадок — треугольник остриём вниз.</summary>
        public static void UgoNozzle(BlockTableRecord btr, Transaction tr, double cx, double cy, string mark)
        {
            try {
                if (UgoLib.TryInsert(btr, tr, btr.Database, UgoLib.NozzleNames, cx, cy, 0))
                {
                    if (!string.IsNullOrEmpty(mark))
                        Text(btr, tr, cx + 2.5, cy - 1, 1.3, mark, DL.Mark);
                    return;
                }
            } catch { }

            double s = 3.5;
            Line(btr, tr, cx, cy + s * 0.6, cx - s * 0.7, cy - s * 0.5, DL.Pipe, 1);
            Line(btr, tr, cx - s * 0.7, cy - s * 0.5, cx + s * 0.7, cy - s * 0.5, DL.Pipe, 1);
            Line(btr, tr, cx + s * 0.7, cy - s * 0.5, cx, cy + s * 0.6, DL.Pipe, 1);
            if (!string.IsNullOrEmpty(mark))
                Text(btr, tr, cx + 2.5, cy - 1, 1.3, mark, DL.Mark);
        }

        /// <summary>Световое табло (ОПОП) — прямоугольник с текстом.</summary>
        public static void UgoTablo(BlockTableRecord btr, Transaction tr, double x, double y, double w, double h, string text)
        {
            try {
                if (UgoLib.TryInsert(btr, tr, btr.Database, UgoLib.TabloNames, x + w/2, y + h/2, 0))
                {
                    if (!string.IsNullOrEmpty(text))
                        Text(btr, tr, x + 0.5, y - 2.5, 1.3, text, DL.Mark);
                    return;
                }
            } catch { }

            Rect(btr, tr, x, y, x + w, y + h, DL.Scheme, 3);
            // заливка-имитация: диагональ
            Line(btr, tr, x, y, x + w, y + h, DL.Scheme, 3);
            Text(btr, tr, x + 0.8, y + h * 0.25, Math.Min(1.5, h * 0.45), text ?? "", DL.Mark);
        }

        /// <summary>УДП — ромб.</summary>
        public static void UgoUdp(BlockTableRecord btr, Transaction tr, double cx, double cy, string mark)
        {
            try {
                if (UgoLib.TryInsert(btr, tr, btr.Database, UgoLib.UdpNames, cx, cy, 0))
                {
                    if (!string.IsNullOrEmpty(mark))
                        Text(btr, tr, cx + 3.5, cy - 0.5, 1.3, mark, DL.Mark);
                    return;
                }
            } catch { }

            double s = 3.0;
            Line(btr, tr, cx, cy + s, cx + s, cy, DL.Scheme, 3);
            Line(btr, tr, cx + s, cy, cx, cy - s, DL.Scheme, 3);
            Line(btr, tr, cx, cy - s, cx - s, cy, DL.Scheme, 3);
            Line(btr, tr, cx - s, cy, cx, cy + s, DL.Scheme, 3);
            Text(btr, tr, cx + s + 0.5, cy - 0.5, 1.3, mark ?? "УДП", DL.Mark);
        }

        /// <summary>МПТ — модуль ПТ (прибор) — квадрат с буквой.</summary>
        public static void UgoMpt(BlockTableRecord btr, Transaction tr, double x, double y, string mark)
        {
            try {
                if (UgoLib.TryInsert(btr, tr, btr.Database, UgoLib.MptNames, x + 5, y + 4, 0))
                {
                    if (!string.IsNullOrEmpty(mark))
                        Text(btr, tr, x, y + 9, 1.3, mark, DL.Mark);
                    return;
                }
            } catch { }

            Rect(btr, tr, x, y, x + 10, y + 8, DL.Eq, 1);
            Text(btr, tr, x + 1.5, y + 2.5, 1.8, "МПТ", DL.Mark);
            if (!string.IsNullOrEmpty(mark))
                Text(btr, tr, x, y + 9, 1.3, mark, DL.Mark);
        }

        /// <summary>Кабель АЛС — ломаная (условно штрих цветом слоя).</summary>
        public static void UgoCable(BlockTableRecord btr, Transaction tr, double x1, double y1, double x2, double y2)
        {
            Line(btr, tr, x1, y1, x2, y2, DL.Scheme, 3);
        }

    }

    // =========================================================================
    // Модель данных проекта (собирается с чертежа + расчёт)
    // =========================================================================
    public class ProjectData
    {
        // Эталон: Детский сад АГПТ 09.02.26 (Астана, 39-25/1)
        public string Cipher = "39-25/1-АГПТ";
        public string ObjectName = "Строительство детского сада, расположенного по адресу: город Астана, район «Есиль», ул. Е 779, уч. 26";
        public string ObjectShort = "Детский сад, г. Астана";
        public string Stage = "РП";
        public string CityYear = "г. Астана, 2026 г.";
        public string Company = "ТОО «ТехноСтройПроект»";
        public string CompanyGsl = "ГСЛ № 20013501";
        public string GenDesigner = "ТОО «КазНИПИ энергетики и связи» ГСЛ №02913";
        public string VolumeTitle = "Автоматическое газовое пожаротушение";
        public string VolumeNo = "Том 4";
        public string Gip = "Гайсин Д.Б.";
        public string Checked = "Камал Р.";
        public string Drawn = "Ергибаев К.";
        public string NormCtrl = "Соболев М.";
        public List<ZoneCalc> Zones = new List<ZoneCalc>();
        public List<SpecRow> Spec = new List<SpecRow>();
        public List<CableRow> Cables = new List<CableRow>();
        public List<PipeSeg> Pipes = new List<PipeSeg>();
        public HydroSummary Hydro = new HydroSummary();
    }

    public class ZoneCalc
    {
        public string Name;
        public double Vp, H, Fh, Tm, Cn, Rho0, K4, Vtr;
        public string Gotv = "HFC-227ea";
        public double Mp, Mg, Fc;
        public int ModuleVol;
        public int ModuleCountWork = 1, ModuleCountRes = 1;
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

    public class PipeSeg
    {
        public string Id, Type;
        public int Dy;
        public double L, Dp, POut, Npsh, DpHam;
        public bool Cav, Ham;
    }

    public class HydroSummary
    {
        public double TPod = 10, TZpu = 0.15, TCrit, DpTotal;
        public string HammerMode = "partial";
        public bool HasCav, HasHam;
    }

    // =========================================================================
    // Расчёт массы ГОТВ (СП 485 / эталон «Детский сад АГПТ»)
    // Mp = Vp·ρ1·(1+K2)·ln(100/(100−Cn))
    // K2 = Π·δ·tпод·√H ;  δ = FH/Vp ;  ρ1 = ρ0·(T0/Tм)·K3
    // Mg = K1·(Mp + Mтр)  → округление вверх до целых кг
    // =========================================================================
    public static class Calc
    {
        public const double PiLeak = 0.65;   // Π — параметр расположения проёмов
        public const double TPod = 10.0;     // с
        public const double T0 = 293.15;     // K (20 °C)
        public const double K3 = 1.0;        // коэффициент высоты (упрощ.)
        public const double K1 = 1.05;       // запас

        public static void Mass(ZoneCalc z)
        {
            if (z.Vp <= 0) z.Vp = 1;
            if (z.Cn <= 0 || z.Cn >= 100) z.Cn = 7.2;
            if (z.Rho0 <= 0) z.Rho0 = 7.28;
            if (z.H <= 0) z.H = 2.4;
            if (z.K4 <= 0) z.K4 = 1.0;

            double TmK = z.Tm + 273.15;
            if (TmK < 200) TmK = 298.15;

            // ρ1 = ρ0 · (T0 / Tм) · K3
            double rho1 = z.Rho0 * (T0 / TmK) * K3;

            // δ = ΣFH / Vp
            double delta = z.Fh / z.Vp;

            // K2 = Π · δ · tпод · √H
            double k2 = PiLeak * delta * TPod * Math.Sqrt(z.H);

            // Mp = Vp · ρ1 · (1+K2) · ln(100/(100−Cn))
            z.Mp = z.Vp * rho1 * (1.0 + k2) * Math.Log(100.0 / (100.0 - z.Cn));

            // Масса остатка в трубопроводах Mтр = Vтр · ρ_GOTV
            // ρ жидкой фазы HFC-227ea ≈ 1400 кг/м³ (оценка); в эталоне Mтр ~0.001–0.002 кг при малых Vтр
            double rhoLiq = 1400.0;
            double mTr = z.Vtr * rhoLiq;
            // если Vтр задан как объём пара/смеси — в эталоне Mтр ≈ Vтр·ρ1·коэфф.; берём max
            mTr = Math.Max(mTr, z.Vtr * rho1 * 2.5);

            // Mg = K1 · (Mp·K4 + Mтр), округление вверх до целых кг (как в альбоме: 9, 10, 11, 14 кг)
            double mgRaw = K1 * (z.Mp * z.K4 + mTr);
            z.Mg = Math.Ceiling(mgRaw);

            // КСИД (упрощённо): требуемая площадь сбросных устройств
            double vOut = 0.7 * Math.Sqrt(2.0 * 0.005 * 1e6 / 1.2);
            double fReq = 1.2 * z.Mp / (rho1 * TPod * Math.Max(vOut, 1.0));
            z.Fc = Math.Max(0, fReq - z.Fh);

            // Подбор модуля: Flamex 20 кг заправка (как в эталоне 66-40-33)
            // если Mg <= 20 → 40л / 20кг; иначе кратно
            double fillKg = 20.0;
            int need = Math.Max(1, (int)Math.Ceiling(z.Mg / fillKg));
            z.ModuleCountWork = need;
            z.ModuleCountRes = need; // 100% резерв по эталону
            z.ModuleVol = 40;        // типоразмер баллона
        }
    }

    // =========================================================================
    // Генераторы листов (всё — в Model Space как «поля» чертежа)
    // Листы раскладываются по оси X: лист N начинается с originX = N * pitch
    // =========================================================================
    public static class SheetGen
    {
        /// <summary>Шаг по X: ширина рамки + SheetGapX (после загрузки блока уточняется).</summary>
        public static double Pitch = DL.A3W + DL.SheetGapX;

        public static void SyncPitchFromFrame()
        {
            double w = FrameBlock.FrameWidth > 10 ? FrameBlock.FrameWidth : DL.A3W;
            Pitch = Math.Max(DL.A3W + DL.SheetGapX, w * FrameBlock.InsertScale + DL.SheetGapX);
        }

        public static Point3d Origin(int sheetIndex) =>
            new Point3d(sheetIndex * Pitch, 0, 0);

        // ----- Лист: Общие данные (двухколоночный, как в альбоме Детский сад АГПТ) -----
        public static void GeneralData(BlockTableRecord ms, Transaction tr, ProjectData pd, int idx)
        {
            var o = Origin(idx);
            Draw.SheetFrame(ms, tr, o.X, o.Y, DL.A3W, DL.A3H, pd,
                "Общие данные", "3", "12");

            SheetArea.Bounds(o, out double x0, out double y0, out double x1, out double y1);
            double leftX = x0;
            double rightX = x0 + 195;
            double yTop = y1 - 6;
            double y = yTop;
            double yFloor = y0 + 4;

            // === Левая колонка ===
            Draw.Text(ms, tr, leftX, y, 2.4, "Ведомость рабочих чертежей основного комплекта", DL.Text);
            y -= 4.2;
            var ved = new List<string[]>
            {
                new[] { "1", "Титул", "" },
                new[] { "2", "Титул (подписи)", "" },
                new[] { "3", "Общие данные", "" },
                new[] { "4", "Описание модулей МПТГ", "" },
                new[] { "5", "Структурная схема. Подключение", "" },
                new[] { "6", "План: оборудование и кабель", "" },
                new[] { "7", "План: модули и трубопровод", "" },
                new[] { "8", "Аксонометрия трубопровода", "" },
                new[] { "9", "Расчёт газового пожаротушения", "" },
                new[] { "10", "Схема монтажа", "" },
                new[] { "11", "Спецификация .СО", "" },
                new[] { "12", "Кабельный журнал", "" },
            };
            if (y - (ved.Count + 1) * 4.2 < yFloor) { /* уплотнение */ }
            double hVed = Draw.Table(ms, tr, leftX, y, new[] { 12.0, 100.0, 55.0 }, 4.0,
                new[] { "Лист", "Наименование", "Примечание" }, ved, DL.Table);
            y -= hVed + 5;
            if (y < yFloor + 40) y = yFloor + 42;

            Draw.Text(ms, tr, leftX, y, 2.2, "Ведомость ссылочных и прилагаемых документов", DL.Text);
            y -= 4.0;
            var refDocs = new List<string[]>
            {
                new[] { "СП 484.1311500.2020", "СПС. Нормы и правила", "" },
                new[] { "СП 485.1311500.2020", "АУПТ. Нормы и правила", "" },
                new[] { "СП 486.1311500.2020", "Перечень зданий…", "" },
                new[] { "СП РК 2.02-102-2022", "Пожарная автоматика", "" },
                new[] { "ПУЭ РК 2022", "Правила устройства ЭУ РК", "" },
            };
            double hRef = Draw.Table(ms, tr, leftX, y, new[] { 48.0, 95.0, 22.0 }, 4.0,
                new[] { "Обозначение", "Наименование", "Прим." }, refDocs, DL.Table);
            y -= hRef + 4;
            if (y < yFloor + 30) y = yFloor + 32;

            Draw.Text(ms, tr, leftX, y, 2.0, "Состав установки АГПТ (7 частей, СП 485)", DL.Text);
            y -= 3.6;
            var parts = new List<string[]>
            {
                new[] { "1", "Прибор управления (МПТ-1-R3)" },
                new[] { "2", "Извещатели пожарные (ИП 212-64-R3)" },
                new[] { "3", "Модули газовые (Flamex) + ЗПУ" },
                new[] { "4", "Трубопровод и насадки" },
                new[] { "5", "Оповещатели / табло / УДП" },
                new[] { "6", "Источники питания (I кат.)" },
                new[] { "7", "Кабельные линии КПСнг(А)-FRLS" },
            };
            // не заходить в штамп
            int maxParts = parts.Count;
            while (maxParts > 3 && y - (maxParts + 1) * 3.8 < yFloor)
                maxParts--;
            if (maxParts < parts.Count)
                parts = parts.GetRange(0, maxParts);
            Draw.Table(ms, tr, leftX, y, new[] { 14.0, 150.0 }, 3.8,
                new[] { "№", "Часть установки" }, parts, DL.Table);

            // === Правая колонка (текст не ниже штампа) ===
            double ry = yTop;
            Draw.Text(ms, tr, rightX, ry, 2.6, "Общие данные", DL.Text);
            ry -= 5;
            string odText =
                "Альбом — часть проекта «" + (pd.ObjectName ?? pd.ObjectShort ?? "Объект") + "». " +
                "Модульное газовое пожаротушение (РУБЕЖ / Flamex, HFC-227ea).\n\n" +
                "Нормы: СП 484, СП 485, СП 486; СП РК 2.02-102-2022. Оборудование сертифицировано в РК.\n\n" +
                "Прибор — МПТ-1-R3. Пуск — только алгоритм C. Задержка эвакуации ≥ 10 с. " +
                "tпод ≤ 10 с. Резерв модуля 100 %.\n\n" +
                "Электроснабжение — I категория. Кабель — КПСнг(А)-FRLS.\n\n" +
                "Зоны: кроссовые и электрощитовые, разводка Ду15…Ду32 по гидравлике.";
            Draw.MText(ms, tr, rightX, ry, Math.Min(200, x1 - rightX - 4), 1.9, odText, DL.Text);
        }

        // ----- Лист: Структурная схема + схема подключения -----
        public static void StructuralScheme(BlockTableRecord ms, Transaction tr, ProjectData pd, int idx)
        {
            var o = Origin(idx);
            Draw.SheetFrame(ms, tr, o.X, o.Y, DL.A3W, DL.A3H, pd,
                "Структурная схема", "4", "12");

            // ---- Левая часть: структурная схема АУГП ----
            double cx = o.X + 120, cy = o.Y + 195;

            void Box(double x, double y, double w, double h, string label, short col)
            {
                Draw.Rect(ms, tr, x - w / 2, y - h / 2, x + w / 2, y + h / 2, DL.Scheme, col);
                Draw.Text(ms, tr, x - w / 2 + 1.5, y - 1.2, 1.9, label, DL.Scheme);
            }
            void Arr(double x0, double y0, double x1, double y1)
            {
                Draw.Line(ms, tr, x0, y0, x1, y1, DL.Scheme, 3);
            }

            Draw.Text(ms, tr, o.X + 30, o.Y + 265, 2.6, "1. Структурная схема АУГП", DL.Text);

            Box(cx, cy + 45, 95, 16, "ППКУП / верхний уровень АПС", 3);
            Arr(cx, cy + 37, cx, cy + 28);
            Box(cx, cy + 20, 85, 14, "МПТ-1-R3 (ППУ АУГП)", 1);
            Arr(cx - 28, cy + 13, cx - 70, cy + 4);
            Arr(cx, cy + 13, cx, cy + 4);
            Arr(cx + 28, cy + 13, cx + 70, cy + 4);

            Box(cx - 70, cy - 4, 48, 12, "ИП ×2 (алг. C)", 3);
            Box(cx, cy - 4, 40, 12, "УДП", 3);
            Box(cx + 70, cy - 4, 55, 12, "Табло ОПОП ×3", 6);

            Arr(cx, cy - 10, cx, cy - 20);
            Box(cx - 35, cy - 28, 50, 12, "МГП рабочий", 1);
            Box(cx + 35, cy - 28, 50, 12, "МГП резерв", 8);
            Arr(cx - 35, cy - 34, cx - 35, cy - 42);
            Arr(cx + 35, cy - 34, cx - 35, cy - 42);
            Box(cx - 35, cy - 50, 75, 12, "Трубопровод → насадки", 5);

            // ---- Правая часть: схема подключения (клеммы / цепи) ----
            double rx = o.X + 250;
            Draw.Text(ms, tr, rx, o.Y + 265, 2.6, "2. Схема подключения оборудования", DL.Text);

            // Рамка таблицы клемм
            Draw.Rect(ms, tr, rx, o.Y + 80, rx + 145, o.Y + 255, DL.Scheme, 7);
            Draw.Text(ms, tr, rx + 4, o.Y + 245, 2.0, "МПТ-1-R3", DL.Text);
            Draw.Text(ms, tr, rx + 4, o.Y + 235, 1.7, "Цепь          Оборудование", DL.Text);
            Draw.Line(ms, tr, rx + 2, o.Y + 232, rx + 143, o.Y + 232, DL.Scheme);

            string[][] links = {
                new[] { "АЛС1", "ИП 212-64-R3 (1, 2)" },
                new[] { "АЛС1", "УДП 513-11-A-R3" },
                new[] { "АЛС1", "ОПОП «Газ уходи»" },
                new[] { "АЛС1", "ОПОП «Газ не входи»" },
                new[] { "АЛС1", "ОПОП «Автоматика откл.»" },
                new[] { "Выход 1", "ЗПУ модуля рабочего" },
                new[] { "Выход 2", "ЗПУ модуля резерва" },
                new[] { "Питание", "ИВЭПР 24/2.5 RS-R3" },
                new[] { "СДУ", "СДУ-М (давление)" },
            };
            double ly = o.Y + 220;
            foreach (var row in links)
            {
                Draw.Text(ms, tr, rx + 4, ly, 1.7, row[0], DL.Text);
                Draw.Text(ms, tr, rx + 40, ly, 1.7, row[1], DL.Text);
                ly -= 12;
            }

            // Таблица цепей (AcDbTable)
            double tx = o.X + 30, ty = o.Y + 100;
            var chainRows = new List<string[]>
            {
                new[] { "1", "АЛС1", "ИП 212-64-R3", "2", "алгоритм C" },
                new[] { "2", "АЛС1", "УДП 513-11-A-R3", "1", "ручной пуск" },
                new[] { "3", "АЛС1", "ОПОП «Газ! Уходи!»", "1", "" },
                new[] { "4", "АЛС1", "ОПОП «Газ! Не входи!»", "1", "" },
                new[] { "5", "АЛС1", "ОПОП «Автоматика откл.»", "1", "" },
                new[] { "6", "Выход 1", "ЗПУ модуля рабочего", "1", "ЭМУ QDQ 90N" },
                new[] { "7", "Выход 2", "ЗПУ модуля резерва", "1", "100% резерв" },
                new[] { "8", "Питание", "ИВЭПР 24/2.5 RS-R3", "1", "I кат." },
                new[] { "9", "СДУ", "СДУ-М", "1", "давление" },
            };
            Draw.Table(ms, tr, tx, ty, new[] { 12.0, 28.0, 70.0, 18.0, 40.0 }, 4.5,
                new[] { "№", "Цепь", "Оборудование", "Кол.", "Примечание" }, chainRows, DL.Table);

            Draw.Text(ms, tr, o.X + 30, o.Y + 42, 2.0,
                "Пуск: алгоритм C (2 ИП) → задержка ≥10 с → откл. вентиляции → ЗПУ → выпуск ≤10 с", DL.Text);
            Draw.Text(ms, tr, o.X + 30, o.Y + 32, 1.8,
                "Кабель: КПСнг(А)-FRLS 2×2×0,75. Питание I категория. Резерв модуля 100%.", DL.Text);
        }

        // ----- Лист: Аксонометрия (стиль эталона — 4 зоны, простые схемы с Ду/L) -----
        public static void Axonometry(BlockTableRecord ms, Transaction tr, ProjectData pd, int idx)
        {
            var o = Origin(idx);
            Draw.SheetFrame(ms, tr, o.X, o.Y, DL.A3W, DL.A3H, pd,
                "Аксонометрическая схема разводки трубопровода", "7", "12");

            string[] zoneNames = pd.Zones.Count >= 4
                ? pd.Zones.Take(4).Select(z => z.Name).ToArray()
                : new[] { "Кроссовая 1", "Электрощитовая 1", "Кроссовая 2", "Электрощитовая 2" };

            // Участки: стояк, горизонт1, горизонт2, опуск к насадку (мм)
            double[][] lengths = {
                new[] { 1500.0, 1200, 1000, 100 },
                new[] { 1500.0, 1200, 1100, 100 },
                new[] { 1500.0, 1600, 900, 100 },
                new[] { 1500.0, 600, 900, 100 }
            };
            int[] dys = { 25, 20, 20, 15 }; // Ду по участкам

            // Схемы в верхней половине листа (над штампом)
            double[] oxs = { o.X + 30, o.X + 220, o.X + 30, o.X + 220 };
            double[] oys = { o.Y + 175, o.Y + 175, o.Y + 105, o.Y + 105 };

            var tableRows = new List<string[]>();
            int segNo = 1;

            for (int zi = 0; zi < 4; zi++)
            {
                double bx = oxs[zi], by = oys[zi];
                Draw.Text(ms, tr, bx + 5, by + 58, 2.0, zoneNames[zi], DL.Text);

                Draw.Rect(ms, tr, bx + 5, by, bx + 22, by + 28, DL.Eq, 1);
                Draw.Text(ms, tr, bx + 7, by + 12, 1.4, "МГП", DL.Mark);

                double topY = by + 28 + lengths[zi][0] * 0.025;
                if (topY > by + 55) topY = by + 55;
                Draw.Line(ms, tr, bx + 13, by + 28, bx + 13, topY, DL.Pipe, 1);
                Draw.Text(ms, tr, bx + 15, by + 38, 1.3, "Ду" + dys[0], DL.Mark);

                double hx1 = bx + 13 + lengths[zi][1] * 0.03;
                if (hx1 > bx + 150) hx1 = bx + 150;
                Draw.Line(ms, tr, bx + 13, topY, hx1, topY, DL.Pipe, 1);
                Draw.Text(ms, tr, bx + 25, topY + 2, 1.3, "Ду" + dys[1] + " L=" + lengths[zi][1].ToString("0"), DL.Mark);

                double hx2 = hx1 + lengths[zi][2] * 0.02;
                if (hx2 > bx + 165) hx2 = bx + 165;
                Draw.Line(ms, tr, hx1, topY, hx2, topY - 6, DL.Pipe, 1);
                Draw.Line(ms, tr, hx2, topY - 6, hx2, topY - 14, DL.Pipe, 1);
                Draw.UgoNozzle(ms, tr, hx2, topY - 16, "");
                Draw.Text(ms, tr, hx2 + 3, topY - 14, 1.2, "Ду" + dys[3], DL.Mark);

                string[] segNames = { "Стояк", "Гор.1", "Гор.2", "Опуск" };
                for (int s = 0; s < 4; s++)
                {
                    tableRows.Add(new[] {
                        segNo.ToString(),
                        zoneNames[zi],
                        segNames[s],
                        dys[s].ToString(),
                        lengths[zi][s].ToString("0")
                    });
                    segNo++;
                }
            }

            // Таблица участков трубопровода (эталон МГИМО / детсад)
            double tx = o.X + DL.BindLeft + 6;
            double ty = o.Y + DL.ContentBottom + 55;
            Draw.Text(ms, tr, tx, ty + 4, 2.0, "Таблица участков трубопроводной разводки", DL.Text);
            Draw.Table(ms, tr, tx, ty - 1, new[] { 14.0, 55.0, 28.0, 18.0, 28.0 }, 3.4,
                new[] { "№", "Зона", "Участок", "Ду", "L, мм" }, tableRows, DL.Table);
        }

        // ----- Лист: Спецификация -----
        public static void Specification(BlockTableRecord ms, Transaction tr, ProjectData pd, int idx)
        {
            var o = Origin(idx);
            var cipherSpec = (pd.Cipher ?? "39-25/1-АГПТ") + ".СО";
            Draw.SheetFrame(ms, tr, o.X, o.Y, DL.A3W, DL.A3H,
                cipherSpec, "Спецификация оборудования, изделий и материалов", "10", "12",
                pd.ObjectName, pd.Stage, pd.Company, pd.CompanyGsl, pd.CityYear,
                pd.Gip, pd.Checked, pd.Drawn, pd.NormCtrl);

            double x = o.X + DL.BindLeft + 3;
            double y = o.Y + DL.A3H - 14;
            Draw.Text(ms, tr, x, y, 2.8, "СПЕЦИФИКАЦИЯ (ГОСТ 21.110) — АГПТ", DL.Text);
            y -= 5;

            // Позиции с кодами как в эталоне
            var rows = new List<string[]>
            {
                new[] { "", "Оборудование систем пожарной сигнализации и управления", "", "", "", "", "", "" },
                new[] { "1", "Извещатель пожарный дымовой оптико-электронный адресно-аналоговый", "ИП 212-64-R3 W1.02", "248-302-0228", "шт.", "9", "", "+10% запас" },
                new[] { "2", "Оповещатель охранно-пожарный комбинированный свето-звуковой адресный", "ОПОП 124-R3", "248-304-0171", "шт.", "4", "", "" },
                new[] { "3", "Устройство дистанционного пуска адресное «Запуск пожаротушения»", "УДП 513-11-A-R3", "248-303-0101", "шт.", "4", "", "" },
                new[] { "4", "Оповещатель световой адресный (табло «Газ! Уходи!»)", "ОПОП 1-R3", "248-304-0174", "шт.", "4", "", "" },
                new[] { "5", "Оповещатель световой адресный (табло «Газ! Не входи!»)", "ОПОП 1-R3", "248-304-0174", "шт.", "4", "", "" },
                new[] { "6", "Оповещатель световой адресный (табло «Автоматика выключена»)", "ОПОП 1-R3", "248-304-0174", "шт.", "4", "", "" },
                new[] { "7", "Адресный модуль пожаротушения", "МПТ-1-R3", "541-801-1604-00", "шт.", "4", "", "" },
                new[] { "", "Оборудование газового пожаротушения", "", "", "", "", "", "" },
                new[] { "8", "Модуль газового пожаротушения в комплекте (Flamex 66-40-33, HFC227ea 20 кг)", "Flamex 66-40-33", "274-801-0101-00", "шт.", "8", "", "4 раб.+4 рез." },
                new[] { "9", "Устройство электромагнитного пуска для модулей ГПТ", "QDQ 90N", "248-305-1302", "шт.", "4", "", "" },
                new[] { "10", "Рукав высокого давления", "RG-32GZ/650", "248-305-1305", "шт.", "4", "", "" },
                new[] { "11", "Сигнализатор давления универсальный", "СДУ-М", "248-305-1304", "шт.", "4", "", "" },
                new[] { "12", "Насадок выпускной ∅15 мм", "PT-15A-15-1/2\"", "248-305-1309", "шт.", "4", "", "" },
                new[] { "", "Трубопроводы и кабели", "", "", "", "", "", "" },
                new[] { "13", "Труба стальная бесшовная Ду20 (25×2,5)", "ГОСТ 8732-78", "241-108-0104", "м", "15", "", "" },
                new[] { "14", "Отвод стальной Ду20", "ГОСТ 8732-78", "", "шт.", "12", "", "" },
                new[] { "15", "Кабель огнестойкий для СПС и СОУЭ", "КПСнг(А)-FRLS 2×2×0,75", "274-302-0301-00", "м", "60", "", "" },
                new[] { "16", "Труба ПВХ гибкая d20", "", "241-207-0302", "м", "50", "", "" },
            };

            // Высота таблицы не должна уходить в зону штампа
            double yFloor = o.Y + DL.ContentBottom + 2;
            double rowH = 4.2;
            int maxData = Math.Max(4, (int)((y - yFloor) / rowH) - 1);
            if (rows.Count > maxData)
                rows = rows.GetRange(0, maxData);
            Draw.Table(ms, tr, x, y,
                new[] { 10.0, 95.0, 40.0, 32.0, 12.0, 12.0, 14.0, 28.0 }, rowH,
                new[] { "Поз.", "Наименование и техническая характеристика", "Тип, марка", "Код", "Ед.", "Кол.", "Масса", "Примечание" },
                rows, DL.Table);
        }

        // ----- Лист: Кабельный журнал -----
        public static void CableJournal(BlockTableRecord ms, Transaction tr, ProjectData pd, int idx)
        {
            var o = Origin(idx);
            Draw.SheetFrame(ms, tr, o.X, o.Y, DL.A3W, DL.A3H, pd,
                "Кабельный журнал АГПТ", "8", "9");

            double x = o.X + DL.BindLeft + 5;
            double y = o.Y + DL.A3H - 18;
            Draw.Text(ms, tr, x, y, 3.5, "КАБЕЛЬНЫЙ ЖУРНАЛ АГПТ (газ)", DL.Text);
            y -= 6;

            var rows = pd.Cables;
            if (rows.Count == 0)
            {
                rows = new List<CableRow>
                {
                    new CableRow { From="МПТ-1-R3", To="ИП-1", Mark="КПСнг-FRLS 1×2×0,5", Length=12 },
                    new CableRow { From="ИП-1", To="ИП-2", Mark="КПСнг-FRLS 1×2×0,5", Length=8 },
                    new CableRow { From="МПТ-1-R3", To="УДП", Mark="КПСнг-FRLS 1×2×0,5", Length=15 },
                    new CableRow { From="МПТ-1-R3", To="ОПОП Уходи", Mark="КПСнг-FRLS 1×2×0,5", Length=10 },
                    new CableRow { From="МПТ-1-R3", To="ОПОП Не входи", Mark="КПСнг-FRLS 1×2×0,5", Length=10 },
                    new CableRow { From="МПТ-1-R3", To="ОПОП Авт.выкл", Mark="КПСнг-FRLS 1×2×0,5", Length=10 },
                    new CableRow { From="МПТ-1-R3", To="ЗПУ МГП-1", Mark="КПСнг-FRLS 1×2×0,5", Length=6 },
                    new CableRow { From="МПТ-1-R3", To="СДУ-М", Mark="КПСнг-FRLS 1×2×0,5", Length=6 },
                    new CableRow { From="ЩР", To="МПТ-1-R3", Mark="ВВГнг-FRLS 3×1,5", Length=20 },
                };
            }
            var data = rows.Select(r => new[] { r.From, r.To, r.Mark, r.Length.ToString("F1") }).ToList();
            Draw.Table(ms, tr, x, y, new[] { 55.0, 55.0, 70.0, 25.0 }, 5.5,
                new[] { "Начало", "Конец", "Марка кабеля", "L, м" }, data, DL.Table);
        }

        // ----- Титульный лист (обложка) -----
        /// <summary>
        /// Титул по эталону «Детский сад АГПТ»: стр.1 (обложка) или стр.2 (с подписями).
        /// idxEven: 0 = обложка, 1 = лист подписей.
        /// </summary>
        public static void TitleSheet(BlockTableRecord ms, Transaction tr, ProjectData pd, int idx)
        {
            var o = Origin(idx);
            double left = o.X + DL.BindLeft;
            double bottom = o.Y + DL.Margin;
            double right = o.X + DL.A3W - DL.Margin;
            double top = o.Y + DL.A3H - DL.Margin;
            Draw.Rect(ms, tr, o.X, o.Y, o.X + DL.A3W, o.Y + DL.A3H, DL.Frame);
            Draw.Rect(ms, tr, left, bottom, right, top, DL.Frame);

            // --- Шапка (как в эталоне) ---
            Draw.Text(ms, tr, left + 8, top - 18, 4.0, "Республика Казахстан", DL.Text);
            Draw.Line(ms, tr, left + 8, top - 22, left + 160, top - 22, DL.Frame);

            Draw.Text(ms, tr, left + 8, top - 36, 2.3,
                "Генеральный проектировщик: " + (pd.GenDesigner ?? "ТОО «КазНИПИ энергетики и связи» ГСЛ №02913"), DL.Text);
            Draw.Text(ms, tr, left + 8, top - 46, 2.3,
                "Проектировщик: " + (pd.Company ?? "") + " " + (pd.CompanyGsl ?? ""), DL.Text);

            string orderNo = "39-25/1";
            if (!string.IsNullOrEmpty(pd.Cipher))
            {
                var parts = pd.Cipher.Split(new[] { '-' }, 3);
                if (parts.Length >= 2) orderNo = parts[0] + "-" + parts[1];
            }
            Draw.Text(ms, tr, left + 8, top - 56, 2.3, "Заказ: " + orderNo, DL.Text);

            // --- Центр: наименование объекта ---
            string obj = pd.ObjectName ?? pd.ObjectShort ?? "";
            Draw.MText(ms, tr, left + 40, o.Y + 210, 320, 3.2,
                "«" + obj + "»", DL.Text);

            // --- Блок «РАБОЧИЙ ПРОЕКТ» ---
            double midX = o.X + DL.A3W * 0.38;
            Draw.Text(ms, tr, midX, o.Y + 155, 5.5, "РАБОЧИЙ ПРОЕКТ", DL.Text);
            Draw.Text(ms, tr, midX + 10, o.Y + 138, 4.0, pd.Cipher ?? "", DL.Text);
            Draw.Text(ms, tr, midX + 25, o.Y + 122, 3.5, pd.VolumeNo ?? "Том 4", DL.Text);
            Draw.Text(ms, tr, midX - 30, o.Y + 105, 3.5,
                pd.VolumeTitle ?? "Автоматическое газовое пожаротушение", DL.Text);

            // Лист подписей (idx==1): два столбца как стр.2 эталона
            // На обложке (idx==0) — только город; на втором титуле — таблица подписей
            bool withSign = (idx % 10) == 1 || idx == 1;
            // FullAlbum передаёт 0 для первого титула; второй титул — отдельно
            if (withSign)
            {
                double y0 = o.Y + 85;
                // Левый столбец — генпроектировщик
                Draw.Text(ms, tr, left + 15, y0, 2.4, "Генеральный проектировщик", DL.Text);
                Draw.Text(ms, tr, left + 15, y0 - 12, 2.0, "ТОО «КазНИПИ энергетики и связи»", DL.Text);
                Draw.Text(ms, tr, left + 15, y0 - 28, 2.1, "Директор", DL.Text);
                Draw.Text(ms, tr, left + 70, y0 - 28, 2.1, "Сатыбалдин А.К.", DL.Text);
                Draw.Line(ms, tr, left + 55, y0 - 30, left + 68, y0 - 30, DL.Frame);
                Draw.Text(ms, tr, left + 15, y0 - 42, 2.1, "ГИП", DL.Text);
                Draw.Text(ms, tr, left + 70, y0 - 42, 2.1, "Абусаид А.", DL.Text);
                Draw.Line(ms, tr, left + 55, y0 - 44, left + 68, y0 - 44, DL.Frame);

                // Правый столбец — проектировщик
                double rx = o.X + 230;
                Draw.Text(ms, tr, rx, y0, 2.4, "Проектировщик", DL.Text);
                Draw.Text(ms, tr, rx, y0 - 12, 2.0, pd.Company ?? "ТОО «ТехноСтройПроект»", DL.Text);
                Draw.Text(ms, tr, rx, y0 - 28, 2.1, "Директор", DL.Text);
                Draw.Text(ms, tr, rx + 55, y0 - 28, 2.1, "Илиясов Д.Ж.", DL.Text);
                Draw.Line(ms, tr, rx + 40, y0 - 30, rx + 53, y0 - 30, DL.Frame);
                Draw.Text(ms, tr, rx, y0 - 42, 2.1, "ГИП", DL.Text);
                Draw.Text(ms, tr, rx + 55, y0 - 42, 2.1, pd.Gip ?? "Гайсин Д.Б.", DL.Text);
                Draw.Line(ms, tr, rx + 40, y0 - 44, rx + 53, y0 - 44, DL.Frame);
            }

            Draw.Text(ms, tr, o.X + 160, o.Y + 28, 3.0, pd.CityYear ?? "г. Астана, 2026 г.", DL.Text);
            Draw.Text(ms, tr, right - 55, bottom + 6, 1.8, "Формат А3", DL.Text);
        }

        /// <summary>Второй титульный лист (с подписями) — как стр.2 эталона.</summary>
        public static void TitleSheetSigned(BlockTableRecord ms, Transaction tr, ProjectData pd, int idx)
        {
            // Рисуем как TitleSheet с withSign: передаём idx=1 для ветки подписей
            TitleSheet(ms, tr, pd, 1);
            // Сдвигаем на нужный Origin(idx) — TitleSheet выше использует Origin(1) hardcoded.
            // Для произвольного idx перерисуем через локальный origin:
            // (упрощение: FullAlbum кладёт signed title на index 1)
        }

        // ----- Лист расчёта по зонам (как стр.3 эталона, формат A3 упрощённо) -----
        public static void CalcSheet(BlockTableRecord ms, Transaction tr, ProjectData pd, int idx)
        {
            var o = Origin(idx);
            Draw.SheetFrame(ms, tr, o.X, o.Y, DL.A3W, DL.A3H, pd,
                "Расчет газового пожаротушения", "8", "12");

            var zones = pd.Zones;
            if (zones.Count == 0)
            {
                zones = new List<ZoneCalc>
                {
                    new ZoneCalc { Name="Кроссовая 1", Vp=10.09, H=2.4, Fh=0.011, Tm=25, Cn=7.2, Rho0=7.28, K4=1.05, Vtr=0.00119, Gotv="Хладон 227еа" },
                    new ZoneCalc { Name="Электрощитовая 1", Vp=11.28, H=2.4, Fh=0.011, Tm=25, Cn=7.2, Rho0=7.28, K4=1.05, Vtr=0.00122, Gotv="Хладон 227еа" },
                    new ZoneCalc { Name="Кроссовая 2", Vp=13.11, H=2.4, Fh=0.011, Tm=25, Cn=7.2, Rho0=7.28, K4=1.05, Vtr=0.00129, Gotv="Хладон 227еа" },
                    new ZoneCalc { Name="Электрощитовая 2", Vp=16.43, H=2.4, Fh=0.011, Tm=25, Cn=7.2, Rho0=7.28, K4=1.05, Vtr=0.00097, Gotv="Хладон 227еа" },
                };
            }
            int n = Math.Min(4, zones.Count);

            // Рабочая область строго над штампом
            double xMin = o.X + DL.BindLeft + 4;
            double yMin = o.Y + DL.ContentBottom + 4;
            double yMax = o.Y + DL.ContentTop - 4;
            double workH = yMax - yMin;
            double workW = DL.A3W - DL.BindLeft - DL.Margin - 10;

            // 2×2 ячейки фиксированного размера — без пересечений
            double gap = 6;
            double cellW = (workW - gap) / 2.0;
            double cellH = (workH - gap) / 2.0;

            for (int i = 0; i < n; i++)
            {
                var z = zones[i];
                Calc.Mass(z);
                int col = i % 2;
                int row = i / 2;
                // row 0 = верх
                double x = xMin + col * (cellW + gap);
                double yTop = yMax - row * (cellH + gap);

                Draw.Text(ms, tr, x, yTop - 2, 2.2, (i + 1) + ". " + z.Name, DL.Text);
                double y = yTop - 6;

                // Компактные таблицы: 3.2 мм строка, укладываются в cellH
                var src = new List<string[]>
                {
                    new[] { "Vp, м³", z.Vp.ToString("F2") },
                    new[] { "H, м", z.H.ToString("F2") },
                    new[] { "FH", z.Fh.ToString("F3") },
                    new[] { "Tм", z.Tm.ToString("F0") },
                    new[] { "ГОТВ", z.Gotv ?? "" },
                    new[] { "Cn %", z.Cn.ToString("F1") },
                };
                double h1 = Draw.Table(ms, tr, x, y, new[] { 28.0, 48.0 }, 3.2,
                    new[] { "Парам.", "Знач." }, src, DL.Table);
                y -= h1 + 2.5;
                // не опускаться ниже низа ячейки
                double cellBottom = yTop - cellH + 2;
                if (y < cellBottom + 20) y = cellBottom + 22;

                var res = new List<string[]>
                {
                    new[] { "Mp кг", z.Mp.ToString("F2") },
                    new[] { "Mg кг", z.Mg.ToString("F0") },
                    new[] { "Модуль", z.ModuleVol + "л " + z.ModuleCountWork + "+" + z.ModuleCountRes },
                };
                Draw.Table(ms, tr, x, y, new[] { 28.0, 48.0 }, 3.2,
                    new[] { "Парам.", "Знач." }, res, DL.Table);
            }
            Draw.Text(ms, tr, xMin, yMin + 1, 1.5,
                "СП 485. Mg↑. Резерв 100%. Алгоритм C. Таблицы не пересекают штамп.", DL.Text);
        }

        // ----- Полный комплект (порядок как в альбоме Детский сад АГПТ) -----
        public static void FullAlbum(BlockTableRecord ms, Transaction tr, ProjectData pd)
        {
            // Сброс кэша рамки + одна загрузка определения блока на весь альбом
            FrameBlock.ResetCache();
            try { FrameBlock.EnsureDefinition(ms.Database, tr); }
            catch (System.Exception ex) { Diag.Msg("Рамка: fallback (" + ex.Message + ")"); }
            SyncPitchFromFrame();
            Diag.Msg("Pitch=" + Pitch.ToString("F0") + " мм, FrameW=" + FrameBlock.FrameWidth.ToString("F0")
                + ", scale=" + FrameBlock.InsertScale.ToString("0.###"));
            try { UgoLib.EnsureImported(ms.Database, tr); }
            catch (System.Exception ex) { Diag.Msg("УГО import: " + ex.Message); }

            // Эталон: МГИМО Астана 25/р-34-ПР-АГПТ (Блок 4 компакт + детсад)
            // АППТ — отдельно: APPT_ALBUM
            TitleSheet(ms, tr, pd, 0);           // 0 титул
            TitleSheetSignedAt(ms, tr, pd, 1);   // 1 титул с подписями
            GeneralData(ms, tr, pd, 2);          // 2 Общие данные
            ModuleDescription(ms, tr, pd, 3);    // 3 Описание модулей
            StructuralScheme(ms, tr, pd, 4);     // 4 Структурная схема
            PlanSheet(ms, tr, pd, 5);            // 5 План: оборудование + кабель
            PlanModulesPipe(ms, tr, pd, 6);      // 6 План: модули + трубопровод
            Axonometry(ms, tr, pd, 7);           // 7 Аксонометрия Ду/L
            CalcSheet(ms, tr, pd, 8);            // 8 Расчёт ГОТВ
            MountingScheme(ms, tr, pd, 9);       // 9 Схема монтажа
            Specification(ms, tr, pd, 10);       // 10 Спецификация .СО
            CableJournal(ms, tr, pd, 11);        // 11 Кабельный журнал
            Diag.Msg("Альбом: 12 листов, шагет 0…" + (11 * Pitch).ToString("F0") + " по X");
        }

        /// <summary>Краткое техническое описание модулей (эталон МГИМО л.6).</summary>
        public static void ModuleDescription(BlockTableRecord ms, Transaction tr, ProjectData pd, int idx)
        {
            var o = Origin(idx);
            Draw.SheetFrame(ms, tr, o.X, o.Y, DL.A3W, DL.A3H, pd,
                "Краткое техническое описание модулей МПТГ", "3", "12");
            double x = o.X + DL.BindLeft + 6;
            double y = o.Y + DL.ContentTop - 8;
            Draw.Text(ms, tr, x, y, 2.8, "Модуль газового пожаротушения (МПТГ)", DL.Text);
            y -= 8;
            string txt =
                "В проекте применяются модули газового пожаротушения (МПТГ) типа Flamex / Firex / Proffex " +
                "с огнетушащим веществом Хладон 227еа (HFC-227ea, C3F7H).\n\n" +
                "Основные характеристики (типовые):\n" +
                "• Рабочее давление — по паспорту модуля; выпуск ГОТВ ≤ 10 с.\n" +
                "• Комплект: баллон, ЗПУ (электромагнитный пуск), рукав РВД, СДУ, насадок.\n" +
                "• Резерв модулей — 100 % от расчётного количества (СП 485).\n" +
                "• Пуск — алгоритм C (два автоматических ИП в зоне).\n" +
                "• Задержка на эвакуацию ≥ 10 с до подачи ГОТВ.\n\n" +
                "Способ тушения — по объёму защищаемого помещения.\n" +
                "Трубопровод — сталь бесшовная Ду15…Ду32 (подбор по гидравлике).\n" +
                "Насадки — по паспорту производителя, ориентация по схеме монтажа.\n\n" +
                "Электропитание АГПТ — I категория (ПУЭ), кабель ВВГнг(А)-FRLS 3×2,5 от щита после АВР.\n" +
                "Линии АЛС/шлейфов — КПСнг(А)-FRLS 1×2×0,5; пусковые — 2×2×0,5.";
            Draw.MText(ms, tr, x, y, 380, 2.1, txt, DL.Text);

            // Таблица типоразмеров
            y = o.Y + 160;
            Draw.Text(ms, tr, x, y, 2.2, "Ориентировочные типоразмеры модулей", DL.Text);
            y -= 5;
            var rows = new List<string[]>
            {
                new[] { "Flamex 66-40-33", "20", "Хладон 227еа", "1+1" },
                new[] { "Firex 65-40-32", "32", "Хладон 227еа", "по расчёту" },
                new[] { "Proffex 65-**-**", "по паспорту", "Хладон 227еа", "по расчёту" },
            };
            Draw.Table(ms, tr, x, y, new[] { 55.0, 30.0, 50.0, 40.0 }, 5.0,
                new[] { "Марка", "V, л", "ГОТВ", "Кол. (раб+рез)" }, rows, DL.Table);
        }

        /// <summary>План: модули + трубопровод + интерфейс (эталон МГИМО л.9).</summary>
        public static void PlanModulesPipe(BlockTableRecord ms, Transaction tr, ProjectData pd, int idx)
        {
            var o = Origin(idx);
            Draw.SheetFrame(ms, tr, o.X, o.Y, DL.A3W, DL.A3H, pd,
                "Фрагмент плана. Модули, трубопровод АГПТ и интерфейс", "6", "12");

            double xMin = o.X + DL.BindLeft + 8;
            double yMin = o.Y + DL.ContentBottom + 6;
            double yMax = o.Y + DL.ContentTop - 8;

            Draw.Text(ms, tr, xMin, yMax - 2, 2.2,
                "Фрагмент плана подвального этажа на отметке -2.500 в осях 1/В-Г", DL.Text);
            Draw.Text(ms, tr, xMin, yMax - 8, 2.0,
                "Помещение Электрощитовой. Модули, трубопровод и интерфейс", DL.Text);

            double pw = 240, ph = 130;
            double bx = xMin + 28, by = yMin + 28;

            // Оси
            Draw.Circle(ms, tr, bx, by + ph + 10, 3.5, DL.Mark, 7);
            Draw.Text(ms, tr, bx - 1.5, by + ph + 8.8, 2.4, "В", DL.Mark);
            Draw.Circle(ms, tr, bx + pw, by + ph + 10, 3.5, DL.Mark, 7);
            Draw.Text(ms, tr, bx + pw - 1.5, by + ph + 8.8, 2.4, "Г", DL.Mark);
            Draw.Circle(ms, tr, bx - 12, by + ph, 3.5, DL.Mark, 7);
            Draw.Text(ms, tr, bx - 13.2, by + ph - 1.2, 2.4, "1", DL.Mark);
            Draw.Circle(ms, tr, bx - 12, by, 3.5, DL.Mark, 7);
            Draw.Text(ms, tr, bx - 13.2, by - 1.2, 2.4, "2", DL.Mark);
            for (double yy = by + ph; yy > by; yy -= 5)
                Draw.Line(ms, tr, bx, yy, bx, Math.Max(by, yy - 2.5), DL.Mark, 8);
            for (double yy = by + ph; yy > by; yy -= 5)
                Draw.Line(ms, tr, bx + pw, yy, bx + pw, Math.Max(by, yy - 2.5), DL.Mark, 8);

            Draw.Rect(ms, tr, bx, by, bx + pw, by + ph, DL.Zone, 7);
            Draw.Rect(ms, tr, bx + 2, by + 2, bx + pw - 2, by + ph - 2, DL.Zone, 7);

            Draw.ElevationMark(ms, tr, bx + 8, by + ph - 14, "-2.500", DL.Mark);
            Draw.DimLine(ms, tr, bx, by - 10, bx + pw, by - 10, "4200", DL.Mark);
            Draw.DimLine(ms, tr, bx - 10, by, bx - 10, by + ph, "2400", DL.Mark);

            Draw.UgoModule(ms, tr, bx + 14, by + 16, "МГП-1");
            Draw.UgoModule(ms, tr, bx + 32, by + 16, "МГП-2 рез.");
            double nx = bx + pw * 0.55, ny = by + ph * 0.55;
            Draw.Line(ms, tr, bx + 22, by + 32, bx + 22, ny, DL.Pipe, 1);
            Draw.Line(ms, tr, bx + 22, ny, nx, ny, DL.Pipe, 1);
            Draw.UgoNozzle(ms, tr, nx, ny, "N-1");
            Draw.Text(ms, tr, bx + 24, (by + 32 + ny) / 2, 1.5, "Ду25 L=1.2", DL.Mark);
            Draw.Text(ms, tr, (bx + 22 + nx) / 2, ny + 2, 1.5, "Ду20 L=2.5", DL.Mark);
            Draw.Text(ms, tr, bx + 14, by + 58, 1.6, "СДУ", DL.Mark);
            Draw.Text(ms, tr, bx + 14, by + 52, 1.6, "РВД", DL.Mark);
            Draw.Text(ms, tr, bx + 55, by + 22, 1.5, "интерфейс RS / пуск", DL.Scheme);

            Draw.Text(ms, tr, xMin, yMin + 2, 1.5,
                "М 1:50. Оси 1/В-Г, отм. -2.500. Трубы стальные. Резерв модуля 100%.", DL.Text);
        }

        public static void MountingScheme(BlockTableRecord ms, Transaction tr, ProjectData pd, int idx)
        {
            var o = Origin(idx);
            Draw.SheetFrame(ms, tr, o.X, o.Y, DL.A3W, DL.A3H, pd,
                "Схема монтажа оборудования АГПТ на поверхности", "9", "12");
            double x = o.X + DL.BindLeft + 10;
            double y = o.Y + DL.ContentTop - 10;
            Draw.Text(ms, tr, x, y, 2.6, "Узлы монтажа", DL.Text);
            y -= 12;

            // Узел 1: насадок на потолке
            Draw.Text(ms, tr, x, y, 2.0, "1. Насадок на перекрытии / за фальшпотолком", DL.Text);
            y -= 6;
            Draw.Line(ms, tr, x, y, x + 80, y, DL.Pipe, 1); // труба
            Draw.UgoNozzle(ms, tr, x + 40, y - 2, "");
            Draw.Text(ms, tr, x + 50, y + 2, 1.5, "∅15 насадок", DL.Mark);
            Draw.Text(ms, tr, x + 5, y + 2, 1.5, "Ду20…25", DL.Mark);
            y -= 25;

            // Узел 2: модуль у стены
            Draw.Text(ms, tr, x, y, 2.0, "2. Модуль МГП у стены (крепление хомутами)", DL.Text);
            y -= 8;
            Draw.UgoModule(ms, tr, x + 10, y - 20, "МГП");
            Draw.Line(ms, tr, x + 18, y - 5, x + 18, y + 15, DL.Pipe, 1);
            Draw.Text(ms, tr, x + 30, y, 1.5, "РВД → трубопровод", DL.Mark);
            Draw.Text(ms, tr, x + 30, y - 8, 1.5, "СДУ на выходе", DL.Mark);
            y -= 40;

            // Узел 3: кабель
            Draw.Text(ms, tr, x, y, 2.0, "3. Прокладка кабеля", DL.Text);
            y -= 6;
            string cab =
                "• АЛС / шлейфы: КПСнг(А)-FRLS 1×2×0,5 в гофре d16 или кабель-канале.\n" +
                "• Пусковые линии: КПСнг(А)-FRLS 2×2×0,5.\n" +
                "• Питание: ВВГнг(А)-FRLS 3×2,5 от щита (I категория).\n" +
                "• Опуски к УДП, табло, ИП — в кабель-канале; отверстия — противопожарная пена.";
            Draw.MText(ms, tr, x, y, 380, 2.0, cab, DL.Text);
        }


        static void TitleSheetSignedAt(BlockTableRecord ms, Transaction tr, ProjectData pd, int idx)
        {
            // Копия TitleSheet с подписями, но Origin(idx)
            var o = Origin(idx);
            double left = o.X + DL.BindLeft;
            double bottom = o.Y + DL.Margin;
            double right = o.X + DL.A3W - DL.Margin;
            double top = o.Y + DL.A3H - DL.Margin;
            Draw.Rect(ms, tr, o.X, o.Y, o.X + DL.A3W, o.Y + DL.A3H, DL.Frame);
            Draw.Rect(ms, tr, left, bottom, right, top, DL.Frame);

            Draw.Text(ms, tr, left + 8, top - 18, 4.0, "Республика Казахстан", DL.Text);
            Draw.Line(ms, tr, left + 8, top - 22, left + 160, top - 22, DL.Frame);
            Draw.Text(ms, tr, left + 8, top - 36, 2.3,
                "Генеральный проектировщик: " + (pd.GenDesigner ?? ""), DL.Text);
            Draw.Text(ms, tr, left + 8, top - 46, 2.3,
                "Проектировщик: " + (pd.Company ?? "") + " " + (pd.CompanyGsl ?? ""), DL.Text);
            string orderNo = "39-25/1";
            if (!string.IsNullOrEmpty(pd.Cipher))
            {
                var parts = pd.Cipher.Split(new[] { '-' }, 3);
                if (parts.Length >= 2) orderNo = parts[0] + "-" + parts[1];
            }
            Draw.Text(ms, tr, left + 8, top - 56, 2.3, "Заказ: " + orderNo, DL.Text);

            Draw.MText(ms, tr, left + 40, o.Y + 210, 320, 3.2,
                "«" + (pd.ObjectName ?? "") + "»", DL.Text);
            double midX = o.X + DL.A3W * 0.38;
            Draw.Text(ms, tr, midX, o.Y + 155, 5.5, "РАБОЧИЙ ПРОЕКТ", DL.Text);
            Draw.Text(ms, tr, midX + 10, o.Y + 138, 4.0, pd.Cipher ?? "", DL.Text);
            Draw.Text(ms, tr, midX + 25, o.Y + 122, 3.5, pd.VolumeNo ?? "Том 4", DL.Text);
            Draw.Text(ms, tr, midX - 30, o.Y + 105, 3.5,
                pd.VolumeTitle ?? "Автоматическое газовое пожаротушение", DL.Text);

            double y0 = o.Y + 85;
            Draw.Text(ms, tr, left + 15, y0, 2.4, "Генеральный проектировщик", DL.Text);
            Draw.Text(ms, tr, left + 15, y0 - 12, 2.0, "ТОО «КазНИПИ энергетики и связи»", DL.Text);
            Draw.Text(ms, tr, left + 15, y0 - 28, 2.1, "Директор", DL.Text);
            Draw.Text(ms, tr, left + 70, y0 - 28, 2.1, "Сатыбалдин А.К.", DL.Text);
            Draw.Line(ms, tr, left + 55, y0 - 30, left + 68, y0 - 30, DL.Frame);
            Draw.Text(ms, tr, left + 15, y0 - 42, 2.1, "ГИП", DL.Text);
            Draw.Text(ms, tr, left + 70, y0 - 42, 2.1, "Абусаид А.", DL.Text);
            Draw.Line(ms, tr, left + 55, y0 - 44, left + 68, y0 - 44, DL.Frame);

            double rx = o.X + 230;
            Draw.Text(ms, tr, rx, y0, 2.4, "Проектировщик", DL.Text);
            Draw.Text(ms, tr, rx, y0 - 12, 2.0, pd.Company ?? "", DL.Text);
            Draw.Text(ms, tr, rx, y0 - 28, 2.1, "Директор", DL.Text);
            Draw.Text(ms, tr, rx + 55, y0 - 28, 2.1, "Илиясов Д.Ж.", DL.Text);
            Draw.Line(ms, tr, rx + 40, y0 - 30, rx + 53, y0 - 30, DL.Frame);
            Draw.Text(ms, tr, rx, y0 - 42, 2.1, "ГИП", DL.Text);
            Draw.Text(ms, tr, rx + 55, y0 - 42, 2.1, pd.Gip ?? "", DL.Text);
            Draw.Line(ms, tr, rx + 40, y0 - 44, rx + 53, y0 - 44, DL.Frame);

            Draw.Text(ms, tr, o.X + 160, o.Y + 28, 3.0, pd.CityYear ?? "", DL.Text);
            Draw.Text(ms, tr, right - 55, bottom + 6, 1.8, "Формат А3", DL.Text);
        }

        /// <summary>План: фрагменты помещений по зонам (масштаб ~1:50).</summary>
        public static void PlanSheet(BlockTableRecord ms, Transaction tr, ProjectData pd, int idx)
        {
            var o = Origin(idx);
            Draw.SheetFrame(ms, tr, o.X, o.Y, DL.A3W, DL.A3H, pd,
                "Фрагмент плана. Оборудование АГПТ и кабельные трассы", "5", "12");

            double xMin = o.X + DL.BindLeft + 4;
            double xMax = o.X + DL.A3W - DL.Margin - 4;
            double yMin = o.Y + DL.ContentBottom;
            double yMax = o.Y + DL.A3H - 10;

            // Данные помещений (эталон детсад / МГИМО)
            var rooms = new[]
            {
                new { Name="Электрощитовая 1", Floor="подвал", Elev="-2.500", Axes="1/В-Г", Lm=3.55, Wm=2.40, Vp=11.28, AxL="В", AxR="Г", AxT="1", AxB="2" },
                new { Name="Кроссовая 1", Floor="2 эт.", Elev="+5.100", Axes="2/Л", Lm=3.15, Wm=2.40, Vp=10.09, AxL="Л", AxR="М", AxT="2", AxB="3" },
                new { Name="Электрощитовая 2", Floor="подвал", Elev="-2.500", Axes="8/Ж-И", Lm=4.20, Wm=2.40, Vp=16.43, AxL="Ж", AxR="И", AxT="8", AxB="9" },
                new { Name="Кроссовая 2", Floor="3 эт.", Elev="+9.300", Axes="2/Л", Lm=3.80, Wm=2.40, Vp=13.11, AxL="Л", AxR="М", AxT="2", AxB="3" },
            };

            double cellW = (xMax - xMin) / 2.0 - 6;
            double cellH = (yMax - yMin - 18) / 2.0 - 6;
            const double sc = 18.0; // м → мм на листе

            for (int i = 0; i < 4; i++)
            {
                var rm = rooms[i];
                int col = i % 2, row = i / 2;
                double cellX = xMin + col * (cellW + 10);
                double cellY = yMax - 4 - (row + 1) * (cellH + 8);

                // Заголовок фрагмента (как в МГИМО)
                Draw.Text(ms, tr, cellX, cellY + cellH - 2, 1.6,
                    "Фрагмент плана (" + rm.Floor + ") отм. " + rm.Elev + " оси " + rm.Axes, DL.Text);
                Draw.Text(ms, tr, cellX, cellY + cellH - 6, 1.5, rm.Name, DL.Text);

                // Помещение
                double pw = Math.Min(cellW - 28, rm.Lm * sc);
                double ph = Math.Min(cellH - 32, rm.Wm * sc);
                double bx = cellX + 18;
                double by = cellY + 16;

                // ----- Оси -----
                // верхние буквенные
                double axTopY = by + ph + 8;
                Draw.Circle(ms, tr, bx, axTopY, 3.0, DL.Mark, 7);
                Draw.Text(ms, tr, bx - 1.2, axTopY - 1.1, 2.0, rm.AxL, DL.Mark);
                Draw.Circle(ms, tr, bx + pw, axTopY, 3.0, DL.Mark, 7);
                Draw.Text(ms, tr, bx + pw - 1.2, axTopY - 1.1, 2.0, rm.AxR, DL.Mark);
                // штриховые вертикали осей
                for (double yy = by + ph; yy > by; yy -= 4)
                    Draw.Line(ms, tr, bx, yy, bx, Math.Max(by, yy - 2), DL.Mark, 8);
                for (double yy = by + ph; yy > by; yy -= 4)
                    Draw.Line(ms, tr, bx + pw, yy, bx + pw, Math.Max(by, yy - 2), DL.Mark, 8);

                // боковые цифровые
                double axLeftX = bx - 10;
                Draw.Circle(ms, tr, axLeftX, by + ph, 3.0, DL.Mark, 7);
                Draw.Text(ms, tr, axLeftX - 1.0, by + ph - 1.1, 2.0, rm.AxT, DL.Mark);
                Draw.Circle(ms, tr, axLeftX, by, 3.0, DL.Mark, 7);
                Draw.Text(ms, tr, axLeftX - 1.0, by - 1.1, 2.0, rm.AxB, DL.Mark);
                for (double xx = bx; xx < bx + pw; xx += 4)
                    Draw.Line(ms, tr, xx, by + ph, Math.Min(bx + pw, xx + 2), by + ph, DL.Mark, 8);
                for (double xx = bx; xx < bx + pw; xx += 4)
                    Draw.Line(ms, tr, xx, by, Math.Min(bx + pw, xx + 2), by, DL.Mark, 8);

                // Стены
                Draw.Rect(ms, tr, bx, by, bx + pw, by + ph, DL.Zone, 7);
                Draw.Rect(ms, tr, bx + 1.2, by + 1.2, bx + pw - 1.2, by + ph - 1.2, DL.Zone, 7);

                // Отметка уровня
                Draw.ElevationMark(ms, tr, bx + 4, by + ph - 10, rm.Elev, DL.Mark);

                // Дверь
                double doorW = Math.Min(8, pw * 0.14);
                double doorX = bx + pw * 0.42;
                Draw.Line(ms, tr, doorX, by, doorX, by - 3.5, DL.Mark, 7);
                Draw.Line(ms, tr, doorX + doorW, by, doorX + doorW, by - 3.5, DL.Mark, 7);
                Draw.Line(ms, tr, doorX, by, doorX + doorW * 0.8, by - 3, DL.Mark, 7);
                Draw.Text(ms, tr, doorX - 4, by + 0.5, 1.2, "В", DL.Mark);
                Draw.Text(ms, tr, doorX + doorW + 0.5, by + 0.5, 1.2, "П", DL.Mark);

                // Размеры
                Draw.DimLine(ms, tr, bx, by - 7, bx + pw, by - 7, (rm.Lm * 1000).ToString("0"), DL.Mark);
                Draw.DimLine(ms, tr, bx - 7, by, bx - 7, by + ph, (rm.Wm * 1000).ToString("0"), DL.Mark);

                // УГО
                double mx = bx + 5, my = by + 5;
                Draw.UgoModule(ms, tr, mx, my, "");
                Draw.Text(ms, tr, mx + 8, my + 2, 1.2, "1", DL.Mark);
                Draw.UgoMpt(ms, tr, mx + 12, my + 1, "");
                Draw.Text(ms, tr, mx + 12, my + 9, 1.1, "МПТ", DL.Mark);

                double nx = bx + pw * 0.55, ny = by + ph * 0.48;
                Draw.Line(ms, tr, mx + 4, my + 12, mx + 4, ny, DL.Pipe, 1);
                Draw.Line(ms, tr, mx + 4, ny, nx, ny, DL.Pipe, 1);
                Draw.UgoNozzle(ms, tr, nx, ny, "");
                Draw.Text(ms, tr, nx + 2, ny, 1.1, "3", DL.Mark);

                double ip1x = bx + pw * 0.28, ip1y = by + ph - 7;
                double ip2x = bx + pw * 0.70, ip2y = by + ph - 7;
                Draw.UgoIp(ms, tr, ip1x, ip1y, 2.4, "");
                Draw.UgoIp(ms, tr, ip2x, ip2y, 2.4, "");
                Draw.Text(ms, tr, ip1x + 2.8, ip1y, 1.1, "2", DL.Mark);
                Draw.Text(ms, tr, ip2x + 2.8, ip2y, 1.1, "2", DL.Mark);

                Draw.UgoUdp(ms, tr, doorX + doorW + 4, by + 6, "");

                double tabW = Math.Min(24, pw * 0.35);
                Draw.UgoTablo(ms, tr, bx + pw - tabW - 2, by + ph - 6, tabW, 3.8, "Газ! Уходи!");
                Draw.UgoTablo(ms, tr, bx + pw - tabW - 2, by + ph - 11, tabW, 3.8, "Не входи!");

                Draw.UgoCable(ms, tr, ip1x, ip1y, ip2x, ip2y);
                Draw.UgoCable(ms, tr, ip2x, ip2y, mx + 14, my + 4);

                Draw.Text(ms, tr, bx + 1, by + 1, 1.2,
                    string.Format("Vp={0:F1}м3", rm.Vp), DL.Text);
            }

            Draw.Text(ms, tr, xMin, yMin + 2, 1.5,
                "УГО: 1-МГП  2-ИП  3-насадок  МПТ  УДП  табло. Оси и отметки — по эталону МГИМО. М1:50.", DL.Text);
        }
    }

    public static class Collect
    {
        /// <summary>true, если зоны подставлены как демо (в модели не было контуров AGPT_ZONE).</summary>
        public static bool LastUsedDemoZones { get; private set; }

        public static ProjectData FromModel(Database db, Transaction tr)
        {
            var pd = new ProjectData();
            LastUsedDemoZones = false;
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

            foreach (ObjectId id in ms)
            {
                var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (ent == null) continue;

                // Только сущности с XData зоны (контур после AGPT_ZONE), не любой объект на слое
                string zoneName = XD.Get(ent, "ZONE_NAME");
                if (!string.IsNullOrEmpty(zoneName))
                {
                    double vp = XD.GetD(ent, "VP", 0);
                    if (vp <= 0) continue;
                    var z = new ZoneCalc
                    {
                        Name = zoneName,
                        Vp = vp,
                        H = XD.GetD(ent, "H", 3),
                        Fh = XD.GetD(ent, "FH", 0.011),
                        Tm = XD.GetD(ent, "TM", 25),
                        Cn = XD.GetD(ent, "CN", 7.2),
                        Rho0 = XD.GetD(ent, "RHO0", 7.28),
                        K4 = XD.GetD(ent, "K4", 1.05),
                        Vtr = XD.GetD(ent, "VTR", 0.005),
                        Gotv = XD.Get(ent, "GOTV") ?? "Хладон 227еа"
                    };
                    Calc.Mass(z);
                    pd.Zones.Add(z);
                }
                if (XD.Get(ent, "SEG_TYPE") != null ||
                    (ent.Layer == DL.Pipe && XD.Get(ent, "DY") != null))
                {
                    pd.Pipes.Add(new PipeSeg
                    {
                        Id = XD.Get(ent, "SEG_ID") ?? "E",
                        Type = XD.Get(ent, "SEG_TYPE") ?? "branch",
                        Dy = (int)XD.GetD(ent, "DY", 20),
                        L = XD.GetD(ent, "L"),
                        Dp = XD.GetD(ent, "DP"),
                        POut = XD.GetD(ent, "POUT"),
                        Npsh = XD.GetD(ent, "NPSH"),
                        Cav = XD.Get(ent, "CAVIT") == "1",
                        Ham = XD.Get(ent, "HAMMER") == "1"
                    });
                }
            }
            if (pd.Zones.Count == 0)
            {
                LastUsedDemoZones = true;
                // Демо только если пользователь ещё не задал контуры AGPT_ZONE
                foreach (var z in new[]
                {
                    new ZoneCalc { Name="Кроссовая 1", Vp=10.09, H=2.4, Fh=0.011, Tm=25, Cn=7.2, Rho0=7.28, K4=1.05, Vtr=0.00119, Gotv="Хладон 227еа" },
                    new ZoneCalc { Name="Электрощитовая 1", Vp=11.28, H=2.4, Fh=0.011, Tm=25, Cn=7.2, Rho0=7.28, K4=1.05, Vtr=0.00122, Gotv="Хладон 227еа" },
                    new ZoneCalc { Name="Кроссовая 2", Vp=13.11, H=2.4, Fh=0.011, Tm=25, Cn=7.2, Rho0=7.28, K4=1.05, Vtr=0.00129, Gotv="Хладон 227еа" },
                    new ZoneCalc { Name="Электрощитовая 2", Vp=16.43, H=2.4, Fh=0.011, Tm=25, Cn=7.2, Rho0=7.28, K4=1.05, Vtr=0.00097, Gotv="Хладон 227еа" },
                })
                {
                    Calc.Mass(z);
                    pd.Zones.Add(z);
                }
            }
            if (pd.Pipes.Count > 0)
            {
                pd.Hydro.DpTotal = pd.Pipes.Sum(p => p.Dp);
                pd.Hydro.HasCav = pd.Pipes.Any(p => p.Cav);
                pd.Hydro.HasHam = pd.Pipes.Any(p => p.Ham);
            }
            return pd;
        }
    }

    // =========================================================================
    // КОМАНДЫ
    // =========================================================================

    /// <summary>
    /// Библиотека УГО: вставка именованных блоков из текущего чертежа
    /// или из blocks\ (экспорт из Садик.dwg / R-CAD).
    /// Имена: AGPT_UGO_IP, AGPT_UGO_MODULE, AGPT_UGO_MPT, AGPT_UGO_UDP, AGPT_UGO_TABLO, AGPT_UGO_NOZZLE
    /// Также пробуем RC-UGO-* если есть в чертеже.
    /// </summary>
    public static class UgoLib
    {
        // Имена из Садик.dwg (RubezhCAD) — список AGPT_UGO_LIST
        public static readonly string[] IpNames = {
            "RC-UGO-IRA-04", "RC-UGO-IRA-04_1", "RC-TSP-RUBEZH-R3-IP-1",
            "RC-UGO-IP12-03", "RC-UGO-IP12-03_1", "AGPT_UGO_IP", "ИП", "IP"
        };
        public static readonly string[] ModuleNames = {
            "RC-UGO-UNIT-01", "RC-TSP-RUBEZH-R3-AM1-1", "AGPT_UGO_MODULE", "AGPT_UGO_MGP", "МГП", "MODULE"
        };
        public static readonly string[] MptNames = {
            "RC-TSP-RUBEZH-R3-2OP-1", "RC-UGO-PPK-01", "RC-UGO-PPK-01_1",
            "RC-TSP-RUBEZH-R3-MDU1-1", "RC-UGO-MDU-02", "RC-UGO-MDU-02_1",
            "AGPT_UGO_MPT", "МПТ", "MPT"
        };
        public static readonly string[] UdpNames = {
            "RC-UGO-UDPD-01_1", "RC-UGO-UDPP-01_1", "AGPT_UGO_UDP", "УДП", "UDP"
        };
        public static readonly string[] TabloNames = {
            "RC-TSP-RUBEZH-R3-OPOP-1", "RC-UGO-OS-10", "RC-UGO-OS-01_1",
            "RC-UGO-OR-01", "RC-UGO-OK-12", "RC-UGO-OK-10_1", "RC-UGO-OK-01_1",
            "AGPT_UGO_TABLO", "ОПОП", "TABLO"
        };
        public static readonly string[] NozzleNames = {
            "RC-UGO-KLAP-04", "RC-UGO-KLAP-02_1", "AGPT_UGO_NOZZLE", "НАСАДОК", "NOZZLE"
        };
        public static readonly string[] PowerNames = {
            "RC-TSP-RUBEZH-IVEPR-1", "RC-UGO-BRP-01"
        };
        public static readonly string[] RmNames = {
            "RC-TSP-RUBEZH-R3-RM1-1", "RC-TSP-RUBEZH-R3-RM4K-1",
            "RC-UGO-RM-01_1", "RC-UGO-RM-05", "RC-UGO-RM-07"
        };

        /// <summary>Ключевые блоки для массового импорта из Садик.dwg</summary>
        public static readonly string[] ImportAllNames = {
            "RC-UGO-IRA-04", "RC-TSP-RUBEZH-R3-IP-1",
            "RC-TSP-RUBEZH-R3-OPOP-1", "RC-UGO-UDPD-01_1", "RC-UGO-UDPP-01_1",
            "RC-TSP-RUBEZH-R3-2OP-1", "RC-UGO-PPK-01", "RC-UGO-MDU-02",
            "RC-UGO-UNIT-01", "RC-TSP-RUBEZH-IVEPR-1",
            "RC-UGO-RM-05", "RC-TSP-RUBEZH-R3-IO-1"
        };

        public static ObjectId FindBlock(Transaction tr, Database db, string[] candidates)
        {
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            foreach (string name in candidates)
            {
                if (string.IsNullOrEmpty(name)) continue;
                if (bt.Has(name))
                    return bt[name];
            }
            // partial match
            foreach (ObjectId id in bt)
            {
                var btr = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
                if (btr.IsAnonymous || btr.IsLayout) continue;
                string n = btr.Name ?? "";
                foreach (string c in candidates)
                {
                    if (string.IsNullOrEmpty(c)) continue;
                    if (n.IndexOf(c, StringComparison.OrdinalIgnoreCase) >= 0)
                        return id;
                }
            }
            return ObjectId.Null;
        }

        public static bool TryInsert(BlockTableRecord ms, Transaction tr, Database db,
            string[] candidates, double x, double y, double scale = 1.0)
        {
            ObjectId defId = FindBlock(tr, db, candidates);
            if (defId == ObjectId.Null) return false;

            // Автомасштаб: R-CAD блоки часто 200–800 ед. → на плане нужно ~5–8 мм
            double sc = scale;
            if (sc <= 0 || Math.Abs(sc - 1.0) < 1e-9)
            {
                try
                {
                    var btrDef = (BlockTableRecord)tr.GetObject(defId, OpenMode.ForRead);
                    Extents3d? ext = null;
                    foreach (ObjectId eid in btrDef)
                    {
                        var ent = tr.GetObject(eid, OpenMode.ForRead) as Entity;
                        if (ent == null) continue;
                        try
                        {
                            var ee = ent.GeometricExtents;
                            if (ext == null) ext = ee;
                            else { var x2 = ext.Value; x2.AddExtents(ee); ext = x2; }
                        }
                        catch { }
                    }
                    if (ext.HasValue)
                    {
                        double bw = Math.Max(0.01, ext.Value.MaxPoint.X - ext.Value.MinPoint.X);
                        double bh = Math.Max(0.01, ext.Value.MaxPoint.Y - ext.Value.MinPoint.Y);
                        double maxDim = Math.Max(bw, bh);
                        // целевой размер на листе ~6 мм
                        sc = 6.0 / maxDim;
                        if (sc > 2.0) sc = 1.0; // уже мелкий блок
                        if (sc < 0.001) sc = 0.01;
                    }
                    else sc = 0.05; // типичный масштаб R-CAD мм→лист
                }
                catch { sc = 0.05; }
            }

            var br = new BlockReference(new Point3d(x, y, 0), defId);
            br.ScaleFactors = new Scale3d(sc, sc, 1);
            try { br.Layer = DL.Scheme; } catch { }
            ms.AppendEntity(br);
            tr.AddNewlyCreatedDBObject(br, true);
            return true;
        }

        /// <summary>Импорт ключевых RC-UGO из Садик_UGO_source.dwg, если ещё нет в чертеже.</summary>
        public static void EnsureImported(Database db, Transaction tr)
        {
            string path = null;
            string asmDir = "";
            try { asmDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ""; } catch { }
            foreach (string c in new[] {
                @"c:\Work\AGPT_Plugin\blocks\Садик_UGO_source.dwg",
                @"C:\Work\AGPT_Plugin\blocks\Садик_UGO_source.dwg",
                @"c:\Work\AGPT_Plugin\blocks\Садик.dwg",
                string.IsNullOrEmpty(asmDir) ? null : Path.Combine(asmDir, "blocks", "Садик_UGO_source.dwg")
            })
            {
                if (!string.IsNullOrEmpty(c) && File.Exists(c)) { path = c; break; }
            }
            if (path == null)
            {
                Diag.Msg("УГО: Садик_UGO_source.dwg не найден — программные символы");
                return;
            }

            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            bool need = false;
            foreach (string nm in ImportAllNames)
            {
                if (!bt.Has(nm)) { need = true; break; }
            }
            if (!need)
            {
                Diag.Msg("УГО: ключевые блоки уже в чертеже");
                return;
            }

            int ok = 0, skip = 0, fail = 0, missing = 0;
            try
            {
                using (var src = new Database(false, true))
                {
                    src.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, true, "");
                    foreach (string blockName in ImportAllNames)
                    {
                        try
                        {
                            if (bt.Has(blockName)) { skip++; continue; }
                            ObjectId srcId = ObjectId.Null;
                            using (var trSrc = src.TransactionManager.StartTransaction())
                            {
                                var btSrc = (BlockTable)trSrc.GetObject(src.BlockTableId, OpenMode.ForRead);
                                if (!btSrc.Has(blockName)) { missing++; trSrc.Commit(); continue; }
                                srcId = btSrc[blockName];
                                trSrc.Commit();
                            }
                            var ids = new ObjectIdCollection();
                            ids.Add(srcId);
                            var map = new IdMapping();
                            src.WblockCloneObjects(ids, db.BlockTableId, map, DuplicateRecordCloning.Replace, false);
                            ok++;
                        }
                        catch
                        {
                            fail++;
                        }
                    }
                }
                Diag.Msg("УГО import: +" + ok + " пропуск=" + skip + " нет_в_источнике=" + missing + " ошибка=" + fail);
            }
            catch (System.Exception ex)
            {
                Diag.Msg("УГО: не удалось открыть источник (" + ex.Message + ")");
            }
        }

        /// <summary>Список имён блоков в чертеже, похожих на УГО / R-CAD.</summary>
        public static System.Collections.Generic.List<string> ListUgoLike(Transaction tr, Database db)
        {
            var list = new System.Collections.Generic.List<string>();
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            foreach (ObjectId id in bt)
            {
                var btr = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
                if (btr.IsAnonymous || btr.IsLayout) continue;
                string n = btr.Name ?? "";
                string u = n.ToUpperInvariant();
                if (u.Contains("UGO") || u.Contains("RC-") || u.Contains("ИП") || u.Contains("IP")
                    || u.Contains("МПТ") || u.Contains("УДП") || u.Contains("ОПОП")
                    || u.Contains("МГП") || u.Contains("DIP") || u.Contains("R3")
                    || u.Contains("TABLO") || u.Contains("NOZZ"))
                    list.Add(n);
            }
            list.Sort();
            return list;
        }
    }

    public class AgptDrawingCommands
    {
        /// <summary>
        /// AGPT_FRAME — вставить блок рамки A3+штамп (из blocks\AGPT_Frame_A3.dxf/.dwg).
        /// Запрос точки вставки. Повторная вставка — тот же блок, со сдвигом вручную.
        /// </summary>
        /// <summary>
        /// AGPT_UGO_LIST — список блоков УГО/R-CAD в текущем чертеже.
        /// Откройте Садик.dwg и выполните эту команду, чтобы увидеть имена для WBLOCK.
        /// </summary>
        [CommandMethod("AGPT_UGO_LIST")]
        public void UgoList()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var ed = doc.Editor;
            try
            {
                using (doc.LockDocument())
                using (var tr = doc.Database.TransactionManager.StartTransaction())
                {
                    var list = UgoLib.ListUgoLike(tr, doc.Database);
                    ed.WriteMessage("\n[AGPT_UGO_LIST] Блоков, похожих на УГО/R-CAD: " + list.Count);
                    foreach (var n in list)
                        ed.WriteMessage("\n  " + n);
                    if (list.Count == 0)
                    {
                        ed.WriteMessage("\n  Откройте Садик.dwg (источник УГО) и повторите команду.");
                        ed.WriteMessage("\n  Либо: INSERT → обзор блоков чертежа.");
                        ed.WriteMessage("\n  Экспорт: _WBLOCK → блок → c:\\Work\\AGPT_Plugin\\blocks\\AGPT_UGO_IP.dwg");
                    }
                    tr.Commit();
                }
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\n[AGPT_UGO_LIST] " + ex.Message);
            }
        }

        /// <summary>
        /// AGPT_UGO_IMPORT — подтянуть определение блока из внешнего DWG в текущий чертёж.
        /// Запрос: путь к DWG (по умолчанию blocks\\Садик_UGO_source.dwg) и имя блока.
        /// </summary>
        [CommandMethod("AGPT_UGO_IMPORT")]
        public void UgoImport()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var db = doc.Database;
            var ed = doc.Editor;
            try
            {
                string defPath = @"c:\Work\AGPT_Plugin\blocks\Садик_UGO_source.dwg";
                if (!System.IO.File.Exists(defPath))
                    defPath = @"c:\Work\AGPT_Plugin\blocks\Садик.dwg";
                var pso = new PromptStringOptions("\nПуть к DWG с УГО <" + defPath + ">: ");
                pso.AllowSpaces = true;
                var psr = ed.GetString(pso);
                string path = (psr.Status == PromptStatus.OK && !string.IsNullOrWhiteSpace(psr.StringResult))
                    ? psr.StringResult.Trim().Trim('"') : defPath;
                if (!System.IO.File.Exists(path))
                {
                    ed.WriteMessage("\n[AGPT_UGO_IMPORT] Файл не найден: " + path);
                    ed.WriteMessage("\n  Положите Садик.dwg в c:\\Work\\AGPT_Plugin\\blocks\\");
                    return;
                }

                var pso2 = new PromptStringOptions("\nИмя блока для импорта <RC-UGO-IRA-04>: ");
                pso2.AllowSpaces = true;
                var psr2 = ed.GetString(pso2);
                string blockName = (psr2.Status == PromptStatus.OK && !string.IsNullOrWhiteSpace(psr2.StringResult))
                    ? psr2.StringResult.Trim() : "RC-UGO-IRA-04";

                using (doc.LockDocument())
                using (var tr = db.TransactionManager.StartTransaction())
                using (var src = new Database(false, true))
                {
                    src.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, true, "");
                    ObjectId srcId = ObjectId.Null;
                    using (var trSrc = src.TransactionManager.StartTransaction())
                    {
                        var btSrc = (BlockTable)trSrc.GetObject(src.BlockTableId, OpenMode.ForRead);
                        if (!btSrc.Has(blockName))
                        {
                            ed.WriteMessage("\n[AGPT_UGO_IMPORT] Блок «" + blockName + "» не найден. Доступные (фрагмент):");
                            int n = 0;
                            foreach (ObjectId id in btSrc)
                            {
                                var btr = (BlockTableRecord)trSrc.GetObject(id, OpenMode.ForRead);
                                if (btr.IsAnonymous || btr.IsLayout) continue;
                                string nm = btr.Name ?? "";
                                if (nm.StartsWith("*")) continue;
                                ed.WriteMessage("\n  " + nm);
                                if (++n >= 40) { ed.WriteMessage("\n  ..."); break; }
                            }
                            trSrc.Commit();
                            tr.Abort();
                            return;
                        }
                        srcId = btSrc[blockName];
                        trSrc.Commit();
                    }

                    var ids = new ObjectIdCollection();
                    ids.Add(srcId);
                    var map = new IdMapping();
                    src.WblockCloneObjects(ids, db.BlockTableId, map, DuplicateRecordCloning.Replace, false);
                    tr.Commit();
                    ed.WriteMessage("\n[AGPT_UGO_IMPORT] Блок «" + blockName + "» импортирован. Вставка: INSERT или AGPT_SHEET_PLAN.");
                }
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\n[AGPT_UGO_IMPORT] " + ex.Message);
            }
        }

                /// <summary>
        /// AGPT_UGO_IMPORT_ALL — импорт набора ключевых УГО из Садик.dwg (R-CAD).
        /// </summary>
        [CommandMethod("AGPT_UGO_IMPORT_ALL")]
        public void UgoImportAll()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var db = doc.Database;
            var ed = doc.Editor;
            try
            {
                string path = @"c:\Work\AGPT_Plugin\blocks\Садик_UGO_source.dwg";
                if (!System.IO.File.Exists(path))
                {
                    // если Садик открыт — можно клонировать из текущего? нет, нужен файл
                    ed.WriteMessage("\n[AGPT_UGO_IMPORT_ALL] Нет файла: " + path);
                    ed.WriteMessage("\n  Скопируйте Садик.dwg в blocks\\Садик_UGO_source.dwg");
                    ed.WriteMessage("\n  Или откройте Садик.dwg — блоки уже в чертеже, AGPT_SHEET_PLAN подхватит их.");
                    return;
                }
                int ok = 0, skip = 0, fail = 0;
                using (doc.LockDocument())
                using (var tr = db.TransactionManager.StartTransaction())
                using (var src = new Database(false, true))
                {
                    src.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, true, "");
                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    foreach (string blockName in UgoLib.ImportAllNames)
                    {
                        try
                        {
                            ObjectId srcId = ObjectId.Null;
                            using (var trSrc = src.TransactionManager.StartTransaction())
                            {
                                var btSrc = (BlockTable)trSrc.GetObject(src.BlockTableId, OpenMode.ForRead);
                                if (!btSrc.Has(blockName)) { skip++; trSrc.Commit(); continue; }
                                srcId = btSrc[blockName];
                                trSrc.Commit();
                            }
                            if (bt.Has(blockName)) { skip++; continue; }
                            var ids = new ObjectIdCollection();
                            ids.Add(srcId);
                            var map = new IdMapping();
                            src.WblockCloneObjects(ids, db.BlockTableId, map, DuplicateRecordCloning.Replace, false);
                            ok++;
                            ed.WriteMessage("\n  + " + blockName);
                        }
                        catch
                        {
                            fail++;
                            ed.WriteMessage("\n  ! " + blockName);
                        }
                    }
                    tr.Commit();
                }
                ed.WriteMessage("\n[AGPT_UGO_IMPORT_ALL] импорт=" + ok + " пропуск=" + skip + " ошибка=" + fail);
                ed.WriteMessage("\n  Далее: AGPT_SHEET_PLAN или AGPT_ALBUM");
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\n[AGPT_UGO_IMPORT_ALL] " + ex.Message);
            }
        }

        [CommandMethod("AGPT_FRAME")]
        public void InsertFrame()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var db = doc.Database;
            var ed = doc.Editor;
            try
            {
                FrameBlock.ResetCache();
                string path = FrameBlock.FindPath();
                if (path != null)
                    ed.WriteMessage("\n[AGPT_FRAME] Файл: " + path);
                else
                    ed.WriteMessage("\n[AGPT_FRAME] DXF/DWG не найден — программный блок рамки.");

                var ppo = new PromptPointOptions("\nТочка вставки рамки (левый нижний угол) <0,0>: ");
                ppo.AllowNone = true;
                var ppr = ed.GetPoint(ppo);
                Point3d pt = Point3d.Origin;
                if (ppr.Status == PromptStatus.OK)
                    pt = ppr.Value;
                else if (ppr.Status != PromptStatus.None)
                    return;

                using (doc.LockDocument())
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    ObjectId defId = FrameBlock.EnsureDefinition(db, tr);
                    if (defId == ObjectId.Null)
                    {
                        ed.WriteMessage("\n[AGPT_FRAME] Не удалось загрузить определение блока.");
                        tr.Abort();
                        return;
                    }

                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                    var br = new BlockReference(pt, defId);
                    double sc = FrameBlock.InsertScale > 1e-9 ? FrameBlock.InsertScale : 1.0;
                    br.ScaleFactors = new Scale3d(sc, sc, 1);
                    try { br.Layer = DL.Frame; } catch { }
                    ms.AppendEntity(br);
                    tr.AddNewlyCreatedDBObject(br, true);

                    // Опционально — подпись шифра/листа
                    var pso = new PromptStringOptions("\nНаименование листа <Общие данные>: ");
                    pso.AllowSpaces = true;
                    var psr = ed.GetString(pso);
                    string title = (psr.Status == PromptStatus.OK && !string.IsNullOrWhiteSpace(psr.StringResult))
                        ? psr.StringResult.Trim() : "Общие данные";

                    var pso2 = new PromptStringOptions("\nНомер листа <1>: ");
                    pso2.AllowSpaces = false;
                    var psr2 = ed.GetString(pso2);
                    string sheetNo = (psr2.Status == PromptStatus.OK && !string.IsNullOrWhiteSpace(psr2.StringResult))
                        ? psr2.StringResult.Trim() : "1";

                    // Текст поверх штампа блока
                    double right = pt.X + DL.A3W - DL.Margin;
                    double bottom = pt.Y + DL.Margin;
                    Draw.Text(ms, tr, right - 183, bottom + 45, 2.4, title, DL.Text);
                    Draw.Text(ms, tr, right - 117, bottom + 4, 2.2, sheetNo, DL.Text);

                    tr.Commit();
                    ed.WriteMessage("\n[AGPT_FRAME] Блок " + FrameBlock.BlockName + " вставлен в " +
                        string.Format("({0:F0},{1:F0})", pt.X, pt.Y));
                    ed.WriteMessage("\n  Шаг следующего листа ≈ " + SheetGen.Pitch.ToString("F0") + " мм по X.  Масштаб блока=" + FrameBlock.InsertScale.ToString("0.####"));
                }
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\n[AGPT_FRAME] Ошибка: " + ex.Message);
            }
        }

        /// <summary>AGPT_FRAME_LOAD — только загрузить определение блока рамки в чертёж (без вставки).</summary>
        [CommandMethod("AGPT_FRAME_LOAD")]
        public void LoadFrameDef()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var db = doc.Database;
            var ed = doc.Editor;
            try
            {
                FrameBlock.ResetCache();
                string path = FrameBlock.FindPath();
                ed.WriteMessage("\n[AGPT_FRAME_LOAD] Поиск: " + (path ?? "(не найден)"));
                // path optional — EnsureDefinition builds programmatic frame
                using (doc.LockDocument())
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var id = FrameBlock.EnsureDefinition(db, tr);
                    tr.Commit();
                    if (id == ObjectId.Null)
                        ed.WriteMessage("\n[AGPT_FRAME_LOAD] Ошибка загрузки.");
                    else
                        ed.WriteMessage("\n[AGPT_FRAME_LOAD] Блок " + FrameBlock.BlockName +
                            " загружен. Вставка: INSERT или AGPT_FRAME. Ширина=" +
                            FrameBlock.FrameWidth.ToString("F0") + " мм.");
                }
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\n[AGPT_FRAME_LOAD] " + ex.Message);
            }
        }

        /// <summary>AGPT_SHEET_OD — лист «Общие данные + расчёт» в модели</summary>
        [CommandMethod("AGPT_SHEET_OD")]
        public void SheetOd()
        {
            RunSheet((ms, tr, pd) => SheetGen.GeneralData(ms, tr, pd, 0));
        }

        /// <summary>AGPT_SHEET_SS — структурная схема</summary>
        [CommandMethod("AGPT_SHEET_SS")]
        public void SheetSs()
        {
            RunSheet((ms, tr, pd) => SheetGen.StructuralScheme(ms, tr, pd, 0));
        }

        /// <summary>AGPT_SHEET_AXO — аксонометрия + таблица гидравлики</summary>
        [CommandMethod("AGPT_SHEET_AXO")]
        public void SheetAxo()
        {
            RunSheet((ms, tr, pd) => SheetGen.Axonometry(ms, tr, pd, 0));
        }

        /// <summary>AGPT_SHEET_SPEC — спецификация как чертёж</summary>
        [CommandMethod("AGPT_SHEET_SPEC")]
        public void SheetSpec()
        {
            RunSheet((ms, tr, pd) => SheetGen.Specification(ms, tr, pd, 0));
        }

        /// <summary>AGPT_SHEET_KZH — кабельный журнал как чертёж</summary>
        [CommandMethod("AGPT_SHEET_KZH")]
        public void SheetKzh()
        {
            RunSheet((ms, tr, pd) => SheetGen.CableJournal(ms, tr, pd, 0));
        }

        /// <summary>AGPT_SHEET_PLAN — план с УГО и расстановкой оборудования</summary>
        [CommandMethod("AGPT_SHEET_PLAN")]
        public void SheetPlan()
        {
            RunSheet((ms, tr, pd) => SheetGen.PlanSheet(ms, tr, pd, 0));
        }

        /// <summary>AGPT_ALBUM — полный комплект листов; опрос зон (L×W×H) при пустой модели.</summary>
        [CommandMethod("AGPT_ALBUM")]
        public void Album()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var db = doc.Database;
            var ed = doc.Editor;
            try
            {
                ProjectData pd;
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    XD.EnsureApp(db, tr);
                    Layers.EnsureAll(db, tr);
                    Draw.EnsureTextStyle(db, tr);
                    pd = Collect.FromModel(db, tr);
                    tr.Commit();
                }

                var ciph = ed.GetString("\nШифр проекта <" + pd.Cipher + ">: ");
                if (ciph.Status == PromptStatus.OK && !string.IsNullOrWhiteSpace(ciph.StringResult))
                    pd.Cipher = ciph.StringResult.Trim();
                var obj = ed.GetString("\nОбъект <" + pd.ObjectShort + ">: ");
                if (obj.Status == PromptStatus.OK && !string.IsNullOrWhiteSpace(obj.StringResult))
                {
                    pd.ObjectShort = obj.StringResult.Trim();
                    pd.ObjectName = obj.StringResult.Trim();
                }

                // Если в модели есть контуры AGPT_ZONE — используем их; иначе предложить ввод
                if (!Collect.LastUsedDemoZones && pd.Zones.Count > 0)
                {
                    ed.WriteMessage(string.Format(
                        "\n[AGPT_ALBUM] Зоны из модели ({0}):", pd.Zones.Count));
                    foreach (var z in pd.Zones)
                        ed.WriteMessage(string.Format("\n  · {0}: Vp={1:F2} м³ Mg≈{2:F1} кг", z.Name, z.Vp, z.Mg));
                }

                var askOpts = new PromptKeywordOptions(
                    Collect.LastUsedDemoZones
                        ? "\nЗон в модели нет. Ввести помещения L×W×H? [Да/Нет] <Нет>:"
                        : "\nДобавить/заменить зоны вводом L×W×H? [Да/Нет] <Нет>:");
                askOpts.Keywords.Add("Да");
                askOpts.Keywords.Add("Нет");
                askOpts.Keywords.Default = "Нет";
                askOpts.AllowNone = true;
                var ask = ed.GetKeywords(askOpts);
                if (ask.Status == PromptStatus.OK && ask.StringResult == "Да")
                {
                    pd.Zones.Clear();
                    ed.WriteMessage("\nВвод зон. Пустое имя — конец списка.");
                    for (int i = 1; i <= 12; i++)
                    {
                        var nr = ed.GetString(string.Format("\n[{0}] Имя помещения (Enter=готово): ", i));
                        if (nr.Status != PromptStatus.OK || string.IsNullOrWhiteSpace(nr.StringResult))
                            break;
                        string name = nr.StringResult.Trim();

                        double len = GetDouble(ed, "  Длина L, м", 3.15);
                        double wid = GetDouble(ed, "  Ширина W, м", 7.15);
                        double hgt = GetDouble(ed, "  Высота H, м", 2.4);
                        double fh = GetDouble(ed, "  Площадь негерм. FH, м²", 0.011);

                        var z = new ZoneCalc
                        {
                            Name = name,
                            Vp = len * wid * hgt,
                            H = hgt,
                            Fh = fh,
                            Tm = 25,
                            Cn = 7.2,
                            Rho0 = 7.28,
                            K4 = 1.05,
                            Vtr = Math.Max(0.0005, len * 0.0004),
                            Gotv = "Хладон 227еа"
                        };
                        Calc.Mass(z);
                        pd.Zones.Add(z);
                        ed.WriteMessage(string.Format(
                            "\n  → {0}: Vp={1:F2} м³  Mg≈{2:F1} кг", name, z.Vp, z.Mg));
                    }
                    if (pd.Zones.Count == 0)
                    {
                        ed.WriteMessage("\nЗоны не введены — будут демо-данные эталона.");
                        using (var tr = db.TransactionManager.StartTransaction())
                        {
                            pd = Collect.FromModel(db, tr);
                            tr.Commit();
                        }
                    }
                }

                using (var tr = db.TransactionManager.StartTransaction())
                {
                    XD.EnsureApp(db, tr);
                    Layers.EnsureAll(db, tr);
                    Draw.EnsureTextStyle(db, tr);
                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                    SheetGen.FullAlbum(ms, tr, pd);
                    tr.Commit();
                    ed.WriteMessage("\n[AGPT_ALBUM] Альбом: 12 листов по X, шаг " + SheetGen.Pitch.ToString("F0")
                        + " мм. ContentBottom=" + DL.ContentBottom + ". ZOOM → E");
                    ed.WriteMessage("\n  0 Титул  1 ОД  2 Схема+подключение  3 Расчёт  4 Аксо  5 План  6 Спец  7 КЖ");
                    ed.WriteMessage(string.Format("\nЗон в расчёте: {0}", pd.Zones.Count));
                }
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\n[AGPT_ALBUM] Ошибка: " + ex.Message);
            }
        }

        static double GetDouble(Editor ed, string prompt, double def)
        {
            var opt = new PromptDoubleOptions("\n" + prompt + " <" + def.ToString("G") + ">: ")
            {
                DefaultValue = def,
                UseDefaultValue = true,
                AllowNegative = false,
                AllowZero = false
            };
            var r = ed.GetDouble(opt);
            return r.Status == PromptStatus.OK ? r.Value : def;
        }

        /// <summary>AGPT_CALC_DRAW — пересчитать зоны и обновить лист общих данных</summary>
        [CommandMethod("AGPT_CALC_DRAW")]
        public void CalcDraw()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            var db = doc.Database; var ed = doc.Editor;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                XD.EnsureApp(db, tr);
                var pd = Collect.FromModel(db, tr);
                foreach (var z in pd.Zones)
                {
                    Calc.Mass(z);
                    ed.WriteMessage($"\n[{z.Name}] Vp={z.Vp:F1} Mg={z.Mg:F2} кг модуль {z.ModuleVol}л Fc={z.Fc:F4}");
                }
                tr.Commit();
            }
            // Перестроить лист OD в начале координат
            RunSheet((ms, tr, pd) => SheetGen.GeneralData(ms, tr, pd, 0));
        }

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
                    ed.WriteMessage("\n[AGPT] Лист сформирован в пространстве модели (чертёж).");
                }
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\n[AGPT] Ошибка: " + ex.Message);
            }
        }
    }
}
