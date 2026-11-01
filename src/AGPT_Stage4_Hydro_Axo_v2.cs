// =============================================================================
// AGPT — Stage 4/5 v2: Гидравлика + Аксонометрия (усиленная версия)
// • Граф сети (узлы + рёбра)
// • Подбор Dy по допустимому ΔP
// • Аксонометрия на лист Layout со штампом ГОСТ
// • Проверка кавитации / вскипания (NPSH_avail = P − P_sat)
// • Проверка гидравлического удара (Жуковский: ΔP = ρ·c·Δv)
// • Расчёт времени закрытия ЗПУ (t_zpu, t_cr = 2L/c)
// Эталон: АУГПТ.online, СП 485, CAADS
// =============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Colors;

[assembly: CommandClass(typeof(AGPT.Stage4v2.AgptHydroAxoV2))]

namespace AGPT.Stage4v2
{
    // =========================================================================
    // Константы
    // =========================================================================
    public static class K
    {
        public const string App = "AGPT";
        public const string LPipe = "AGPT-PIPE";
        public const string LEq = "AGPT-EQ";
        public const string LMark = "AGPT-MARK";
        public const string LAxo = "AGPT-AXO";
        public const string LZone = "AGPT-ZONE";
        public const string LStamp = "AGPT-STAMP";

        public static readonly int[] DySeries = { 15, 20, 25, 32, 40, 50, 65, 80 };

        public static readonly Dictionary<int, double> DinM = new Dictionary<int, double>
        {
            { 15, 0.0157 }, { 20, 0.0210 }, { 25, 0.0265 }, { 32, 0.0340 },
            { 40, 0.0400 }, { 50, 0.0530 }, { 65, 0.0680 }, { 80, 0.0825 }
        };

        public const double Lambda = 0.02;
        public const double RhoMix = 200;      // кг/м³
        public const double TNorm = 10.0;      // с
        public const double PModule = 25.0;    // бар
        public const double PMinNoz = 5.0;     // бар
        public const double DpMaxFrac = 0.6;   // допустимая доля (Pmod-Pmin) на потери

        /// <summary>Минимальный запас NPSH (бар): P_local − P_sat ≥ NpshMin</summary>
        public const double NpshMinBar = 1.5;

        /// <summary>
        /// Давление насыщения ГОТВ при типовой температуре выпуска, бар (абс→изб приблизительно).
        /// Используются оценочные значения для проверки вскипания/кавитации в трубопроводе.
        /// HFC-227ea ~3.9 бар абс @20°C → ~2.9 изб; FK-5-1-12 низкое; HFC-125 выше.
        /// </summary>
        public static readonly Dictionary<string, double> PSatBar = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            { "HFC-227ea", 2.9 },
            { "HFC-125",   11.0 },
            { "FK-5-1-12", 0.4 },
            { "FK5112",    0.4 },
            { "NOVEC",     0.4 },
            { "DEFAULT",   2.9 }
        };

        public static double GetPSat(string gotv)
        {
            if (string.IsNullOrEmpty(gotv)) return PSatBar["DEFAULT"];
            if (PSatBar.TryGetValue(gotv, out var p)) return p;
            if (gotv.IndexOf("227", StringComparison.OrdinalIgnoreCase) >= 0) return PSatBar["HFC-227ea"];
            if (gotv.IndexOf("125", StringComparison.OrdinalIgnoreCase) >= 0) return PSatBar["HFC-125"];
            if (gotv.IndexOf("5-1-12", StringComparison.OrdinalIgnoreCase) >= 0) return PSatBar["FK-5-1-12"];
            if (gotv.IndexOf("novec", StringComparison.OrdinalIgnoreCase) >= 0) return PSatBar["NOVEC"];
            return PSatBar["DEFAULT"];
        }

        /// <summary>
        /// Гидравлический удар (формула Жуковского): ΔP = ρ · c · Δv
        /// c — скорость волны в трубе, м/с; для стальных труб с жидким ГОТВ ~800–1200 м/с.
        /// P_удар = P_раб + ΔP; риск, если P_удар > P_доп трубы / арматуры.
        /// </summary>
        public const double WaveSpeed = 1000.0;     // м/с, скорость волны давления
        public const double RhoLiquid = 1400.0;     // кг/м³, плотность жидкой фазы ГОТВ (оценка)
        public const double PAllowPipe = 60.0;      // бар, допустимое давление трубы/ЗПУ (типовое)
        public const double HammerSafety = 1.1;     // коэффициент запаса к P_удар

        /// <summary>
        /// Время срабатывания ЗПУ.
        /// t_cr = 2·L_max/c — критическое время (фаза волны туда-обратно).
        /// Если t_zpu ≥ t_cr — удар неполный: ΔP_уд *= t_cr/t_zpu.
        /// Типовое время открытия/закрытия электромагнитного ЗПУ: 0,05…0,5 с.
        /// </summary>
        public const double TZpuDefault = 0.15;     // с, расчётное время закрытия/открытия ЗПУ
        public const double TZpuMin = 0.05;         // с, быстрый электромагнит
        public const double TZpuMax = 0.50;         // с, медленный / пневмопривод
    }

    // =========================================================================
    // XData / Layers
    // =========================================================================
    public static class XD
    {
        public static void EnsureApp(Database db, Transaction tr)
        {
            var rat = (RegAppTable)tr.GetObject(db.RegAppTableId, OpenMode.ForRead);
            if (rat.Has(K.App)) return;
            rat.UpgradeOpen();
            var r = new RegAppTableRecord { Name = K.App };
            rat.Add(r); tr.AddNewlyCreatedDBObject(r, true);
        }

        public static void Set(Entity e, params (string key, string val)[] pairs)
        {
            var list = new List<TypedValue> { new TypedValue((int)DxfCode.ExtendedDataRegAppName, K.App) };
            var old = e.GetXDataForApplication(K.App);
            var keep = new HashSet<string>(pairs.Select(p => p.key));
            if (old != null)
                foreach (TypedValue tv in old)
                    if (tv.TypeCode == (int)DxfCode.ExtendedDataAsciiString)
                    {
                        var s = tv.Value.ToString();
                        int eq = s.IndexOf('=');
                        if (eq > 0 && !keep.Contains(s.Substring(0, eq))) list.Add(tv);
                    }
            foreach (var (key, val) in pairs)
                list.Add(new TypedValue((int)DxfCode.ExtendedDataAsciiString, key + "=" + val));
            e.XData = new ResultBuffer(list.ToArray());
        }

        public static string Get(Entity e, string key)
        {
            var rb = e.GetXDataForApplication(K.App);
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

    public static class Ly
    {
        public static void Ensure(Database db, Transaction tr, string name, short color)
        {
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (lt.Has(name)) return;
            lt.UpgradeOpen();
            var ltr = new LayerTableRecord { Name = name, Color = Color.FromColorIndex(ColorMethod.ByAci, color) };
            lt.Add(ltr); tr.AddNewlyCreatedDBObject(ltr, true);
        }

        public static void EnsureAll(Database db, Transaction tr)
        {
            Ensure(db, tr, K.LPipe, 5);
            Ensure(db, tr, K.LEq, 1);
            Ensure(db, tr, K.LMark, 7);
            Ensure(db, tr, K.LAxo, 4);
            Ensure(db, tr, K.LStamp, 7);
            Ensure(db, tr, K.LZone, 8);
        }
    }

    // =========================================================================
    // 1. ГРАФ СЕТИ
    // =========================================================================
    public class Node
    {
        public string Id;
        public Point3d Pt;
        public string Kind; // source | junction | nozzle | module
        public List<string> EdgeIds = new List<string>();
    }

    public class Edge
    {
        public string Id;
        public ObjectId EntId;
        public string NodeA, NodeB;
        public string SegType; // main | branch | drop
        public int Dy;
        public double L;
        public double Q, V, Dp;
        public Point3d P1, P2;
        /// <summary>Давление в конце сегмента (изб., бар)</summary>
        public double POut;
        /// <summary>NPSH_avail = POut − P_sat, бар</summary>
        public double NpshAvail;
        /// <summary>true если NPSH_avail &lt; NpshMin или POut &lt; PMinNoz</summary>
        public bool CavitationRisk;
        /// <summary>ΔP гидравлического удара (Жуковский), бар</summary>
        public double DpHammer;
        /// <summary>P_удар = POut + ΔP_hammer, бар</summary>
        public double PHammer;
        /// <summary>true если P_удар · k_зап > P_доп</summary>
        public bool HammerRisk;
        /// <summary>Коэффициент полноты удара = min(1, t_cr/t_zpu)</summary>
        public double HammerFactor;
    }

    public class NetworkGraph
    {
        public Dictionary<string, Node> Nodes = new Dictionary<string, Node>();
        public Dictionary<string, Edge> Edges = new Dictionary<string, Edge>();
        public double Mg;
        public double TPod;
        public double DpTotal;
        public string Gotv = "HFC-227ea";
        public double PSat;
        public int CavitationCount;
        public bool HasCavitation;
        public int HammerCount;
        public bool HasHammer;
        public double DpHammerMax;
        public double PHammerMax;
        /// <summary>Критическое время закрытия t_cr = 2·L_path/c, с</summary>
        public double TCrit;
        /// <summary>Принятое время закрытия/открытия ЗПУ, с</summary>
        public double TZpu;
        /// <summary>Длина наидлиннейшего пути source→nozzle, м</summary>
        public double LPathMax;
        /// <summary>Режим удара: full | partial</summary>
        public string HammerMode;

        const double SnapTol = 50.0; // мм — допуск склейки узлов

        public string SnapNode(Point3d p, string preferKind = "junction")
        {
            foreach (var n in Nodes.Values)
                if (n.Pt.DistanceTo(p) <= SnapTol)
                    return n.Id;
            string id = "N" + Nodes.Count;
            Nodes[id] = new Node { Id = id, Pt = p, Kind = preferKind };
            return id;
        }

        public void AddEdge(ObjectId entId, Point3d p1, Point3d p2, string segType, int dy, double len)
        {
            string kindA = segType == "drop" ? "junction" : "junction";
            string kindB = segType == "drop" ? "nozzle" : "junction";
            // source: если рядом модуль — определится позже
            string a = SnapNode(p1, kindA);
            string b = SnapNode(p2, kindB);
            string eid = "E" + Edges.Count;
            var e = new Edge
            {
                Id = eid, EntId = entId, NodeA = a, NodeB = b,
                SegType = segType, Dy = dy, L = len > 0 ? len : p1.DistanceTo(p2) / 1000.0,
                P1 = p1, P2 = p2
            };
            Edges[eid] = e;
            Nodes[a].EdgeIds.Add(eid);
            Nodes[b].EdgeIds.Add(eid);
        }

        public void MarkSourcesAndNozzles(List<(Point3d pt, string kind)> anchors)
        {
            foreach (var (pt, kind) in anchors)
            {
                foreach (var n in Nodes.Values)
                    if (n.Pt.DistanceTo(pt) <= SnapTol * 2)
                        n.Kind = kind;
            }
        }

        /// <summary>Топологическая сортировка от source к nozzle (BFS).</summary>
        public List<string> BfsOrder()
        {
            var sources = Nodes.Values.Where(n => n.Kind == "source" || n.Kind == "module").Select(n => n.Id).ToList();
            if (sources.Count == 0)
                sources = Nodes.Values.Where(n => n.EdgeIds.Count == 1).Select(n => n.Id).Take(1).ToList();
            if (sources.Count == 0 && Nodes.Count > 0)
                sources.Add(Nodes.Keys.First());

            var order = new List<string>();
            var seen = new HashSet<string>();
            var q = new Queue<string>(sources);
            while (q.Count > 0)
            {
                var id = q.Dequeue();
                if (!seen.Add(id)) continue;
                order.Add(id);
                foreach (var eid in Nodes[id].EdgeIds)
                {
                    var e = Edges[eid];
                    var other = e.NodeA == id ? e.NodeB : e.NodeA;
                    if (!seen.Contains(other)) q.Enqueue(other);
                }
            }
            // рёбра в порядке обхода
            return order;
        }

        public List<Edge> EdgesInFlowOrder()
        {
            var nodeOrder = BfsOrder();
            var rank = new Dictionary<string, int>();
            for (int i = 0; i < nodeOrder.Count; i++) rank[nodeOrder[i]] = i;

            return Edges.Values
                .OrderBy(e => Math.Min(
                    rank.ContainsKey(e.NodeA) ? rank[e.NodeA] : 999,
                    rank.ContainsKey(e.NodeB) ? rank[e.NodeB] : 999))
                .ToList();
        }
    }

    // =========================================================================
    // 2. ГИДРАВЛИКА + ПОДБОР Dy
    // =========================================================================
    public static class Hydro
    {
        public static double Din(int dy) =>
            K.DinM.ContainsKey(dy) ? K.DinM[dy] : dy / 1000.0 * 0.9;

        public static double CalcDp(int dy, double lenM, double qKgS)
        {
            double d = Din(dy);
            double area = Math.PI * d * d / 4;
            double v = area > 1e-12 ? qKgS / (K.RhoMix * area) : 0;
            double dpPa = K.Lambda * (lenM / d) * (K.RhoMix * v * v / 2);
            return dpPa / 1e5; // бар
        }

        public static double CalcV(int dy, double qKgS)
        {
            double d = Din(dy);
            double area = Math.PI * d * d / 4;
            return area > 1e-12 ? qKgS / (K.RhoMix * area) : 0;
        }

        /// <summary>Минимальный Dy из ряда, при котором ΔP сегмента ≤ dpAllow.</summary>
        public static int SuggestDy(double lenM, double qKgS, double dpAllow)
        {
            foreach (int dy in K.DySeries)
            {
                if (CalcDp(dy, lenM, qKgS) <= dpAllow)
                    return dy;
            }
            return K.DySeries.Last();
        }

        public static void Run(NetworkGraph g, bool resizeDy)
        {
            if (g.Edges.Count == 0) return;

            g.PSat = K.GetPSat(g.Gotv);
            double qTotal = g.Mg > 0 ? g.Mg / K.TNorm : 2.0;
            int nNoz = Math.Max(1, g.Nodes.Values.Count(n => n.Kind == "nozzle"));
            double qNoz = qTotal / nNoz;

            var ordered = g.EdgesInFlowOrder();
            foreach (var e in ordered)
            {
                if (e.SegType == "drop")
                    e.Q = qNoz;
                else if (e.SegType == "branch")
                    e.Q = qNoz * CountDownstreamNozzles(g, e);
                else
                    e.Q = qTotal;
            }

            double pBudget = (K.PModule - K.PMinNoz) * K.DpMaxFrac;
            double lenSum = g.Edges.Values.Sum(e => e.L);

            // Давление в узлах (накопление потерь от source)
            var nodeP = new Dictionary<string, double>();
            foreach (var n in g.Nodes.Values)
                nodeP[n.Id] = K.PModule; // старт; уточнится при обходе

            // Источники — PModule
            foreach (var n in g.Nodes.Values)
                if (n.Kind == "source" || n.Kind == "module")
                    nodeP[n.Id] = K.PModule;

            foreach (var e in ordered)
            {
                double dpAllow = lenSum > 0 ? pBudget * (e.L / lenSum) : pBudget / Math.Max(1, g.Edges.Count);

                if (resizeDy)
                {
                    int newDy = SuggestDy(e.L, e.Q, Math.Max(0.05, dpAllow));
                    if (e.Dy < newDy || e.Dy == 0)
                        e.Dy = newDy;
                    else if (CalcDp(e.Dy, e.L, e.Q) > dpAllow * 1.2)
                        e.Dy = newDy;
                }

                e.Dp = CalcDp(e.Dy, e.L, e.Q);
                e.V = CalcV(e.Dy, e.Q);

                // P на входе ребра = min известных давлений узлов (от source течёт к nozzle)
                double pIn = K.PModule;
                if (nodeP.ContainsKey(e.NodeA) && nodeP.ContainsKey(e.NodeB))
                {
                    // Берём большее как «вверх по потоку» (ближе к модулю)
                    pIn = Math.Max(nodeP[e.NodeA], nodeP[e.NodeB]);
                    // Если один из узлов ещё не снижен — используем max
                }
                else if (nodeP.ContainsKey(e.NodeA))
                    pIn = nodeP[e.NodeA];
                else if (nodeP.ContainsKey(e.NodeB))
                    pIn = nodeP[e.NodeB];

                e.POut = pIn - e.Dp;
                if (e.POut < 0) e.POut = 0;

                // Обновить давление downstream-узла
                string down = nodeP.ContainsKey(e.NodeA) && nodeP.ContainsKey(e.NodeB)
                    ? (nodeP[e.NodeA] >= nodeP[e.NodeB] ? e.NodeB : e.NodeA)
                    : e.NodeB;
                // Более надёжно: узел с меньшим текущим P или не-source
                if (g.Nodes.ContainsKey(e.NodeA) && g.Nodes.ContainsKey(e.NodeB))
                {
                    var ka = g.Nodes[e.NodeA].Kind;
                    var kb = g.Nodes[e.NodeB].Kind;
                    if (ka == "source" || ka == "module") down = e.NodeB;
                    else if (kb == "source" || kb == "module") down = e.NodeA;
                    else down = nodeP[e.NodeA] <= nodeP[e.NodeB] ? e.NodeA : e.NodeB;
                }
                nodeP[down] = Math.Min(nodeP.ContainsKey(down) ? nodeP[down] : K.PModule, e.POut);

                // NPSH / кавитация
                e.NpshAvail = e.POut - g.PSat;
                e.CavitationRisk = (e.NpshAvail < K.NpshMinBar) || (e.POut < K.PMinNoz);

                // Гидравлический удар — полный Жуковский (позже умножим на factor от t_zpu)
                double dv = Math.Abs(e.V);
                double dpHamPa = K.RhoLiquid * K.WaveSpeed * dv;
                e.DpHammer = dpHamPa / 1e5; // полный, без учёта t_zpu
                e.HammerFactor = 1.0;
            }

            // --- Время закрытия ЗПУ и критическое время ---
            // L_path ≈ сумма L вдоль самого длинного пути (оценка: сумма всех L / грубо max path)
            g.LPathMax = EstimateMaxPathLength(g);
            g.TCrit = g.LPathMax > 0 ? (2.0 * g.LPathMax / K.WaveSpeed) : 0.01;
            if (g.TZpu <= 0) g.TZpu = K.TZpuDefault;
            // factor: если ЗПУ закрывается медленнее t_cr — удар неполный
            double hamFactor = g.TZpu <= 1e-6 ? 1.0 : Math.Min(1.0, g.TCrit / g.TZpu);
            g.HammerMode = hamFactor >= 0.999 ? "full" : "partial";

            foreach (var e in ordered)
            {
                e.HammerFactor = hamFactor;
                e.DpHammer *= hamFactor;
                e.PHammer = Math.Max(e.POut + e.DpHammer, K.PModule + e.DpHammer);
                e.HammerRisk = (e.PHammer * K.HammerSafety) > K.PAllowPipe;
            }

            g.DpTotal = g.Edges.Values.Sum(e => e.Dp);
            g.CavitationCount = g.Edges.Values.Count(e => e.CavitationRisk);
            g.HasCavitation = g.CavitationCount > 0;
            g.HammerCount = g.Edges.Values.Count(e => e.HammerRisk);
            g.HasHammer = g.HammerCount > 0;
            g.DpHammerMax = g.Edges.Count > 0 ? g.Edges.Values.Max(e => e.DpHammer) : 0;
            g.PHammerMax = g.Edges.Count > 0 ? g.Edges.Values.Max(e => e.PHammer) : 0;

            double pAvail = K.PModule - K.PMinNoz;
            if (g.DpTotal > pAvail && pAvail > 0)
                g.TPod = K.TNorm * (g.DpTotal / pAvail);
            else
                g.TPod = K.TNorm;

            // Если кавитация — при подборе Dy увеличить Dy на проблемных сегментах (вторая итерация)
            if (resizeDy && g.HasCavitation)
            {
                foreach (var e in ordered.Where(x => x.CavitationRisk))
                {
                    foreach (int dy in K.DySeries)
                    {
                        if (dy <= e.Dy) continue;
                        double dp = CalcDp(dy, e.L, e.Q);
                        double pOut = K.PModule - dp; // грубо
                        // пересчёт относительно текущего POut + старый Dp
                        pOut = e.POut + e.Dp - dp;
                        if (pOut - g.PSat >= K.NpshMinBar && pOut >= K.PMinNoz)
                        {
                            e.Dy = dy;
                            e.Dp = dp;
                            e.V = CalcV(dy, e.Q);
                            e.POut = pOut;
                            e.NpshAvail = e.POut - g.PSat;
                            e.CavitationRisk = false;
                            break;
                        }
                    }
                }
                g.DpTotal = g.Edges.Values.Sum(e => e.Dp);
                g.CavitationCount = g.Edges.Values.Count(e => e.CavitationRisk);
                g.HasCavitation = g.CavitationCount > 0;
                // Пересчёт удара после смены Dy (с учётом t_zpu)
                double hf = g.TZpu <= 1e-6 ? 1.0 : Math.Min(1.0, g.TCrit / g.TZpu);
                g.HammerMode = hf >= 0.999 ? "full" : "partial";
                foreach (var e in ordered)
                {
                    double dv = Math.Abs(e.V);
                    e.HammerFactor = hf;
                    e.DpHammer = (K.RhoLiquid * K.WaveSpeed * dv) / 1e5 * hf;
                    e.PHammer = Math.Max(e.POut + e.DpHammer, K.PModule + e.DpHammer);
                    e.HammerRisk = (e.PHammer * K.HammerSafety) > K.PAllowPipe;
                }
                g.HammerCount = g.Edges.Values.Count(e => e.HammerRisk);
                g.HasHammer = g.HammerCount > 0;
                g.DpHammerMax = g.Edges.Values.Max(e => e.DpHammer);
                g.PHammerMax = g.Edges.Values.Max(e => e.PHammer);
            }
        }

        /// <summary>Оценка максимальной длины пути source→nozzle (сумма L на пути BFS).</summary>
        static double EstimateMaxPathLength(NetworkGraph g)
        {
            var sources = g.Nodes.Values
                .Where(n => n.Kind == "source" || n.Kind == "module")
                .Select(n => n.Id).ToList();
            if (sources.Count == 0 && g.Nodes.Count > 0)
                sources.Add(g.Nodes.Keys.First());

            double maxL = 0;
            foreach (var src in sources)
            {
                // Dijkstra-like по длине
                var dist = new Dictionary<string, double>();
                foreach (var id in g.Nodes.Keys) dist[id] = double.MaxValue;
                dist[src] = 0;
                var pq = new SortedSet<(double d, string id)>(Comparer<(double d, string id)>.Create(
                    (a, b) => a.d != b.d ? a.d.CompareTo(b.d) : string.Compare(a.id, b.id, StringComparison.Ordinal)));
                pq.Add((0, src));
                while (pq.Count > 0)
                {
                    var (d, u) = pq.Min;
                    pq.Remove(pq.Min);
                    if (d > dist[u]) continue;
                    foreach (var eid in g.Nodes[u].EdgeIds)
                    {
                        var e = g.Edges[eid];
                        var v = e.NodeA == u ? e.NodeB : e.NodeA;
                        double nd = d + e.L;
                        if (nd < dist[v])
                        {
                            dist[v] = nd;
                            pq.Add((nd, v));
                        }
                    }
                }
                foreach (var n in g.Nodes.Values)
                    if (n.Kind == "nozzle" && dist[n.Id] < double.MaxValue)
                        maxL = Math.Max(maxL, dist[n.Id]);
            }
            // fallback: сумма всех сегментов
            if (maxL <= 0)
                maxL = g.Edges.Values.Sum(e => e.L);
            return maxL;
        }

        /// <summary>
        /// Рекомендуемое время закрытия ЗПУ, чтобы удар был неполным:
        /// t_zpu_rec ≥ t_cr (с запасом 1,2).
        /// </summary>
        public static double RecommendTZpu(double tCrit)
        {
            double t = Math.Max(K.TZpuMin, tCrit * 1.2);
            return Math.Min(t, K.TZpuMax);
        }

        static int CountDownstreamNozzles(NetworkGraph g, Edge start)
        {
            // BFS от NodeB
            int count = 0;
            var seen = new HashSet<string>();
            var q = new Queue<string>();
            q.Enqueue(start.NodeB);
            while (q.Count > 0)
            {
                var id = q.Dequeue();
                if (!seen.Add(id)) continue;
                if (g.Nodes[id].Kind == "nozzle") count++;
                foreach (var eid in g.Nodes[id].EdgeIds)
                {
                    if (eid == start.Id) continue;
                    var e = g.Edges[eid];
                    var o = e.NodeA == id ? e.NodeB : e.NodeA;
                    if (!seen.Contains(o)) q.Enqueue(o);
                }
            }
            return Math.Max(1, count);
        }
    }

    // =========================================================================
    // 3. СБОР С ЧЕРТЕЖА → ГРАФ
    // =========================================================================
    public static class Build
    {
        public static NetworkGraph FromDrawing(Database db, Transaction tr)
        {
            var g = new NetworkGraph();
            var anchors = new List<(Point3d, string)>();

            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

            foreach (ObjectId id in ms)
            {
                var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (ent == null) continue;

                // Трубы
                if (ent.Layer == K.LPipe || XD.Get(ent, "SEG_TYPE") != null)
                {
                    if (ent is Polyline pl && pl.NumberOfVertices >= 2)
                    {
                        var p1 = pl.GetPoint3dAt(0);
                        var p2 = pl.GetPoint3dAt(pl.NumberOfVertices - 1);
                        string st = XD.Get(ent, "SEG_TYPE") ?? "branch";
                        int dy = (int)XD.GetD(ent, "DY", 20);
                        double len = XD.GetD(ent, "L");
                        if (len <= 0) len = p1.DistanceTo(p2) / 1000.0;
                        g.AddEdge(id, p1, p2, st, dy, len);
                    }
                    continue;
                }

                string kind = XD.Get(ent, "KIND") ?? "";
                Point3d pt = Point3d.Origin;
                if (ent is Polyline poly && poly.NumberOfVertices > 0)
                    pt = poly.GetPoint3dAt(0);
                else if (ent is Circle cir)
                    pt = cir.Center;
                else if (ent is DBText)
                    continue;

                if (kind == "NOZZLE")
                    anchors.Add((pt, "nozzle"));
                else if (kind == "MODULE")
                {
                    anchors.Add((pt, "module"));
                    if ((XD.Get(ent, "ROLE") ?? "Р") == "Р")
                        g.Mg += XD.GetD(ent, "M_FILL");
                }
            }

            // Mg из зоны, если модули не дали
            if (g.Mg <= 0)
            {
                foreach (ObjectId id in ms)
                {
                    var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent == null || ent.Layer != K.LZone) continue;
                    double mg = XD.GetD(ent, "MG");
                    if (mg > 0) { g.Mg = mg; break; }
                    double vp = XD.GetD(ent, "VP");
                    if (vp > 0) { g.Mg = vp * 0.5; break; } // грубая оценка
                }
            }
            if (g.Mg <= 0) g.Mg = 20;

            // GOTV из зоны
            foreach (ObjectId id in ms)
            {
                var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (ent == null || ent.Layer != K.LZone) continue;
                var gv = XD.Get(ent, "GOTV");
                if (!string.IsNullOrEmpty(gv)) { g.Gotv = gv; break; }
            }

            g.MarkSourcesAndNozzles(anchors);
            // Источник = module nodes
            foreach (var n in g.Nodes.Values)
                if (n.Kind == "module") n.Kind = "source";

            return g;
        }
    }

    // =========================================================================
    // 4. АКСОНОМЕТРИЯ
    // =========================================================================
    public static class Axo
    {
        static readonly double C30 = Math.Cos(Math.PI / 6);
        static readonly double S30 = Math.Sin(Math.PI / 6);

        public static Point3d Iso(Point3d p, double z) =>
            new Point3d((p.X - p.Y) * C30, (p.X + p.Y) * S30 + z, 0);

        public static void Draw(
            Database db, Transaction tr, BlockTableRecord target,
            NetworkGraph g,
            Point3d origin, double hPipe, double hNoz, double scale,
            Dictionary<string, Point3d> nozzlePts, Dictionary<string, Point3d> modulePts)
        {
            Ly.Ensure(db, tr, K.LAxo, 4);
            Ly.Ensure(db, tr, K.LMark, 7);

            Func<Point3d, double, Point3d> T = (p, z) =>
            {
                var i = Iso(p, z);
                return new Point3d(origin.X + i.X * scale, origin.Y + i.Y * scale, 0);
            };

            foreach (var e in g.Edges.Values)
            {
                double z1 = hPipe, z2 = e.SegType == "drop" ? hNoz : hPipe;
                var a = T(e.P1, z1);
                var b = T(e.P2, z2);

                var pl = new Polyline(2);
                pl.AddVertexAt(0, new Point2d(a.X, a.Y), 0, 0, 0);
                pl.AddVertexAt(1, new Point2d(b.X, b.Y), 0, 0, 0);
                pl.Layer = K.LAxo;
                short col = e.SegType == "main" ? (short)1 : e.SegType == "branch" ? (short)5 : (short)4;
                pl.Color = Color.FromColorIndex(ColorMethod.ByAci, col);
                target.AppendEntity(pl); tr.AddNewlyCreatedDBObject(pl, true);

                var mid = new Point3d((a.X + b.X) / 2, (a.Y + b.Y) / 2, 0);
                var label = $"Ø{e.Dy}";
                if (e.Dp > 0) label += $" ΔP={e.Dp:F2}";
                if (e.L > 0) label += $" L={e.L:F1}";
                if (e.CavitationRisk) label += " CAV!";
                if (e.HammerRisk) label += " HAM!";
                var txt = new DBText
                {
                    Position = mid, Height = 35 * scale, TextString = label,
                    Layer = K.LMark, HorizontalMode = TextHorizontalMode.TextCenter, AlignmentPoint = mid
                };
                target.AppendEntity(txt); tr.AddNewlyCreatedDBObject(txt, true);
            }

            // Насадки
            foreach (var kv in nozzlePts)
            {
                var p = T(kv.Value, hNoz);
                double s = 70 * scale;
                var tri = new Polyline(3);
                tri.AddVertexAt(0, new Point2d(p.X, p.Y - s), 0, 0, 0);
                tri.AddVertexAt(1, new Point2d(p.X - s * 0.7, p.Y + s * 0.4), 0, 0, 0);
                tri.AddVertexAt(2, new Point2d(p.X + s * 0.7, p.Y + s * 0.4), 0, 0, 0);
                tri.Closed = true; tri.Layer = K.LAxo;
                tri.Color = Color.FromColorIndex(ColorMethod.ByAci, 3);
                target.AppendEntity(tri); tr.AddNewlyCreatedDBObject(tri, true);
            }

            // Модули
            foreach (var kv in modulePts)
            {
                var p = T(kv.Value, 0);
                double w = 100 * scale, h = 300 * scale;
                var rect = new Polyline(4);
                rect.AddVertexAt(0, new Point2d(p.X - w / 2, p.Y), 0, 0, 0);
                rect.AddVertexAt(1, new Point2d(p.X + w / 2, p.Y), 0, 0, 0);
                rect.AddVertexAt(2, new Point2d(p.X + w / 2, p.Y + h), 0, 0, 0);
                rect.AddVertexAt(3, new Point2d(p.X - w / 2, p.Y + h), 0, 0, 0);
                rect.Closed = true; rect.Layer = K.LAxo;
                rect.Color = Color.FromColorIndex(ColorMethod.ByAci, 1);
                target.AppendEntity(rect); tr.AddNewlyCreatedDBObject(rect, true);

                var t = new DBText
                {
                    Position = new Point3d(p.X, p.Y + h / 2, 0), Height = 35 * scale,
                    TextString = "МГП", Layer = K.LMark,
                    HorizontalMode = TextHorizontalMode.TextCenter,
                    AlignmentPoint = new Point3d(p.X, p.Y + h / 2, 0)
                };
                target.AppendEntity(t); tr.AddNewlyCreatedDBObject(t, true);
            }
        }

        public static void CollectAnchors(Database db, Transaction tr,
            out Dictionary<string, Point3d> noz, out Dictionary<string, Point3d> mod)
        {
            noz = new Dictionary<string, Point3d>();
            mod = new Dictionary<string, Point3d>();
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
            int ni = 0, mi = 0;
            foreach (ObjectId id in ms)
            {
                var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (ent == null) continue;
                string kind = XD.Get(ent, "KIND") ?? "";
                Point3d pt = Point3d.Origin;
                if (ent is Polyline p && p.NumberOfVertices > 0) pt = p.GetPoint3dAt(0);
                else if (ent is Circle c) pt = c.Center;
                if (kind == "NOZZLE") noz["N" + (ni++)] = pt;
                else if (kind == "MODULE") mod["M" + (mi++)] = pt;
            }
        }
    }

    // =========================================================================
    // 5. ЛИСТ + ШТАМП ГОСТ
    // =========================================================================
    public static class Sheet
    {
        // Формат A3 альбом: 420×297 мм → в единицах чертежа (мм)
        public const double SheetW = 4200; // A3 ширина в условных единицах (×10 мм), согласовано с DrawStamp
        // Практично: чертёж часто в мм, лист A3 = 420×297 мм. Берём 4200×2970 (масштаб 10) для читаемости,
        // либо реальные 420×297 если units=м. Здесь: 4200 × 2970 (десятые мм / условные).

        public static ObjectId EnsureLayout(Database db, Transaction tr, string name)
        {
            var lm = LayoutManager.Current;
            try
            {
                var id = lm.GetLayoutId(name);
                if (!id.IsNull) return id;
            }
            catch (System.Exception)
            {
                // layout name may not exist — create below
            }

            ObjectId layoutId = ObjectId.Null;
            try
            {
                var doc = Application.DocumentManager.MdiActiveDocument;
                if (doc != null)
                {
                    using (var docLock = doc.LockDocument())
                    {
                        layoutId = lm.CreateLayout(name);
                    }
                }
                else
                {
                    layoutId = lm.CreateLayout(name);
                }
            }
            catch (System.Exception ex)
            {
                try
                {
                    var doc = Application.DocumentManager.MdiActiveDocument;
                    if (doc != null)
                        doc.Editor.WriteMessage("\n[AGPT] Layout '" + name + "': " + ex.Message);
                }
                catch { }
                // last resort: try get again
                try { layoutId = lm.GetLayoutId(name); } catch { }
            }
            return layoutId;
        }

        public static void DrawStamp(BlockTableRecord paper, Transaction tr,
            string objectName, string sheetTitle, string sheetNo,
            double tPod, double dpTotal, int nSeg, int nNoz, double mg,
            bool hasCav = false, int cavCount = 0, double pSat = 0, string gotv = "",
            bool hasHam = false, int hamCount = 0, double pHamMax = 0,
            double tZpu = 0, double tCrit = 0, string hamMode = "")
        {
            Ly.Ensure(paper.Database, tr, K.LStamp, 7);

            // Рамка A3 (горизонт.): 0,0 — 4200,2970  (условные единицы ≈ 0.1 мм)
            double W = 4200, H = 2970;
            double m = 50; // поле

            // Внешняя рамка
            Rect(paper, tr, m, m, W - m, H - m, K.LStamp);
            // Внутренняя
            Rect(paper, tr, m + 20, m + 20, W - m - 10, H - m - 10, K.LStamp);

            // Штамп основной надписи (справа снизу), упрощённый ГОСТ Р 21.1101
            double sw = 1850, sh = 550;
            double sx = W - m - 10 - sw;
            double sy = m + 20;

            // Сетка штампа
            Rect(paper, tr, sx, sy, sx + sw, sy + sh, K.LStamp);
            HLine(paper, tr, sx, sy + 150, sx + sw, K.LStamp);
            HLine(paper, tr, sx, sy + 300, sx + sw, K.LStamp);
            HLine(paper, tr, sx, sy + 400, sx + sw, K.LStamp);
            VLine(paper, tr, sx + 700, sy, sy + 150, K.LStamp);
            VLine(paper, tr, sx + 1100, sy, sy + 150, K.LStamp);
            VLine(paper, tr, sx + 1400, sy, sy + 150, K.LStamp);

            Text(paper, tr, sx + 20, sy + sh - 80, 45, "АГПТ. Аксонометрия распределительного трубопровода", K.LStamp);
            Text(paper, tr, sx + 20, sy + 320, 35, objectName, K.LStamp);
            Text(paper, tr, sx + 20, sy + 170, 30, sheetTitle, K.LStamp);
            Text(paper, tr, sx + 720, sy + 40, 28, "Лист", K.LStamp);
            Text(paper, tr, sx + 1120, sy + 40, 28, "Листов", K.LStamp);
            Text(paper, tr, sx + 1420, sy + 40, 28, sheetNo, K.LStamp);

            // Блок результатов гидравлики слева от штампа
            double bx = m + 40, by = m + 40;
            Text(paper, tr, bx, by + 320, 36, "Результаты гидравлики", K.LStamp);
            Text(paper, tr, bx, by + 270, 28, $"Mg ≈ {mg:F1} кг  ГОТВ: {gotv}", K.LStamp);
            Text(paper, tr, bx, by + 230, 28, $"t_под = {tPod:F2} с  |  ΣΔP = {dpTotal:F3} бар  |  сегм.: {nSeg}", K.LStamp);
            Text(paper, tr, bx, by + 190, 28, $"ЗПУ: t_zpu={tZpu:F3} с  t_cr={tCrit:F4} с  режим={hamMode}", K.LStamp);
            string cavTxt = hasCav
                ? $"КАВИТАЦИЯ: ДА ({cavCount})  P_sat={pSat:F2}"
                : $"Кавитация: нет  P_sat={pSat:F2}";
            Text(paper, tr, bx, by + 150, 28, cavTxt, K.LStamp);
            string hamTxt = hasHam
                ? $"УДАР: ДА ({hamCount})  Pуд.max={pHamMax:F1} > Pдоп={K.PAllowPipe}"
                : $"Удар: нет  Pуд.max={pHamMax:F1}  Pдоп={K.PAllowPipe}";
            Text(paper, tr, bx, by + 110, 28, hamTxt, K.LStamp);
        }

        static void Rect(BlockTableRecord b, Transaction tr, double x0, double y0, double x1, double y1, string layer)
        {
            var pl = new Polyline(4);
            pl.AddVertexAt(0, new Point2d(x0, y0), 0, 0, 0);
            pl.AddVertexAt(1, new Point2d(x1, y0), 0, 0, 0);
            pl.AddVertexAt(2, new Point2d(x1, y1), 0, 0, 0);
            pl.AddVertexAt(3, new Point2d(x0, y1), 0, 0, 0);
            pl.Closed = true; pl.Layer = layer;
            b.AppendEntity(pl); tr.AddNewlyCreatedDBObject(pl, true);
        }

        static void HLine(BlockTableRecord b, Transaction tr, double x0, double y, double x1, string layer)
        {
            var pl = new Polyline(2);
            pl.AddVertexAt(0, new Point2d(x0, y), 0, 0, 0);
            pl.AddVertexAt(1, new Point2d(x1, y), 0, 0, 0);
            pl.Layer = layer;
            b.AppendEntity(pl); tr.AddNewlyCreatedDBObject(pl, true);
        }

        static void VLine(BlockTableRecord b, Transaction tr, double x, double y0, double y1, string layer)
        {
            var pl = new Polyline(2);
            pl.AddVertexAt(0, new Point2d(x, y0), 0, 0, 0);
            pl.AddVertexAt(1, new Point2d(x, y1), 0, 0, 0);
            pl.Layer = layer;
            b.AppendEntity(pl); tr.AddNewlyCreatedDBObject(pl, true);
        }

        static void Text(BlockTableRecord b, Transaction tr, double x, double y, double h, string s, string layer)
        {
            var t = new DBText { Position = new Point3d(x, y, 0), Height = h, TextString = s, Layer = layer };
            b.AppendEntity(t); tr.AddNewlyCreatedDBObject(t, true);
        }
    }

    // =========================================================================
    // КОМАНДЫ
    // =========================================================================
    public class AgptHydroAxoV2
    {
        /// <summary>AGPT_HYDRO2 — граф + гидравлика + опциональный подбор Dy</summary>
        [CommandMethod("AGPT_HYDRO2")]
        public void Hydro2()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            var db = doc.Database; var ed = doc.Editor;

            var ko = new PromptKeywordOptions("\nПодбирать Dy по ΔP? [Да/Нет]", "Да Нет");
            ko.Keywords.Default = "Да";
            var kr = ed.GetKeywords(ko);
            bool resize = kr.Status != PromptStatus.OK || kr.StringResult != "Нет";

            var tzOpts = new PromptDoubleOptions($"\nВремя закрытия/открытия ЗПУ, с <{K.TZpuDefault}>")
            {
                DefaultValue = K.TZpuDefault,
                UseDefaultValue = true,
                AllowNegative = false,
                AllowZero = false
            };
            var tzRes = ed.GetDouble(tzOpts);
            double tZpu = tzRes.Status == PromptStatus.OK ? tzRes.Value : K.TZpuDefault;

            using (var tr = db.TransactionManager.StartTransaction())
            {
                XD.EnsureApp(db, tr);
                Ly.EnsureAll(db, tr);

                var g = Build.FromDrawing(db, tr);
                if (g.Edges.Count == 0)
                {
                    ed.WriteMessage("\nНет сегментов труб. Сначала AGPT_PIPE.");
                    tr.Commit(); return;
                }

                g.TZpu = tZpu;
                Hydro.Run(g, resize);

                // Запись в XData + обновление Dy на полилинии (только XData; геометрию Dy не меняем)
                foreach (var e in g.Edges.Values)
                {
                    var ent = (Entity)tr.GetObject(e.EntId, OpenMode.ForWrite);
                    XD.Set(ent,
                        ("DY", e.Dy.ToString()),
                        ("DP", e.Dp.ToString("F4")),
                        ("Q", e.Q.ToString("F4")),
                        ("V_FLOW", e.V.ToString("F3")),
                        ("TPOD", g.TPod.ToString("F2")),
                        ("L", e.L.ToString("F3")),
                        ("NODE_A", e.NodeA),
                        ("NODE_B", e.NodeB),
                        ("POUT", e.POut.ToString("F3")),
                        ("NPSH", e.NpshAvail.ToString("F3")),
                        ("CAVIT", e.CavitationRisk ? "1" : "0"),
                        ("PSAT", g.PSat.ToString("F2")),
                        ("GOTV", g.Gotv),
                        ("DP_HAM", e.DpHammer.ToString("F2")),
                        ("P_HAM", e.PHammer.ToString("F2")),
                        ("HAMMER", e.HammerRisk ? "1" : "0"),
                        ("TZPU", g.TZpu.ToString("F3")),
                        ("TCRIT", g.TCrit.ToString("F4")),
                        ("HAM_F", e.HammerFactor.ToString("F3")));
                }

                double tRec = Hydro.RecommendTZpu(g.TCrit);
                ed.WriteMessage("\n=== ГИДРАВЛИКА v2 (граф + NPSH + удар + ЗПУ) ===");
                ed.WriteMessage($"\nУзлов: {g.Nodes.Count}  Рёбер: {g.Edges.Count}  Mg={g.Mg:F1} кг  ГОТВ={g.Gotv}");
                ed.WriteMessage($"\nt_под={g.TPod:F2} с  ΣΔP={g.DpTotal:F3} бар  P_sat={g.PSat:F2} бар");
                ed.WriteMessage($"\n--- ЗПУ ---");
                ed.WriteMessage($"\nL_path.max = {g.LPathMax:F2} м");
                ed.WriteMessage($"\nt_cr = 2·L/c = {g.TCrit:F4} с  (критическое время закрытия)");
                ed.WriteMessage($"\nt_zpu = {g.TZpu:F3} с  (принятое)  |  рекомендуется ≥ {tRec:F3} с");
                ed.WriteMessage($"\nРежим удара: {g.HammerMode}  (factor={ (g.TZpu>0?Math.Min(1.0,g.TCrit/g.TZpu):1):F3})");
                ed.WriteMessage($"\nПодбор Dy: {(resize ? "включён" : "выключен")}");
                if (g.HasCavitation)
                    ed.WriteMessage($"\n*** КАВИТАЦИЯ: {g.CavitationCount} сегм. ***");
                else
                    ed.WriteMessage("\nКавитация: НЕТ");
                if (g.HasHammer)
                    ed.WriteMessage($"\n*** ГИДР. УДАР: {g.HammerCount} сегм.  ΔPmax={g.DpHammerMax:F1}  Pmax={g.PHammerMax:F1} > Pдоп={K.PAllowPipe} ***");
                else
                    ed.WriteMessage($"\nГидр. удар: НЕТ (Pmax={g.PHammerMax:F1} ≤ {K.PAllowPipe} бар)");
                foreach (var e in g.EdgesInFlowOrder())
                {
                    string flag = "";
                    if (e.CavitationRisk) flag += " !CAV!";
                    if (e.HammerRisk) flag += " !HAM!";
                    ed.WriteMessage($"\n  {e.Id} [{e.SegType}] Ø{e.Dy} L={e.L:F2} v={e.V:F2} ΔP={e.Dp:F3} Pвых={e.POut:F2} NPSH={e.NpshAvail:F2} ΔPуд={e.DpHammer:F1} Pуд={e.PHammer:F1}{flag}");
                }

                // Таблица узлов
                ed.WriteMessage("\n--- Узлы ---");
                foreach (var n in g.Nodes.Values.OrderBy(x => x.Id))
                    ed.WriteMessage($"\n  {n.Id} [{n.Kind}] ({n.Pt.X:F0},{n.Pt.Y:F0}) рёбер:{n.EdgeIds.Count}");

                tr.Commit();
            }
        }

        /// <summary>AGPT_AXO2 — аксонометрия в модель (точка вставки)</summary>
        [CommandMethod("AGPT_AXO2")]
        public void Axo2()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            var db = doc.Database; var ed = doc.Editor;

            var origin = ed.GetPoint("\nБазовая точка аксонометрии: ");
            if (origin.Status != PromptStatus.OK) return;

            using (var tr = db.TransactionManager.StartTransaction())
            {
                XD.EnsureApp(db, tr);
                Ly.EnsureAll(db, tr);

                var g = Build.FromDrawing(db, tr);
                // Подтянуть уже посчитанные DP/DY
                foreach (var e in g.Edges.Values)
                {
                    var ent = tr.GetObject(e.EntId, OpenMode.ForRead) as Entity;
                    if (ent == null) continue;
                    e.Dy = (int)XD.GetD(ent, "DY", e.Dy);
                    e.Dp = XD.GetD(ent, "DP");
                    e.Q = XD.GetD(ent, "Q");
                    e.V = XD.GetD(ent, "V_FLOW");
                    g.TPod = XD.GetD(ent, "TPOD", g.TPod);
                }
                if (g.Edges.Values.All(e => e.Dp <= 0))
                    Hydro.Run(g, false);

                Axo.CollectAnchors(db, tr, out var noz, out var mod);
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                Axo.Draw(db, tr, ms, g, origin.Value, 2800, 2500, 1.0, noz, mod);

                var title = new DBText
                {
                    Position = new Point3d(origin.Value.X, origin.Value.Y - 400, 0),
                    Height = 90, TextString = "Аксонометрия АГПТ (v2)", Layer = K.LMark
                };
                ms.AppendEntity(title); tr.AddNewlyCreatedDBObject(title, true);

                tr.Commit();
                ed.WriteMessage($"\n[AXO2] рёбер={g.Edges.Count} насадков={noz.Count} модулей={mod.Count} t_под={g.TPod:F1}с ΣΔP={g.DpTotal:F2}");
            }
        }

        /// <summary>AGPT_AXO_SHEET — аксонометрия на лист Layout + штамп</summary>
        [CommandMethod("AGPT_AXO_SHEET")]
        public void AxoSheet()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            var db = doc.Database; var ed = doc.Editor;

            string layoutName = "АГПТ-Аксонометрия";
            var nameRes = ed.GetString($"\nИмя листа <{layoutName}>: ");
            if (nameRes.Status == PromptStatus.OK && !string.IsNullOrWhiteSpace(nameRes.StringResult))
                layoutName = nameRes.StringResult.Trim();

            var objRes = ed.GetString("\nНаименование объекта: ");
            string objName = objRes.Status == PromptStatus.OK ? objRes.StringResult : "Объект";

            using (var tr = db.TransactionManager.StartTransaction())
            {
                XD.EnsureApp(db, tr);
                Ly.EnsureAll(db, tr);

                var g = Build.FromDrawing(db, tr);
                foreach (var e in g.Edges.Values)
                {
                    var ent = tr.GetObject(e.EntId, OpenMode.ForRead) as Entity;
                    if (ent == null) continue;
                    e.Dy = (int)XD.GetD(ent, "DY", e.Dy);
                    e.Dp = XD.GetD(ent, "DP");
                    e.Q = XD.GetD(ent, "Q");
                    e.V = XD.GetD(ent, "V_FLOW");
                    double tp = XD.GetD(ent, "TPOD");
                    if (tp > 0) g.TPod = tp;
                }
                if (g.Edges.Count > 0 && g.Edges.Values.All(e => e.Dp <= 0))
                    Hydro.Run(g, false);
                g.DpTotal = g.Edges.Values.Sum(e => e.Dp);
                if (g.TPod <= 0) g.TPod = K.TNorm;

                Axo.CollectAnchors(db, tr, out var noz, out var mod);

                // Layout
                ObjectId layoutId;
                try
                {
                    layoutId = LayoutManager.Current.GetLayoutId(layoutName);
                    if (layoutId.IsNull)
                        layoutId = LayoutManager.Current.CreateLayout(layoutName);
                }
                catch
                {
                    layoutId = LayoutManager.Current.CreateLayout(layoutName);
                }

                var layout = (Layout)tr.GetObject(layoutId, OpenMode.ForWrite);
                var paperBtr = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForWrite);

                // Очистка старого содержимого листа (опционально — только наши слои)
                var toErase = new List<ObjectId>();
                foreach (ObjectId id in paperBtr)
                {
                    var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent != null && (ent.Layer == K.LAxo || ent.Layer == K.LStamp || ent.Layer == K.LMark))
                        toErase.Add(id);
                }
                foreach (var id in toErase)
                {
                    var ent = (Entity)tr.GetObject(id, OpenMode.ForWrite);
                    ent.Erase();
                }

                // Штамп + рамка
                int nNoz = noz.Count;
                Sheet.DrawStamp(paperBtr, tr, objName, "Аксонометрическая схема трубопровода", "1",
                    g.TPod, g.DpTotal, g.Edges.Count, nNoz, g.Mg,
                    g.HasCavitation, g.CavitationCount, g.PSat, g.Gotv,
                    g.HasHammer, g.HammerCount, g.PHammerMax,
                    g.TZpu, g.TCrit, g.HammerMode ?? "");

                // Аксонометрия в центре листа
                // Центр рабочей области: ~2100, 1800
                var origin = new Point3d(900, 900, 0);
                // Масштаб подгоняем грубо
                double scale = 0.15;
                Axo.Draw(db, tr, paperBtr, g, origin, 2800, 2500, scale, noz, mod);

                // Заголовок над схемой
                var hdr = new DBText
                {
                    Position = new Point3d(900, 2700, 0),
                    Height = 60,
                    TextString = "Аксонометрия распределительного трубопровода АГПТ",
                    Layer = K.LMark
                };
                paperBtr.AppendEntity(hdr); tr.AddNewlyCreatedDBObject(hdr, true);

                // Легенда
                var leg = new StringBuilder();
                leg.AppendLine("Условные обозначения:");
                leg.AppendLine("  красный — магистраль");
                leg.AppendLine("  синий — ветка");
                leg.AppendLine("  голубой — спуск к насадку");
                leg.AppendLine("  зелёный — насадок");
                var ly = 0;
                foreach (var line in leg.ToString().Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var t = new DBText
                    {
                        Position = new Point3d(3200, 2500 - ly * 50, 0),
                        Height = 28, TextString = line, Layer = K.LMark
                    };
                    paperBtr.AppendEntity(t); tr.AddNewlyCreatedDBObject(t, true);
                    ly++;
                }

                // Таблица сегментов
                double ty = 2400;
                var th = new DBText
                {
                    Position = new Point3d(50, ty, 0), Height = 28,
                    TextString = "Сегм | Тип | Dy | L | ΔP | Pвых | NPSH | ΔPуд | Pуд | CAV | HAM",
                    Layer = K.LMark
                };
                paperBtr.AppendEntity(th); tr.AddNewlyCreatedDBObject(th, true);
                ty -= 40;
                foreach (var e in g.EdgesInFlowOrder())
                {
                    var row = $"{e.Id}|{e.SegType}|{e.Dy}|{e.L:F1}|{e.Dp:F2}|{e.POut:F1}|{e.NpshAvail:F1}|{e.DpHammer:F1}|{e.PHammer:F1}|{(e.CavitationRisk?"ДА":"-")}|{(e.HammerRisk?"ДА":"-")}";
                    var rt = new DBText
                    {
                        Position = new Point3d(50, ty, 0), Height = 26,
                        TextString = row, Layer = K.LMark
                    };
                    paperBtr.AppendEntity(rt); tr.AddNewlyCreatedDBObject(rt, true);
                    ty -= 32;
                    if (ty < 700) break;
                }

                LayoutManager.Current.CurrentLayout = layoutName;
                tr.Commit();
                ed.WriteMessage($"\n[AXO_SHEET] Лист «{layoutName}»: аксонометрия + штамп + таблица сегментов");
                ed.WriteMessage($"\nt_под={g.TPod:F2} с  ΣΔP={g.DpTotal:F3} бар  Mg={g.Mg:F1} кг");
            }
        }

        /// <summary>Полный цикл: HYDRO2 (с подбором Dy) → AXO_SHEET</summary>
        [CommandMethod("AGPT_HYDRO_AXO_SHEET")]
        public void Full()
        {
            Hydro2();
            AxoSheet();
        }
    }
}
