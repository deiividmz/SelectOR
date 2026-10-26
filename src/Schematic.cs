// PUESTO DE MANDO (1.2.75): el esquema de una estación de la sala, generado de la propia ruta (RouteGraph), como los
// esquemas de los puestos de mando reales:
//  · las vías largas y rectas (de estación y de línea) son líneas horizontales, cada una en su fila, por su posición
//    lateral; dos que coinciden a lo largo nunca comparten fila;
//  · cada aguja va en la fila de su vía (la de su tronco o su rama recta: la que menos se desvía del tronco);
//  · todo lo demás (escaleras, escapes, enlaces) va de fila a fila a 45°; si una diagonal no cabe, el esquema se estira
//    a lo ancho desde ese punto (como en las cabeceras reales);
//  · las travesías (varias agujas con el mismo identificador) son un solo punto con su rombo; los cruces sin agujas, una X;
//  · cada salida en su propia fila, con la estación siguiente;
//  · andenes (con su número de vía) y señales principales (las del mapa de la sala, con su aspecto en directo).
// Coordenadas del esquema: píxeles a escala 1 (x a lo largo, y hacia abajo; fila r en y = r·Row).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.RegularExpressions;

namespace SelectOR
{
    public sealed class Schematic
    {
        public const float Row = 26f;   // (1.2.75, estilo ADIF: filas más separadas, que se lean bien)
        const double Scale = 0.9, Gap = 8, VMax = 650;

        public sealed class Track
        {
            public int Edge; public PointF[] Pts; public bool Flat; public int RowIdx = -1;
            // (para la auditoría) el nodo y el tipo de cada extremo (j aguja/unión · g travesía · e topera · x salida),
            // su posición lateral típica y su tramo a lo largo del eje
            public int[] EndNode = new int[2]; public char[] EndKind = new char[2]; public double V, UMin, UMax, UA, UB;   // UA/UB: posición real a lo largo en sus extremos
            internal (double off, double u)[] Map;   // (vía en fila) del punto del tramo a la posición a lo largo
            internal double LenPx;
        }
        public sealed class Switch { public int Node, Order = -1; public PointF At; public PointF[][] Legs = new PointF[2][]; public bool Hidden; }
        public sealed class Signal { public RoomMapGeo.Sig Sig; public PointF At; public int Face; public int RowIdx; public int Stack; }   // Stack: varias en el mismo sitio, una encima de otra
        public sealed class Platform { public RectangleF Bar; public string Label; }
        public sealed class Exit { public PointF At; public bool Right; public string Station; public int Edge = -1, Side; }   // Edge/Side: el tramo y su extremo por donde sale

        public string Station;
        public readonly List<Track> Tracks = new();
        public readonly List<Switch> Switches = new();
        public readonly List<PointF> Slips = new(), Crossings = new();
        public readonly List<int> SlipNodes = new();   // las agujas de las travesías (no se dibujan sueltas)
        public readonly List<int> Missing = new();     // (auditoría) tramos dentro de la zona que no se dibujan (los de dentro de una travesía)
        public readonly List<PointF> Buffers = new();
        public readonly List<Signal> Signals = new();
        public readonly List<Platform> Platforms = new();
        public readonly List<Exit> Exits = new();
        public RectangleF Bounds;
        public int Rows;

        RouteGraph _g; double _cx, _cy, _ax, _ay, _u0, _u1;
        Dictionary<int, (double u, double v)> _nodeState = new();
        readonly Dictionary<int, List<(double u, double v, double off)>> _full = new();   // cada punto de cada tramo (sin recortar)
        readonly Dictionary<int, (double a, double b)> _offRange = new();                  // lo que se dibuja de cada tramo
        readonly List<(double u, double dx)> _breaks = new();
        readonly Dictionary<int, Track> _byEdge = new();

        // ── (1.2.75, nudos) de quién es cada aguja: distancia por la vía de los andenes de cada estación a cada nodo
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<RouteGraph, Dictionary<string, Dictionary<int, double>>> _stDist = new();
        static Dictionary<string, Dictionary<int, double>> StationDist(RouteGraph g) => _stDist.GetValue(g, gg =>
        {
            var res = new Dictionary<string, Dictionary<int, double>>(StringComparer.Ordinal);
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var E in gg.Edges) foreach (var (_, nm) in E.Platforms) if (!string.IsNullOrWhiteSpace(nm)) names.Add(nm);
            foreach (string nm in names)
            {
                var d = new Dictionary<int, double>(); var pq = new PriorityQueue<int, double>();
                void Seed(int n, double v) { if (n < 0) return; if (!d.TryGetValue(n, out double o) || v < o) { d[n] = v; pq.Enqueue(n, v); } }
                foreach (var E in gg.Edges)
                {
                    var offs = E.Platforms.Where(t => t.name == nm).Select(t => t.off).ToList();
                    if (offs.Count == 0) continue;
                    Seed(E.A, offs.Min()); Seed(E.B, E.Len - offs.Max());
                }
                while (pq.TryDequeue(out int n, out double dn))
                {
                    if (dn > d[n] + 1e-6 || dn > 5000) continue;
                    foreach (int e in gg.EdgesAt(n))
                    {
                        var E = gg.Edges[e]; int m = E.A == n ? E.B : E.A; if (m < 0) continue;
                        double nd = dn + E.Len;
                        if (!d.TryGetValue(m, out double om) || nd < om) { d[m] = nd; pq.Enqueue(m, nd); }
                    }
                }
                res[nm] = d;
            }
            return res;
        });

        // ── (1.2.75) escapes en tijera: cuatro agujas (dos en cada vía) unidas por dos vías paralelas y dos diagonales que se
        // cruzan. Se tratan juntas: en cada aguja la recta es la paralela y la desviada la diagonal. Por los ángulos solos no
        // sale (en el Museo las vías de la tijera están a 2 m y la diagonal sale casi igual que la recta, a veces más recta), y
        // el cruce de la ruta a veces viene apuntado en las paralelas: las diagonales son las dos que se cortan de verdad.
        public sealed class ScissorsInfo
        {
            public readonly Dictionary<int, int> Straight = new();   // aguja → su paralela
            public readonly HashSet<int> Diagonals = new(), Parallels = new();
        }
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<RouteGraph, ScissorsInfo> _scissors = new();
        public static ScissorsInfo Scissors(RouteGraph g) => _scissors.GetValue(g, gg =>
        {
            var res = new ScissorsInfo();
            (double x, double y) P(int n) { foreach (int e in gg.EdgesAt(n)) { var E = gg.Edges[e]; return E.A == n ? (E.X[0], E.Y[0]) : (E.X[^1], E.Y[^1]); } return (0, 0); }
            int Other(int e, int n) => gg.Edges[e].A == n ? gg.Edges[e].B : gg.Edges[e].A;
            // ramas cortas de una aguja hacia otras agujas a las que también llegan por una rama: (tramo, aguja del otro lado)
            List<(int e, int m)> Br(int n)
            {
                var l = new List<(int, int)>();
                if (!gg.NodeLegs(n, out var tr, out var br) || tr.Length != 1 || br.Length != 2) return l;
                foreach (int e in br)
                {
                    int m = Other(e, n);
                    if (m < 0 || m == n || gg.Edges[e].Len > 80 || !gg.NodeLegs(m, out var tm, out var bm) || tm.Length != 1 || bm.Length != 2 || !bm.Contains(e)) continue;
                    l.Add((e, m));
                }
                return l;
            }
            static bool Cross((double x, double y) a, (double x, double y) b, (double x, double y) c, (double x, double y) d)
            {
                double r1 = b.x - a.x, r2 = b.y - a.y, s1 = d.x - c.x, s2 = d.y - c.y, den = r1 * s2 - r2 * s1;
                if (Math.Abs(den) < 1e-9) return false;
                double t = ((c.x - a.x) * s2 - (c.y - a.y) * s1) / den, u = ((c.x - a.x) * r2 - (c.y - a.y) * r1) / den;
                return t > 0.05 && t < 0.95 && u > 0.05 && u < 0.95;
            }
            var done = new HashSet<int>();
            var nodes = new HashSet<int>();
            foreach (var E in gg.Edges) { if (E.A >= 0) nodes.Add(E.A); if (E.B >= 0) nodes.Add(E.B); }
            foreach (int n in nodes)
            {
                if (done.Contains(n)) continue;
                var bn = Br(n); if (bn.Count != 2 || bn[0].m == bn[1].m) continue;
                int m0 = bn[0].m, m1 = bn[1].m;
                var b0 = Br(m0); var b1 = Br(m1); if (b0.Count != 2 || b1.Count != 2) continue;
                int n2 = b0.Select(x => x.m).FirstOrDefault(x => x != n, -1); if (n2 < 0 || n2 == m1) continue;
                if (!b1.Any(x => x.m == n2) || !b1.Any(x => x.m == n)) continue;
                var b2 = Br(n2); if (b2.Count != 2 || !b2.Any(x => x.m == m0) || !b2.Any(x => x.m == m1)) continue;
                int e_n0 = bn[0].e, e_n1 = bn[1].e, e_20 = b2.First(x => x.m == m0).e, e_21 = b2.First(x => x.m == m1).e;
                // diagonales: n→m0 con n2→m1, o n→m1 con n2→m0; la pareja que se corta
                int[] par, dia;
                if (Cross(P(n), P(m0), P(n2), P(m1)) && !Cross(P(n), P(m1), P(n2), P(m0))) { dia = new[] { e_n0, e_21 }; par = new[] { e_n1, e_20 }; }
                else if (Cross(P(n), P(m1), P(n2), P(m0)) && !Cross(P(n), P(m0), P(n2), P(m1))) { dia = new[] { e_n1, e_20 }; par = new[] { e_n0, e_21 }; }
                else continue;
                foreach (int q in new[] { n, n2, m0, m1 })
                {
                    done.Add(q);
                    res.Straight[q] = gg.EdgesAt(q).First(e => par.Contains(e));
                }
                foreach (int e in dia) res.Diagonals.Add(e);
                foreach (int e in par) res.Parallels.Add(e);
            }
            return res;
        });

        // ── estaciones de la ruta (las de los andenes), con el centro de sus andenes
        public static List<(string name, double x, double y, int n)> Stations(RouteGraph g)
        {
            var acc = new Dictionary<string, (double x, double y, int n)>(StringComparer.Ordinal);
            if (g?.Edges == null) return new();
            for (int e = 0; e < g.Edges.Length; e++)
                foreach (var (off, name) in g.Edges[e].Platforms)
                {
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    var (x, y) = g.PointAt(e, off);
                    acc.TryGetValue(name, out var a); acc[name] = (a.x + x, a.y + y, a.n + 1);
                }
            return acc.Select(kv => (kv.Key, kv.Value.x / kv.Value.n, kv.Value.y / kv.Value.n, kv.Value.n)).OrderBy(t => t.Key, StringComparer.CurrentCulture).ToList();
        }

        double U(double x, double y) => _flip * ((x - _cx) * _ax + (y - _cy) * _ay);
        double V(double x, double y) => _flip * (-(x - _cx) * _ay + (y - _cy) * _ax);   // (_flip: media vuelta, u y v a la vez; un espejo cambiaría el lado de las agujas)


        double _flip = 1;
        bool Mine(string name) => name == Station;   // (un andén de esta estación)
        public double AxisU(double x, double y) => U(x, y);   // (auditoría)
        public bool Drawn(int e) => _byEdge.ContainsKey(e);
        public double AxisV(double x, double y) => V(x, y);
        public (double u0, double u1) Region => (_u0, _u1);
        // A lo largo, por tramos (1.2.75): entre dos cosas seguidas (agujas, señales, extremos de andén, toperas…) un ancho
        // según lo que las separa, pero no proporcional: lo corto se abre (las cabeceras, con agujas cada pocos metros, se
        // leían apelotonadas) y lo largo se acorta (kilómetros de vía sin nada). Encima, lo que se estira para que quepan las
        // diagonales a 45° (_breaks).
        List<(double u, double x)> _knots;
        const double MinGap = 12;
        static double Gx(double du) => du <= 60 ? Math.Max(MinGap, du * 1.5) : 90 + 55 * Math.Log(du / 60);
        public float X(double u)
        {
            double x;
            var k = _knots;
            if (k == null || k.Count < 2) x = (u - _u0) * Scale;
            else if (u <= k[0].u) x = k[0].x + (u - k[0].u) * Scale;
            else if (u >= k[^1].u) x = k[^1].x + (u - k[^1].u) * Scale;
            else
            {
                int lo = 0, hi = k.Count - 1;
                while (hi - lo > 1) { int m = (lo + hi) / 2; if (k[m].u <= u) lo = m; else hi = m; }
                double t = (u - k[lo].u) / Math.Max(1e-9, k[hi].u - k[lo].u);
                x = k[lo].x + (k[hi].x - k[lo].x) * t;
            }
            foreach (var (bu, dx) in _breaks) if (bu <= u) x += dx;
            return (float)x;
        }


        // ── zonas: las estaciones y los tramos de línea que no cubre ninguna (1.2.75, por la auditoría: entre estaciones
        // lejanas quedaban kilómetros de línea, con sus señales, fuera de todos los esquemas)
        public sealed class Zone { public string Name; public List<(double x, double y)> Anchors; public bool Line; }
        List<(double x, double y)> _anchors; bool _line;

        // (panel por línea) girado o no: flip = 1 como sale, −1 media vuelta
        public static Schematic Build(RouteGraph g, RoomMapGeo geo, Zone z, int flip)
        {
            var s = new Schematic { _g = g, Station = z.Name, _anchors = z.Line ? z.Anchors : null, _line = z.Line, _flip = flip };
            return s.Make(geo) ? s : null;
        }
        public int Flip => (int)_flip;

        public static Schematic Build(RouteGraph g, RoomMapGeo geo, Zone z)
        {
            if (!z.Line) return Build(g, geo, z.Name);
            var s = new Schematic { _g = g, Station = z.Name, _anchors = z.Anchors, _line = true };
            return s.Make(geo) ? s : null;
        }

        // ¿Está este punto del tramo e dibujado en el esquema?
        // (con 25 m de margen: lo que queda justo en el borde va también a un tramo de línea)
        public bool Covers(int e, double off)
        {
            if (Missing.Contains(e)) return true;   // (1.2.75) los trocitos de dentro de una travesía: van en su rombo (si no, cada travesía creaba un tramo de línea que repetía la estación)
            if (!_byEdge.ContainsKey(e) || !_offRange.TryGetValue(e, out var r)) return false;
            if (r.a <= 0.5 && r.b >= _g.Edges[e].Len - 0.5) return true;   // (entero, también sus puntas)
            return off >= r.a + 25 && off <= r.b - 25;
        }
        // (auditoría) las coordenadas del esquema (u a lo largo, v a lo ancho) de un punto de un tramo
        public bool PathUV(int e, double off, out double u, out double v)
        {
            u = v = 0;
            if (!_full.TryGetValue(e, out var l) || l.Count < 2) return false;
            int i = 0; while (i + 2 < l.Count && l[i + 1].off < off) i++;
            double span = l[i + 1].off - l[i].off, t = span > 0 ? Math.Max(0, Math.Min(1, (off - l[i].off) / span)) : 0;
            u = l[i].u + (l[i + 1].u - l[i].u) * t; v = l[i].v + (l[i + 1].v - l[i].v) * t; return true;
        }

        // Las estaciones (sus nombres) y los tramos de línea sin estación, troceados (de hasta 3 km), con nombre
        // «Línea A – B» (las dos estaciones más cercanas a sus extremos).
        public static List<Zone> Zones(RouteGraph g)
        {
            var st = Stations(g);
            var zones = st.Select(t => new Zone { Name = t.name }).ToList();
            var built = st.Select(t => { try { return Build(g, null, t.name); } catch { return null; } }).Where(x => x != null).ToList();
            // puntos de vía (cada 40 m) que no salen en ninguna estación
            var unc = new List<(double x, double y)>();
            for (int e = 0; e < g.Edges.Length; e++)
            {
                var E = g.Edges[e];
                for (double off = 0; off <= E.Len; off += 40)
                {
                    var (x, y) = g.PointAt(e, off);
                    if (!built.Any(b => b.Covers(e, off))) unc.Add((x, y));
                }
            }
            // grupos de puntos juntos (a menos de 90 m)
            var grid = new Dictionary<(int, int), List<int>>();
            for (int i = 0; i < unc.Count; i++) { var k = ((int)Math.Floor(unc[i].x / 90), (int)Math.Floor(unc[i].y / 90)); if (!grid.TryGetValue(k, out var l)) grid[k] = l = new(); l.Add(i); }
            var comp = new int[unc.Count]; for (int i = 0; i < comp.Length; i++) comp[i] = -1;
            int nc = 0;
            for (int i = 0; i < unc.Count; i++)
            {
                if (comp[i] >= 0) continue;
                var q = new Queue<int>(); q.Enqueue(i); comp[i] = nc;
                while (q.Count > 0)
                {
                    int a = q.Dequeue(); var (ax, ay) = unc[a]; int gx = (int)Math.Floor(ax / 90), gy = (int)Math.Floor(ay / 90);
                    for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++)
                        {
                            if (!grid.TryGetValue((gx + dx, gy + dy), out var l)) continue;
                            foreach (int b in l) if (comp[b] < 0 && (unc[b].x - ax) * (unc[b].x - ax) + (unc[b].y - ay) * (unc[b].y - ay) < 90 * 90) { comp[b] = nc; q.Enqueue(b); }
                        }
                }
                nc++;
            }
            var used = new HashSet<string>(zones.Select(z => z.Name));
            for (int c = 0; c < nc; c++)
            {
                var pts = Enumerable.Range(0, unc.Count).Where(i => comp[i] == c).Select(i => unc[i]).ToList();
                if (pts.Count < 4) continue;   // menos de ~150 m
                // en trozos de hasta 3 km a lo largo de su eje
                double cx = pts.Average(p => p.x), cy = pts.Average(p => p.y), sxx = 0, syy = 0, sxy = 0;
                foreach (var (x, y) in pts) { sxx += (x - cx) * (x - cx); syy += (y - cy) * (y - cy); sxy += (x - cx) * (y - cy); }
                double a0 = 0.5 * Math.Atan2(2 * sxy, sxx - syy), ux = Math.Cos(a0), uy = Math.Sin(a0);
                var byU = pts.Select(p => (u: (p.x - cx) * ux + (p.y - cy) * uy, p)).OrderBy(t => t.u).ToList();
                double umin = byU[0].u, umax = byU[^1].u; int parts = Math.Max(1, (int)Math.Ceiling((umax - umin) / 3000));
                for (int k = 0; k < parts; k++)
                {
                    double lo = umin + (umax - umin) * k / parts, hi = umin + (umax - umin) * (k + 1) / parts;
                    var chunk = byU.Where(t => t.u >= lo - 1 && t.u <= hi + 1).Select(t => t.p).ToList();
                    if (chunk.Count < 4) continue;
                    string Near((double x, double y) p) => st.OrderBy(t => (t.x - p.x) * (t.x - p.x) + (t.y - p.y) * (t.y - p.y)).First().name;
                    var first = byU.First(t => t.u >= lo - 1).p; var last = byU.Last(t => t.u <= hi + 1).p;
                    string a = Near(first), b = Near(last);
                    string name = a == b ? "Junto a " + a : "Línea " + string.Join(" – ", new[] { a, b }.OrderBy(x => x, StringComparer.CurrentCulture));   // (sin traducir: el nombre es el identificador de la dependencia en la sala, igual para todos)
                    string nm = name; int n = 2; while (!used.Add(nm)) nm = name + " (" + n++ + ")";
                    zones.Add(new Zone { Name = nm, Anchors = chunk, Line = true });
                }
            }
            return zones;
        }

        public static Schematic Build(RouteGraph g, RoomMapGeo geo, string station)
        {
            var s = new Schematic { _g = g, Station = station };
            if (!s.Make(geo)) return null;
            // la vía 1 arriba: si los números de vía crecen hacia arriba, se hace al revés
            var nums = s.Platforms.Where(p => p.Label != null && int.TryParse(p.Label, out _)).Select(p => (n: int.Parse(p.Label), y: p.Bar.Top)).ToList();
            if (nums.Count >= 2)
            {
                double mn = nums.Average(t => t.n), my = nums.Average(t => t.y), cov = nums.Sum(t => (t.n - mn) * (t.y - my));
                if (cov < 0) { var f = new Schematic { _g = g, Station = station, _flip = -1 }; if (f.Make(geo)) return f; }
            }
            return s;
        }

        bool Make(RoomMapGeo geo)
        {
            var g = _g; if (g?.Edges == null) return false;
            // andenes de la estación → centro y eje (componente principal)
            var pp = new List<(double x, double y)>();
            if (_anchors != null) pp.AddRange(_anchors);   // (un tramo de línea: sus puntos)
            else
            {
                // (1.2.75) los nudos: una estación con andenes en vías que se cruzan en ángulo (dos líneas). El eje sale solo del
                // grupo de andenes de la orientación con más andén; el otro grupo se trata como las vías que salen de la estación
                // (enderezado por su propio recorrido), y queda horizontal y en paralelo. Con un solo eje para todos, ninguno de
                // los dos grupos quedaba recto y el nudo se dibujaba con diagonales largas que se cruzaban.
                var plat = new List<(double x, double y, double a)>();
                for (int e = 0; e < g.Edges.Length; e++)
                {
                    var offs = g.Edges[e].Platforms.Where(t => Mine(t.name)).Select(t => t.off).OrderBy(o => o).ToList();
                    if (offs.Count == 0) continue;
                    double L = g.Edges[e].Len;
                    // (de un extremo al otro de los andenes del tramo: en una estación en curva, la tangente cambia mucho)
                    double o1 = offs[0], o2 = offs[^1];
                    if (o2 - o1 < 20) { o1 = Math.Max(0, o1 - 10); o2 = Math.Min(L, o2 + 10); }
                    var p1 = g.PointAt(e, o1); var p2 = g.PointAt(e, o2);
                    double a = Math.Atan2(p2.y - p1.y, p2.x - p1.x); a = ((a % Math.PI) + Math.PI) % Math.PI;
                    foreach (double off in offs) { var p = g.PointAt(e, off); plat.Add((p.x, p.y, a)); }
                }
                double AngDiff(double a, double b) { double d = Math.Abs(a - b) % Math.PI; return Math.Min(d, Math.PI - d); }
                const double Tol = 22 * Math.PI / 180;
                double bestA = 0; int bestN = -1;
                foreach (var c in plat) { int n = plat.Count(o => AngDiff(o.a, c.a) < Tol); if (n > bestN) { bestN = n; bestA = c.a; } }
                pp.AddRange(plat.Where(o => AngDiff(o.a, bestA) < Tol).Select(o => (o.x, o.y)));
            }
            if (pp.Count == 0) return false;
            _cx = pp.Average(p => p.x); _cy = pp.Average(p => p.y);
            double sxx = 0, syy = 0, sxy = 0;
            foreach (var (x, y) in pp) { double dx = x - _cx, dy = y - _cy; sxx += dx * dx; syy += dy * dy; sxy += dx * dy; }
            double ang = 0.5 * Math.Atan2(2 * sxy, sxx - syy);
            if (pp.Count < 3 || sxx + syy < 100)
            {
                // pocos andenes: el eje de la vía en la que están
                for (int e = 0; e < g.Edges.Length; e++)
                    if (g.Edges[e].Platforms.Any(p => p.name == Station)) { var E = g.Edges[e]; ang = Math.Atan2(E.Y[^1] - E.Y[0], E.X[^1] - E.X[0]); break; }
            }
            _ax = Math.Cos(ang); _ay = Math.Sin(ang);
            // sentido: hacia donde hay más vía
            double tot = 0;
            foreach (var E in g.Edges) for (int i = 0; i < E.X.Length; i++) { double u = U(E.X[i], E.Y[i]), v = V(E.X[i], E.Y[i]); if (Math.Abs(u) < 1500 && Math.Abs(v) < 400) tot += u; }
            if (tot < 0) { _ax = -_ax; _ay = -_ay; }
            // ── coordenadas del esquema. Cerca de la estación, las del plano (u a lo largo del eje, v a lo ancho). Al alejarse,
            // u es la distancia recorrida por la propia vía y v, la posición lateral con la que esa vía salió de la estación:
            // las curvas y los bucles de las líneas quedan enderezados, como en un esquema real (con un solo eje recto para
            // 5 km, una línea que da la vuelta salía en el lado contrario, cruzando otras o encima de ellas).
            double pu0 = pp.Min(p => U(p.x, p.y)), pu1 = pp.Max(p => U(p.x, p.y));
            // (1.2.75) la estación, como en un esquema de ADIF: sus andenes y sus cabeceras; por las vías que salen, como mucho
            // 700 m (antes 2,5 km: se colaban las playas de las estaciones de al lado)
            double nearPad = _line ? 60 : 250, vNear = _line ? 400 : 250, lmax = _line ? 60 : 700;
            // (1.2.75) a lo ancho, la posición real solo en la franja de la estación (sus andenes y 45 m a cada lado); fuera de
            // ella, cada vía sigue recta desde donde sale: una línea que entra en curva ya no baja en una diagonal larga que
            // cruza las demás (en un esquema de ADIF las líneas entran rectas)
            {
                double pv0 = pp.Min(p => V(p.x, p.y)), pv1 = pp.Max(p => V(p.x, p.y)), spread = Math.Max(Math.Abs(pv0), Math.Abs(pv1));
                // (1.2.75) en un tramo de línea, el ancho de sus propias vías y 30 m (con 400 m, un tramo junto a una estación
                // grande se quedaba con la estación entera y la dibujaba otra vez, mal: «Junto a Estación Sur»)
                vNear = _line ? Math.Max(30, Math.Min(400, spread + 30)) : Math.Max(45, Math.Min(250, spread + 45));
            }
            // (1.2.75, nudos) la franja acaba a medio camino del andén de otra estación que haya dentro de ella (a lo largo):
            // si no, se colaba la estación de al lado entera, con su playa y sus líneas (Museo con Domingo Gil)
            double loU = pu0 - nearPad, hiU = pu1 + nearPad;
            if (!_line)
                for (int e = 0; e < g.Edges.Length; e++)
                    foreach (var (off, name) in g.Edges[e].Platforms)
                    {
                        if (Mine(name) || string.IsNullOrWhiteSpace(name)) continue;
                        var (ox, oy) = g.PointAt(e, off); double ou = U(ox, oy), ov = V(ox, oy);
                        if (Math.Abs(ov) > vNear + 30) continue;
                        if (ou > pu1 && ou < hiU + 1) hiU = Math.Min(hiU, Math.Max(pu1 + 60, (pu1 + ou) / 2));
                        if (ou < pu0 && ou > loU - 1) loU = Math.Max(loU, Math.Min(pu0 - 60, (pu0 + ou) / 2));
                    }
            bool NearPt(double x, double y) { double u = U(x, y), v = V(x, y); return u >= loU && u <= hiU && Math.Abs(v) <= vNear; }
            bool NearQ(int e, (double x, double y, double off) q) => NearPt(q.x, q.y);
            bool NearNode(int n, double x, double y) => NearPt(x, y);
            (double x, double y) NodeXY(int n)
            {
                foreach (int e in g.EdgesAt(n)) { var E = g.Edges[e]; return E.A == n ? (E.X[0], E.Y[0]) : (E.X[^1], E.Y[^1]); }
                return (double.NaN, double.NaN);
            }
            // puntos de cada tramo (cada 20 m como mucho)
            var dense = new Dictionary<int, List<(double x, double y, double off)>>();
            List<(double x, double y, double off)> Dense(int e)
            {
                if (dense.TryGetValue(e, out var l)) return l;
                var E = g.Edges[e]; l = new();
                for (int i = 0; i < E.X.Length; i++)
                {
                    if (i > 0)
                    {
                        double span = E.Cum[i] - E.Cum[i - 1]; int n = (int)(span / 20);
                        for (int k = 1; k <= n; k++) { double f = (double)k / (n + 1); l.Add((E.X[i - 1] + (E.X[i] - E.X[i - 1]) * f, E.Y[i - 1] + (E.Y[i] - E.Y[i - 1]) * f, E.Cum[i - 1] + span * f)); }
                    }
                    l.Add((E.X[i], E.Y[i], E.Cum[i]));
                }
                dense[e] = l; return l;
            }
            // (1.2.75, nudos) cuál de las dos ramas es la recta: la que menos se desvía del tronco; si las dos salen casi igual
            // (menos de 3° de diferencia: escapes en tijera, agujas simétricas), la que a 30 m sigue más cerca de la prolongación
            // del tronco (la desviada ya se ha ido casi una vía entera). Con solo el ángulo, en una tijera se tomaba como recta el
            // escape y la vía entera quedaba descolocada.
            var sciss = Scissors(g);
            int StraightLeg(int n, int trunk, int b0, int b1)
            {
                if (sciss.Straight.TryGetValue(n, out int sp) && (sp == b0 || sp == b1)) return sp;   // (tijera)
                double Ang0(int e) => g.LegAngle(e, g.Edges[e].A == n);
                double Dv(int e) => Math.Abs(((Ang0(e) - (Ang0(trunk) + 180)) % 360 + 540) % 360 - 180);
                double d0 = Dv(b0), d1 = Dv(b1);
                if (Math.Abs(d0 - d1) >= 3) return d0 <= d1 ? b0 : b1;
                (double x, double y) At2(int e, double d) { var E = g.Edges[e]; return g.PointAt(e, E.A == n ? Math.Min(d, E.Len) : Math.Max(0, E.Len - d)); }
                var o = At2(trunk, 0); var t = At2(trunk, 25);
                double dx = o.x - t.x, dy = o.y - t.y, l = Math.Sqrt(dx * dx + dy * dy); if (l < 1e-6) return d0 <= d1 ? b0 : b1;
                dx /= l; dy /= l;
                double Lat(int e) { var p = At2(e, 30); return Math.Abs((p.x - o.x) * dy - (p.y - o.y) * dx); }
                double l0 = Lat(b0), l1 = Lat(b1);
                if (Math.Abs(l0 - l1) < 0.3) return d0 <= d1 ? b0 : b1;
                return l0 <= l1 ? b0 : b1;
            }
            // papeles de una aguja: tronco, recta, desviada y si la desviada tuerce a la derecha de la recta (+1) o a la izquierda
            var roles = new Dictionary<int, (int t, int st, int dv, int side)?>();
            (int t, int st, int dv, int side)? Roles(int n)
            {
                if (roles.TryGetValue(n, out var r)) return r;
                r = null;
                if (g.NodeLegs(n, out var tr, out var br) && tr.Length == 1 && br.Length == 2)
                {
                    double Ang(int e) => g.LegAngle(e, g.Edges[e].A == n);
                    double Nrm(double a) => ((a % 360) + 540) % 360 - 180;
                    int st = StraightLeg(n, tr[0], br[0], br[1]), dv = st == br[0] ? br[1] : br[0];
                    r = (tr[0], st, dv, Nrm(Ang(dv) - Ang(st)) >= 0 ? 1 : -1);
                }
                roles[n] = r; return r;
            }
            const double Spacing = 4.5;   // (una vía de distancia a lo ancho)
            // estado de cada nodo: (u, v, distancia por la vía desde la parte cercana, sentido hacia fuera, límite de su rama)
            // (en la parte lejana, v es la de la vía principal de la aguja; arr: el tramo por el que se llegó)
            var ns = new Dictionary<int, (double u, double v, double d, int s, double lim)>();
            var arrived = new Dictionary<int, int>();
            var pq = new PriorityQueue<int, double>();
            int Sign(double d) => d >= 0 ? 1 : -1;
            for (int e = 0; e < g.Edges.Length; e++)
            {
                var E = g.Edges[e];
                foreach (int n in new[] { E.A, E.B })
                {
                    if (n < 0 || ns.ContainsKey(n)) continue;
                    var (x, y) = NodeXY(n);
                    if (NearNode(n, x, y)) { ns[n] = (U(x, y), V(x, y), 0, 0, lmax); pq.Enqueue(n, 0); }
                }
            }
            // (un tramo que pasa por la parte cercana sin tener ahí ningún extremo: sus extremos, desde donde sale de ella)
            for (int e = 0; e < g.Edges.Length; e++)
            {
                var E = g.Edges[e]; if (E.A < 0 || E.B < 0 || ns.ContainsKey(E.A) && ns.ContainsKey(E.B)) continue;
                var D = Dense(e); int first = D.FindIndex(q => NearQ(e, q)); if (first < 0) continue;
                int last = D.FindLastIndex(q => NearQ(e, q));
                foreach (var (n, bi, step) in new[] { (E.A, first, -1), (E.B, last, 1) })
                {
                    if (ns.ContainsKey(n)) continue;
                    var b = D[bi]; int j = Math.Max(0, Math.Min(D.Count - 1, bi + step));
                    double du = U(D[j].x, D[j].y) - U(b.x, b.y); double dist = step > 0 ? E.Len - b.off : b.off;
                    ns[n] = (U(b.x, b.y) + Sign(du) * dist, V(b.x, b.y), dist, Sign(du), lmax); pq.Enqueue(n, dist);
                }
            }
            // (1.2.75, nudos) cada aguja, de una sola estación: la que la tiene más cerca por la vía. Al llegar a una de otra
            // estación (100 m más cerca de sus andenes que de los nuestros), la rama acaba antes de ella, con su flecha, y no
            // sigue: como en los esquemas de ADIF, cada estación con lo suyo (la escalera de escapes entre Princesa Susana y
            // Parque Grande salía en los dos, y en Parque Grande mal colocada)
            var foreign = new HashSet<int>();
            Dictionary<int, double> myDist = null; List<Dictionary<int, double>> otherDist = null;
            if (!_line)
            {
                var stDist = StationDist(g);
                stDist.TryGetValue(Station, out myDist);
                otherDist = stDist.Where(kv => kv.Key != Station).Select(kv => kv.Value).ToList();
            }
            bool Foreign(int n)
            {
                if (myDist == null || g.EdgesAt(n).Count() < 3) return false;
                double mine = myDist.TryGetValue(n, out double md) ? md : double.MaxValue, other = double.MaxValue;
                foreach (var od in otherDist) if (od.TryGetValue(n, out double o) && o < other) other = o;
                return other + 100 < mine && other <= 500;   // (y que la otra estación llegue a dibujarla: si no, la aguja no salía en ningún esquema)
            }
            while (pq.TryDequeue(out int n0, out double d0))
            {
                var st0 = ns[n0]; if (d0 > st0.d + 1e-6 || st0.d > st0.lim) continue;
                if (foreign.Contains(n0)) continue;
                foreach (int e in g.EdgesAt(n0))
                {
                    var E = g.Edges[e]; int m = E.A == n0 ? E.B : E.A; if (m < 0 || m == n0) continue;
                    var (mx, my) = NodeXY(m); if (NearNode(m, mx, my)) continue;
                    var D = Dense(e); bool fromA = E.A == n0;
                    double bu = st0.u, bv = st0.v, dist = E.Len; int s = st0.s;
                    if (s == 0)
                    {
                        // sale de la parte cercana: desde el último punto cercano, en el sentido en que se aleja
                        int bi = fromA ? D.FindLastIndex(q => NearQ(e, q)) : D.FindIndex(q => NearQ(e, q));
                        if (bi < 0) bi = fromA ? 0 : D.Count - 1;
                        var b = D[bi]; int j = Math.Max(0, Math.Min(D.Count - 1, bi + (fromA ? 2 : -2)));
                        double du = U(D[j].x, D[j].y) - U(b.x, b.y); if (Math.Abs(du) < 1) du = U(mx, my) - U(b.x, b.y);
                        s = Sign(du); bu = U(b.x, b.y); bv = V(b.x, b.y); dist = fromA ? E.Len - b.off : b.off;
                    }
                    else
                    {
                        // nodo lejano: hacia dónde va este tramo y a qué lado, según su papel en la aguja
                        var ro = Roles(n0); int arr = arrived.TryGetValue(n0, out int a0) ? a0 : -1;
                        if (ro is { } R && arr >= 0)
                        {
                            bool arrTrunk = arr == R.t, depTrunk = e == R.t;
                            int sdep = depTrunk || arrTrunk ? st0.s : -st0.s;   // de rama a rama: se vuelve hacia atrás
                            s = sdep;
                            if (e == R.dv) bv = st0.v + R.side * sdep * Spacing;   // la desviada, al lado al que tuerce
                        }
                    }
                    double nd = st0.d + dist, lim = st0.lim;
                    // el andén de otra estación en este tramo: la rama acaba a medio camino
                    foreach (var (off, name) in E.Platforms)
                        if (!Mine(name)) { double dp = st0.d + (fromA ? off : E.Len - off); lim = Math.Min(lim, Math.Max(150, dp / 2)); }
                    if (ns.TryGetValue(m, out var old) && old.d <= nd) continue;
                    if (Foreign(m)) foreign.Add(m);
                    // si se llega a una aguja por su desviada, su vía principal está al otro lado
                    double mv = bv;
                    if (Roles(m) is { } Rm && e == Rm.dv) mv = bv + Rm.side * s * Spacing;
                    ns[m] = (bu + s * dist, mv, nd, s, lim);
                    arrived[m] = e;
                    pq.Enqueue(m, nd);
                }
            }
            // un grupo de agujas de otra estación rodeado del todo por vías nuestras (un escape en medio de una línea que pasa
            // por la estación) sí se dibuja: si no, quedaba un hueco en medio con flechas que no llevaban a ninguna parte
            if (foreign.Count > 0)
            {
                var done = new HashSet<int>();
                foreach (int f0 in foreign.ToList())
                {
                    if (!done.Add(f0)) continue;
                    var comp = new List<int> { f0 }; var q = new Queue<int>(); q.Enqueue(f0); bool enclosed = true;
                    while (q.Count > 0)
                    {
                        int f = q.Dequeue();
                        foreach (int e in g.EdgesAt(f))
                        {
                            var E = g.Edges[e]; int m = E.A == f ? E.B : E.A; if (m < 0) continue;
                            if (foreign.Contains(m)) { if (done.Add(m)) { comp.Add(m); q.Enqueue(m); } continue; }
                            if (!ns.TryGetValue(m, out var sm) || sm.d > sm.lim) enclosed = false;
                        }
                    }
                    if (enclosed) foreach (int f in comp) foreign.Remove(f);
                }
            }
            _nodeState = ns.ToDictionary(kv => kv.Key, kv => (kv.Value.u, kv.Value.v));

            // cada punto de cada tramo: (u, v, distancia, límite); se dibuja lo que está dentro del límite de su rama
            var T = new Dictionary<int, (List<(double u, double v, double off)> P, bool exA, bool exB)>();
            for (int e = 0; e < g.Edges.Length; e++)
            {
                var E = g.Edges[e];
                bool hasA = E.A >= 0 && ns.ContainsKey(E.A), hasB = E.B >= 0 && ns.ContainsKey(E.B);
                var D = Dense(e);
                int firstNear = D.FindIndex(q => NearQ(e, q)), lastNear = D.FindLastIndex(q => NearQ(e, q));
                if (firstNear < 0 && !hasA && !hasB) continue;
                var Q = new List<(double u, double v, double d, double lim, double off)>();
                // (1.2.75, nudos) el andén de otra estación en ESTE tramo: lo que se dibuja de él acaba a medio camino (antes solo
                // se cortaba lo que venía después, y las vías con andén de la estación de al lado entraban enteras)
                var others = E.Platforms.Where(t => !Mine(t.name) && !string.IsNullOrWhiteSpace(t.name)).Select(t => t.off).ToList();
                double PL(double lim, Func<double, double> dpOf)
                {
                    foreach (double po in others) { double dp = dpOf(po); if (dp >= 0) lim = Math.Min(lim, Math.Max(150, dp / 2)); }
                    return lim;
                }
                for (int i = 0; i < D.Count; i++)
                {
                    var q = D[i];
                    if (NearQ(e, q)) { Q.Add((U(q.x, q.y), V(q.x, q.y), 0, lmax, q.off)); continue; }
                    // lejos: entre el punto cercano de ese lado y el extremo de ese lado (o entre los dos extremos)
                    if (firstNear >= 0 && i < firstNear && hasA)
                    {
                        var b = D[firstNear]; var a = ns[E.A]; double f = b.off > 0 ? q.off / b.off : 0;
                        Q.Add((a.u + (U(b.x, b.y) - a.u) * f, a.v + (V(b.x, b.y) - a.v) * f, b.off - q.off, PL(a.lim, po => po < b.off ? b.off - po : -1), q.off));
                    }
                    else if (lastNear >= 0 && i > lastNear && hasB)
                    {
                        var b = D[lastNear]; var bb = ns[E.B]; double f = E.Len - b.off > 0 ? (q.off - b.off) / (E.Len - b.off) : 0;
                        Q.Add((U(b.x, b.y) + (bb.u - U(b.x, b.y)) * f, V(b.x, b.y) + (bb.v - V(b.x, b.y)) * f, q.off - b.off, PL(bb.lim, po => po > b.off ? po - b.off : -1), q.off));
                    }
                    else if (firstNear < 0 && hasA && hasB)
                    {
                        var a = ns[E.A]; var bb = ns[E.B]; double f = E.Len > 0 ? q.off / E.Len : 0;
                        double dA = a.d + q.off, dB = bb.d + (E.Len - q.off);
                        // a lo ancho: el cambio, junto a la aguja de la que es desviada (en sus primeros 40 m)
                        bool divA = Roles(E.A) is { } RA && RA.dv == e, divB = Roles(E.B) is { } RB && RB.dv == e;
                        double ramp = Math.Min(40, E.Len);
                        double fv = divA && !divB ? Math.Min(1, q.off / ramp) : divB && !divA ? Math.Max(0, 1 - (E.Len - q.off) / ramp) : f;
                        Q.Add((a.u + (bb.u - a.u) * f, a.v + (bb.v - a.v) * fv, Math.Min(dA, dB), dA <= dB ? PL(a.lim, po => a.d + po) : PL(bb.lim, po => bb.d + E.Len - po), q.off));
                    }
                    else if (firstNear < 0 && (hasA || hasB))
                    {
                        var a = hasA ? ns[E.A] : ns[E.B]; double dd = hasA ? q.off : E.Len - q.off;
                        Q.Add((a.u + a.s * dd, a.v, a.d + dd, PL(a.lim, po => a.d + (hasA ? po : E.Len - po)), q.off));
                    }
                }
                if (Q.Count < 2) continue;
                bool fA = E.A >= 0 && foreign.Contains(E.A), fB = E.B >= 0 && foreign.Contains(E.B);
                if (fA && fB) continue;   // (entre dos agujas de otra estación: es suyo)
                double cut = Math.Min(25, E.Len * 0.5);   // (un tramo corto entre una aguja nuestra y una de otra estación: queda un trozo con su flecha)
                if (fA || fB)
                    for (int i = 0; i < Q.Count; i++)
                        if (fA && Q[i].off < cut || fB && Q[i].off > E.Len - cut) Q[i] = (Q[i].u, Q[i].v, Q[i].d, -1, Q[i].off);
                _full[e] = Q.Select(q => (q.u, q.v, q.off)).ToList();
                bool Ok((double u, double v, double d, double lim, double off) q) => q.d <= q.lim && Math.Abs(q.v) <= VMax;
                if (!Q.Any(Ok)) continue;
                var C = new List<(double u, double v, double off)>();
                for (int i = 0; i < Q.Count; i++)
                {
                    if (Ok(Q[i])) { C.Add((Q[i].u, Q[i].v, Q[i].off)); continue; }
                    foreach (int j in new[] { i - 1, i + 1 })
                    {
                        if (j < 0 || j >= Q.Count || !Ok(Q[j])) continue;
                        double lo = 0, hi = 1; var A = Q[j]; var B = Q[i];
                        for (int k = 0; k < 30; k++)
                        {
                            double m = (lo + hi) / 2;
                            var t = (A.u + (B.u - A.u) * m, A.v + (B.v - A.v) * m, A.d + (B.d - A.d) * m, A.lim, A.off);
                            if (Ok(t)) lo = m; else hi = m;
                        }
                        C.Add((A.u + (B.u - A.u) * lo, A.v + (B.v - A.v) * lo, A.off + (B.off - A.off) * lo));
                    }
                }
                var Dd = new List<(double u, double v, double off)>();
                foreach (var q in C.OrderBy(q => q.off)) if (Dd.Count == 0 || q.off - Dd[^1].off > 0.3) Dd.Add(q);
                if (Dd.Count >= 2) { T[e] = (Dd, !Ok(Q[0]), !Ok(Q[^1])); _offRange[e] = (Dd[0].off, Dd[^1].off); }
            }
            if (T.Count == 0) return false;
            // (1.2.75, nudos) fuera los trozos sueltos: solo lo que está unido por vía dibujada a la parte de la estación (al
            // cortar a medio camino de otra estación, lo que hay detrás de ella quedaba suelto, como islas, al fondo del esquema)
            if (!_line)
            {
                bool Touches(int e, int n) { var E = g.Edges[e]; var r = _offRange[e]; return n == E.A ? r.a <= 0.5 : n == E.B && r.b >= E.Len - 0.5; }
                var keep = new HashSet<int>(); var q = new Queue<int>();
                foreach (int e in T.Keys) if (Dense(e).Any(p => NearQ(e, p))) { keep.Add(e); q.Enqueue(e); }
                while (q.Count > 0)
                {
                    int e = q.Dequeue(); var E = g.Edges[e];
                    foreach (int n in new[] { E.A, E.B })
                    {
                        if (n < 0 || !Touches(e, n)) continue;
                        foreach (int f in g.EdgesAt(n)) if (T.ContainsKey(f) && !keep.Contains(f) && Touches(f, n)) { keep.Add(f); q.Enqueue(f); }
                    }
                }
                if (keep.Count > 0) foreach (int e in T.Keys.Where(k => !keep.Contains(k)).ToList()) { T.Remove(e); _offRange.Remove(e); }
            }
            if (T.Count == 0) return false;
            _u0 = T.Values.Min(t => t.P.Min(p => p.u)); _u1 = T.Values.Max(t => t.P.Max(p => p.u));
            bool In(double u, double v) => u >= _u0 && u <= _u1 && Math.Abs(v) <= VMax;
            bool NodeIn(int n) => ns.TryGetValue(n, out var q) && q.d <= q.lim && Math.Abs(q.v) <= VMax;

            // travesías: agujas con el mismo identificador, juntas → un solo punto
            var group = new Dictionary<int, int>();          // nodo → nodo que representa a su travesía
            var groupAt = new Dictionary<int, (double u, double v)>();
            var nodes = T.Keys.SelectMany(e => new[] { g.Edges[e].A, g.Edges[e].B }).Where(n => n >= 0).Distinct().ToList();
            var byKey = new Dictionary<(int, int, int), List<int>>();
            foreach (int n in nodes)
                if (g.JunctionOf(n, out int tx, out int tz, out int id, out _)) { var k = (tx, tz, id); if (!byKey.TryGetValue(k, out var l)) byKey[k] = l = new(); l.Add(n); }
            (double u, double v) NodeUV(int n)
            {
                if (ns.TryGetValue(n, out var q)) return (q.u, q.v);
                foreach (int e in g.EdgesAt(n)) { var E = g.Edges[e]; int i = E.A == n ? 0 : E.X.Length - 1; return (U(E.X[i], E.Y[i]), V(E.X[i], E.Y[i])); }
                return (0, 0);
            }
            foreach (var l in byKey.Values.Where(l => l.Count >= 2))
            {
                var uvs = l.Select(NodeUV).ToList();
                double mu = uvs.Average(p => p.u), mv = uvs.Average(p => p.v);
                if (uvs.Any(p => Math.Abs(p.u - mu) > 40 || Math.Abs(p.v - mv) > 15)) continue;
                if (l.Any(n => !NodeIn(n))) continue;   // (entera dentro de la zona; si no, sus agujas sueltas)
                foreach (int n in l) group[n] = l[0];
                groupAt[l[0]] = (mu, mv);
            }
            int Rep(int n) => n >= 0 && group.TryGetValue(n, out int r) ? r : n;
            var internalEdge = new HashSet<int>(T.Keys.Where(e => g.Edges[e].A >= 0 && Rep(g.Edges[e].A) == Rep(g.Edges[e].B) && group.ContainsKey(g.Edges[e].A)));
            Missing.AddRange(internalEdge);

            // agujas: tronco, recta, desviada (por los ángulos con que salen las ramas)
            var straightOf = new Dictionary<int, int>(); var divergeOf = new Dictionary<int, int>(); var trunkOf = new Dictionary<int, int>();
            foreach (int n in nodes)
            {
                if (group.ContainsKey(n) || !g.NodeLegs(n, out var tr, out var br) || tr.Length != 1 || br.Length != 2) continue;
                double At(int e) => g.LegAngle(e, g.Edges[e].A == n);
                double a0 = At(tr[0]);
                int st = StraightLeg(n, tr[0], br[0], br[1]), dv = st == br[0] ? br[1] : br[0];
                trunkOf[n] = tr[0]; straightOf[n] = st; divergeOf[n] = dv;
            }

            // filas: tramos largos y rectos, por su posición lateral
            // recto: en buena parte de su largo (al menos 45 m y la mitad) a menos de 3,5 m de su posición lateral
            // mediana (una vía de estación que al final se curva hacia la cabecera sigue siendo una vía en fila)
            bool Flat(int e)
            {
                if (g.Edges[e].Platforms.Any(pl => Mine(pl.name)) && T[e].P.Max(p => p.u) - T[e].P.Min(p => p.u) > 30) return true;   // (las del andén, siempre)
                if (_line && T[e].P.Max(p => p.u) - T[e].P.Min(p => p.u) > 5) return true;   // (un tramo de línea: todas en fila, enderezadas)
                // en diagonal (sin fila) solo los enlaces entre dos agujas que pasan de una vía a otra (escaleras y escapes):
                // cortos (menos de 200 m a lo largo) y con un cambio lateral de al menos 2,5 m. Todo lo demás (las vías que
                // acaban en topera, las largas, las líneas, aunque sean curvas) va en su fila: si no, se dibujaba encima de otra.
                var P = T[e].P; double ext = P.Max(p => p.u) - P.Min(p => p.u);
                var E = g.Edges[e];
                bool jA = !T[e].exA && E.A >= 0 && !g.IsDeadEnd(E.A), jB = !T[e].exB && E.B >= 0 && !g.IsDeadEnd(E.B);
                double dvEnds = Math.Abs(P[^1].v - P[0].v);
                // (1.2.75, nudos) y que sea la desviada de alguna de sus dos agujas (o llegue a una travesía): un trozo que es vía
                // directa en las dos (tronco o recta) es la vía que sigue, aunque vaya inclinada respecto al eje de la estación (una
                // línea que pasa en ángulo: todos sus trozos parecían cambiar de lado y la escalera de escapes salía sin filas)
                bool divA = E.A >= 0 && (divergeOf.TryGetValue(E.A, out int dA) && dA == e || group.ContainsKey(E.A));
                bool divB = E.B >= 0 && (divergeOf.TryGetValue(E.B, out int dB) && dB == e || group.ContainsKey(E.B));
                bool link = jA && jB && ext < 200 && (dvEnds >= 2.5 || sciss.Diagonals.Contains(e)) && (divA || divB);
                if (link) return false;
                return ext > 8 || !(jA && jB);
            }
            double Med(IEnumerable<double> xs) { var l = xs.OrderBy(x => x).ToList(); return l[l.Count / 2]; }
            // posición lateral típica de un tramo, pesando cada trozo por su largo (en las curvas hay muchos más puntos)
            double WMed(List<(double u, double v, double off)> P)
            {
                var seg = new List<(double v, double w)>();
                for (int i = 1; i < P.Count; i++) seg.Add(((P[i].v + P[i - 1].v) / 2, Math.Abs(P[i].u - P[i - 1].u) + 0.01));
                seg.Sort((a, b) => a.v.CompareTo(b.v));
                double half = seg.Sum(x => x.w) / 2, acc = 0;
                foreach (var (v, w) in seg) { acc += w; if (acc >= half) return v; }
                return seg.Count > 0 ? seg[^1].v : 0;
            }
            (double a, double b) Ur(int e) => (T[e].P.Min(p => p.u), T[e].P.Max(p => p.u));
            bool Overl(int a, int b) { var (a0, a1) = Ur(a); var (b0, b1) = Ur(b); return a0 < b1 - 5 && b0 < a1 - 5; }
            // vías seguidas: el tronco de una aguja y su rama recta son la misma vía (y dos tramos que se unen sin aguja); van en
            // la misma fila aunque la vía se curve (si no, la vía general «saltaba» de fila en una aguja y la desviada se
            // quedaba en la suya). Una vía seguida no se solapa consigo misma a lo largo.
            var flatSet = new HashSet<int>(T.Keys.Where(e => !internalEdge.Contains(e) && Flat(e)));
            var chainOf = flatSet.ToDictionary(e => e, e => new List<int> { e });
            void Join(int a, int b)
            {
                if (!chainOf.TryGetValue(a, out var ca) || !chainOf.TryGetValue(b, out var cb) || ca == cb) return;
                if (ca.Any(x => cb.Any(y => Overl(x, y)))) return;
                ca.AddRange(cb); foreach (int x in cb) chainOf[x] = ca;
            }
            foreach (int n in nodes)
            {
                if (group.ContainsKey(n)) continue;
                if (trunkOf.TryGetValue(n, out int tk) && straightOf.TryGetValue(n, out int sk)) { Join(tk, sk); continue; }
                var at = g.EdgesAt(n).Where(flatSet.Contains).ToList();
                if (at.Count == 2 && g.EdgesAt(n).Count() == 2) Join(at[0], at[1]);
            }
            double CMed(List<int> ids)
            {
                var seg = new List<(double v, double w)>();
                foreach (int e in ids) { var P = T[e].P; for (int i = 1; i < P.Count; i++) seg.Add(((P[i].v + P[i - 1].v) / 2, Math.Abs(P[i].u - P[i - 1].u) + 0.01)); }
                seg.Sort((a, b) => a.v.CompareTo(b.v));
                double half = seg.Sum(x => x.w) / 2, acc = 0;
                foreach (var (v, w) in seg) { acc += w; if (acc >= half) return v; }
                return seg.Count > 0 ? seg[^1].v : 0;
            }
            // El orden de las filas: dos vías que coinciden a lo largo van en el dibujo en el mismo orden que en la realidad en
            // el trozo en que coinciden (no por su posición media: en una estación en curva, la media engaña). Se colocan de
            // la más larga a la más corta; cada una, en una fila que ya hay (si no coincide con ninguna de las de esa fila y
            // está a menos de 2,4 m) o en una nueva, siempre entre las que tiene a un lado y al otro.
            var chains = chainOf.Values.Distinct().ToList();
            var cpts = chains.Select(ch => ch.SelectMany(e => T[e].P.Select(p => (p.u, p.v))).OrderBy(p => p.u).ToList()).ToList();
            var cv = chains.Select(CMed).ToList();
            double? VAt(int c, double u)
            {
                var l = cpts[c]; if (l.Count == 0 || u < l[0].u || u > l[^1].u) return null;
                int k = l.FindIndex(p => p.u >= u); if (k <= 0) return l[Math.Max(0, k)].v;
                var (ua, va) = l[k - 1]; var (ub, vb) = l[k];
                if (ub - ua > 60) return null;   // (hueco entre dos tramos de la misma vía)
                return ub - ua < 1e-6 ? va : va + (vb - va) * (u - ua) / (ub - ua);
            }
            bool COverl(int a, int b) => chains[a].Any(x => chains[b].Any(y => Overl(x, y)));
            var rel = new Dictionary<(int, int), int>();   // −1: a va antes (menos v) que b · +1: después
            // (1.2.75, nudos) el lado de cada aguja manda: la vía a la que lleva la desviada va del lado al que tuerce de verdad
            // (mirado a 25 m de la aguja, respecto a la recta). Con vías a 4,5 m y desvíos de 3-4° (escaleras y escapes en
            // tijera) la posición media no distinguía el lado y la desviada salía dibujada al lado contrario.
            var forced = new Dictionary<(int, int), int>();
            {
                (double x, double y) Along(int e, int n, double d)
                {
                    var E = g.Edges[e]; double off = E.A == n ? Math.Min(d, E.Len) : Math.Max(0, E.Len - d);
                    return g.PointAt(e, off);
                }
                double SchemU(int e, int n, double d)
                {
                    // u del esquema a d metros de la aguja por ese tramo (de su lista de puntos)
                    var P = T[e].P; var E = g.Edges[e]; double off = E.A == n ? Math.Min(d, E.Len) : Math.Max(0, E.Len - d);
                    int k = 0; while (k + 2 < P.Count && P[k + 1].off < off) k++;
                    double span = P[k + 1].off - P[k].off, t = span > 0 ? Math.Max(0, Math.Min(1, (off - P[k].off) / span)) : 0;
                    return P[k].u + (P[k + 1].u - P[k].u) * t;
                }
                int ChainOfEdge(int e) => chainOf.TryGetValue(e, out var c) ? chains.IndexOf(c) : -1;
                foreach (int n in nodes)
                {
                    if (group.ContainsKey(n) || !straightOf.TryGetValue(n, out int st) || !divergeOf.TryGetValue(n, out int dv)) continue;
                    if (!T.ContainsKey(st) || !T.ContainsKey(dv) || T[st].P.Count < 2) continue;
                    int S = ChainOfEdge(st); if (S < 0) continue;
                    int D = ChainOfEdge(dv);
                    if (D < 0)
                    {
                        // la desviada es un enlace: la vía a la que llega (hasta tres enlaces seguidos: en un escape en tijera,
                        // la desviada lleva a otra aguja y de ahí a otro enlace)
                        var seenE = new HashSet<int> { dv, st }; var qn = new Queue<(int node, int depth)>();
                        int m0 = g.Edges[dv].A == n ? g.Edges[dv].B : g.Edges[dv].A; if (m0 >= 0) qn.Enqueue((m0, 0));
                        while (D < 0 && qn.Count > 0)
                        {
                            var (m, dep) = qn.Dequeue();
                            foreach (int f in g.EdgesAt(m))
                            {
                                if (!seenE.Add(f) || !T.ContainsKey(f)) continue;
                                int c = ChainOfEdge(f);
                                if (c >= 0 && c != S) { D = c; break; }
                                if (c < 0 && dep < 2) { int o2 = g.Edges[f].A == m ? g.Edges[f].B : g.Edges[f].A; if (o2 >= 0) qn.Enqueue((o2, dep + 1)); }
                            }
                        }
                    }
                    if (D < 0 || D == S) continue;
                    var n0 = Along(st, n, 0); var ps = Along(st, n, 25); var pd = Along(dv, n, 25);
                    double tx = ps.x - n0.x, ty = ps.y - n0.y, dx = pd.x - n0.x, dy = pd.y - n0.y;
                    double tl = Math.Sqrt(tx * tx + ty * ty); if (tl < 1e-6) continue;
                    double cross = (tx * dy - ty * dx) / tl;
                    if (Math.Abs(cross) < 0.25) continue;   // (no se sabe)
                    double su = SchemU(st, n, 25) - SchemU(st, n, 0); if (Math.Abs(su) < 0.5) continue;
                    bool dvMore = (cross > 0) == (su > 0);   // la desviada, con más v que la recta
                    int r = dvMore ? -1 : 1;                 // rel(S, D)
                    if (forced.TryGetValue((S, D), out int old) && old != r) { forced[(S, D)] = 0; forced[(D, S)] = 0; continue; }   // (contradicción: nada)
                    forced[(S, D)] = r; forced[(D, S)] = -r;
                }
            }
            // las que van después de cada una por los lados de las agujas, encadenadas
            var after = new Dictionary<int, HashSet<int>>();
            {
                var next = new Dictionary<int, List<int>>();
                foreach (var kv in forced) if (kv.Value < 0) { if (!next.TryGetValue(kv.Key.Item1, out var l)) next[kv.Key.Item1] = l = new(); l.Add(kv.Key.Item2); }
                foreach (int a0 in next.Keys)
                {
                    var seen = new HashSet<int>(); var q = new Queue<int>(next[a0]);
                    while (q.Count > 0) { int x = q.Dequeue(); if (!seen.Add(x)) continue; if (next.TryGetValue(x, out var l)) foreach (int y in l) q.Enqueue(y); }
                    after[a0] = seen;
                }
            }
            int Rel(int a, int b)
            {
                if (a == b) return 0;
                if (rel.TryGetValue((a, b), out int r)) return r;
                if (forced.TryGetValue((a, b), out int fr) && fr != 0) { rel[(a, b)] = fr; rel[(b, a)] = -fr; return fr; }
                // (y encadenadas: si a va antes que x y x antes que b, a va antes que b, aunque ninguna aguja los una)
                bool ab = after.TryGetValue(a, out var sa) && sa.Contains(b), ba = after.TryGetValue(b, out var sb) && sb.Contains(a);
                if (ab != ba) { int fr2 = ab ? -1 : 1; rel[(a, b)] = fr2; rel[(b, a)] = -fr2; return fr2; }
                r = 0;
                if (COverl(a, b))
                {
                    double lo = Math.Max(cpts[a][0].u, cpts[b][0].u), hi = Math.Min(cpts[a][^1].u, cpts[b][^1].u);
                    int less = 0, more = 0;
                    for (double u = lo; u <= hi; u += 8)
                    {
                        var va = VAt(a, u); var vb = VAt(b, u);
                        if (va == null || vb == null || Math.Abs(va.Value - vb.Value) < 0.8) continue;
                        if (va < vb) less++; else more++;
                    }
                    if (less + more > 0 && Math.Max(less, more) >= 0.7 * (less + more)) r = less > more ? -1 : 1;
                }
                rel[(a, b)] = r; rel[(b, a)] = -r;
                return r;
            }
            var lvl = new List<(double v, List<int> cs)>();
            foreach (int c in Enumerable.Range(0, chains.Count).OrderByDescending(i => cpts[i].Count == 0 ? 0 : cpts[i][^1].u - cpts[i][0].u))
            {
                int lo = 0, hi = lvl.Count;
                for (int i = 0; i < lvl.Count; i++)
                    foreach (int m in lvl[i].cs)
                    {
                        int r = Rel(m, c);
                        if (r < 0) lo = Math.Max(lo, i + 1); else if (r > 0) hi = Math.Min(hi, i);
                    }
                if (lo > hi)
                {
                    // (1.2.75) contradicción: mandan los lados de las agujas (la posición media no vale en una playa que va en
                    // ángulo con la estación: salía en espejo, con todas sus agujas al revés)
                    int lo2 = 0, hi2 = lvl.Count;
                    for (int i = 0; i < lvl.Count; i++)
                        foreach (int m in lvl[i].cs)
                        {
                            int r = forced.TryGetValue((m, c), out int fr) ? fr : 0;
                            if (r == 0) { bool mc = after.TryGetValue(m, out var am) && am.Contains(c), cm = after.TryGetValue(c, out var ac) && ac.Contains(m); if (mc != cm) r = mc ? -1 : 1; }   // (también encadenadas)
                            if (r < 0) lo2 = Math.Max(lo2, i + 1); else if (r > 0) hi2 = Math.Min(hi2, i);
                        }
                    if (lo2 <= hi2) { lo = lo2; hi = hi2; }
                }
                int join = -1; double bd = 2.4;
                if (lo <= hi)
                    for (int j = lo; j < hi; j++)
                        if (Math.Abs(lvl[j].v - cv[c]) < bd && !lvl[j].cs.Any(m => COverl(m, c))) { bd = Math.Abs(lvl[j].v - cv[c]); join = j; }
                if (join >= 0) { lvl[join].cs.Add(c); lvl[join] = (lvl[join].cs.Average(i => cv[i]), lvl[join].cs); continue; }
                int p = lvl.Count(l => l.v < cv[c]);
                if (lo <= hi) p = Math.Max(lo, Math.Min(hi, p));
                lvl.Insert(p, (cv[c], new List<int> { c }));
            }
            // (1.2.75) vía de paso recta: dos vías seguidas que se continúan por un enlace que no es la desviada de ninguna de
            // sus dos agujas (el camino directo: la línea que entra en la estación) y que no coinciden a lo largo, en la misma
            // fila si se puede (sin solaparse con lo que ya hay en ella ni cambiar el orden con las que coinciden a lo largo).
            // Si no, la línea entraba por arriba o por abajo y bajaba a la estación en una diagonal larga.
            {
                int LevelOf(int c) { for (int i = 0; i < lvl.Count; i++) if (lvl[i].cs.Contains(c)) return i; return -1; }
                int ChainAt(int n, int except)
                {
                    foreach (int f in g.EdgesAt(n)) if (f != except && chainOf.TryGetValue(f, out var ch)) return chains.IndexOf(ch);
                    return -1;
                }
                bool Through(int n, int e) => !(divergeOf.TryGetValue(n, out int dv) && dv == e);
                bool CanMove(int c, int to)
                {
                    if (lvl[to].cs.Any(m => m != c && COverl(m, c))) return false;
                    for (int i = 0; i < lvl.Count; i++)
                        foreach (int m in lvl[i].cs)
                        {
                            if (m == c) continue;
                            int r = Rel(m, c);
                            if (r < 0 && i >= to || r > 0 && i <= to) return false;
                        }
                    return true;
                }
                for (int pass = 0; pass < 3; pass++)
                    foreach (int e in T.Keys)
                    {
                        if (flatSet.Contains(e) || internalEdge.Contains(e)) continue;
                        var E = g.Edges[e]; if (E.A < 0 || E.B < 0 || group.ContainsKey(E.A) || group.ContainsKey(E.B)) continue;
                        if (!Through(E.A, e) || !Through(E.B, e)) continue;
                        int ca = ChainAt(E.A, e), cb = ChainAt(E.B, e);
                        if (ca < 0 || cb < 0 || ca == cb || COverl(ca, cb)) continue;
                        int la = LevelOf(ca), lb = LevelOf(cb); if (la < 0 || lb < 0 || la == lb) continue;
                        // solo se mueve una línea (sin andén de la estación) a la fila de la vía con la que sigue: las vías de la
                        // estación se quedan en su sitio
                        bool HasPlat(int c) => chains[c].Any(x => g.Edges[x].Platforms.Any(pl => Mine(pl.name)));
                        bool pa = HasPlat(ca), pb = HasPlat(cb);
                        if (pa == pb) continue;
                        int mv = pa ? cb : ca, to = pa ? la : lb, from = pa ? lb : la;
                        if (!CanMove(mv, to)) continue;
                        lvl[from].cs.Remove(mv); lvl[to].cs.Add(mv);
                    }
                lvl.RemoveAll(l => l.cs.Count == 0);
            }
            var levels = lvl.Select(l => (v: l.v, ids: l.cs.SelectMany(i => chains[i]).ToList())).ToList();
            if (levels.Count == 0) return false;
            var rowOf = new Dictionary<int, int>();
            for (int r = 0; r < levels.Count; r++) foreach (int e in levels[r].ids) rowOf[e] = r;
            var lv = levels.Select(l => l.v).ToList();
            int VRow(double v)
            {
                int k = lv.BinarySearch(v); if (k < 0) k = ~k;
                int best = Math.Max(0, Math.Min(lv.Count - 1, k));
                if (k - 1 >= 0 && Math.Abs(lv[k - 1] - v) < Math.Abs(lv[best] - v)) best = k - 1;
                return best;
            }
            // fila de cada aguja / travesía / unión
            var nrow = new Dictionary<int, int>();
            foreach (int n0 in nodes)
            {
                int n = Rep(n0); if (nrow.ContainsKey(n)) continue;
                if (groupAt.TryGetValue(n, out var ga))
                {
                    // la de la vía en fila (de las que llegan) más cercana a su centro
                    int best = -1; double bd = 1e9;
                    foreach (int e in T.Keys) if (!internalEdge.Contains(e) && rowOf.TryGetValue(e, out int r) && (Rep(g.Edges[e].A) == n || Rep(g.Edges[e].B) == n))
                        { double d = Math.Abs(lv[r] - ga.v); if (d < bd) { bd = d; best = r; } }
                    nrow[n] = best >= 0 && bd < 6 ? best : VRow(ga.v);
                    continue;
                }
                if (trunkOf.TryGetValue(n, out int t) && rowOf.TryGetValue(t, out int rt)) { nrow[n] = rt; continue; }
                if (straightOf.TryGetValue(n, out int st) && rowOf.TryGetValue(st, out int rs)) { nrow[n] = rs; continue; }
                // aguja de una escalera (ni el tronco ni la recta son vías en fila): en la fila de la vía a la que da paso
                if (divergeOf.TryGetValue(n, out int dvn) && rowOf.TryGetValue(dvn, out int rd)) { nrow[n] = rd; continue; }
                foreach (int e in g.EdgesAt(n)) if (rowOf.TryGetValue(e, out int r)) { nrow[n] = r; break; }
                if (!nrow.ContainsKey(n)) nrow[n] = VRow(NodeUV(n).v);
            }

            // extremos de cada tramo: (u, fila, tipo, nodo)  tipo: j aguja/unión · g travesía · e topera · x salida
            var xrow = new Dictionary<(int, int), int>();
            (double u, int r, char k, int n) End(int e, int side)
            {
                var t = T[e]; var p = side == 0 ? t.P[0] : t.P[^1]; bool ex = side == 0 ? t.exA : t.exB;
                int n = side == 0 ? g.Edges[e].A : g.Edges[e].B;
                if (xrow.TryGetValue((e, side), out int xr)) return (p.u, xr, 'x', n);
                if (!ex && n >= 0)
                {
                    int rn = Rep(n);
                    if (groupAt.TryGetValue(rn, out var ga)) return (ga.u, nrow[rn], 'g', rn);
                    if (g.IsDeadEnd(n)) return (NodeUV(n).u, rowOf.TryGetValue(e, out int rr) ? rr : VRow(p.v), 'e', n);
                    if (nrow.TryGetValue(rn, out int r2)) return (NodeUV(rn).u, r2, 'j', rn);   // (el mismo punto para todas sus vías)
                }
                int r = rowOf.TryGetValue(e, out int r3) ? r3 : -1;
                if (r < 0 && ex)
                {
                    // una línea que sale sigue recta en la fila de su otro extremo
                    int on = side == 0 ? g.Edges[e].B : g.Edges[e].A; bool oex = side == 0 ? t.exB : t.exA;
                    if (!oex && on >= 0 && nrow.TryGetValue(Rep(on), out int r4)) r = r4;
                }
                if (r < 0) r = VRow(p.v);
                return (p.u, r, ex ? 'x' : 'o', n);
            }
            // salidas, cada una en su fila, en su orden
            foreach (bool right in new[] { false, true })
            {
                var ex = new List<(double v, int r, int e, int side)>();
                foreach (int e in T.Keys) for (int side = 0; side < 2; side++)
                    {
                        var en = End(e, side); if (en.k != 'x') continue;
                        var p = side == 0 ? T[e].P[0] : T[e].P[^1];
                        if ((p.u > (_u0 + _u1) / 2) == right) ex.Add((p.v, en.r, e, side));
                    }
                int prev = int.MinValue;
                // (1.2.75) por la fila de su vía (y a igualdad, por su posición): por la posición sola, si no coincidía con el orden
                // de las filas, las salidas se iban empujando hacia abajo y sus vías bajaban en diagonales largas que se cruzaban
                foreach (var (v, r0, e, side) in ex.OrderBy(t => t.r).ThenBy(t => t.v)) { int r = r0 <= prev ? prev + 1 : r0; xrow[(e, side)] = r; prev = r; }
            }
            Rows = Math.Max(levels.Count, xrow.Count > 0 ? xrow.Values.Max() + 1 : 0);

            // a lo largo, por tramos (X): las cosas que hay en el esquema, de izquierda a derecha
            {
                var ev = new List<double> { _u0, _u1 };
                foreach (int n in nodes) if (NodeIn(n)) { int rn = Rep(n); ev.Add(groupAt.TryGetValue(rn, out var gq) ? gq.u : NodeUV(rn).u); }
                foreach (var t in T.Values) { ev.Add(t.P[0].u); ev.Add(t.P[^1].u); }
                foreach (int e in T.Keys) foreach (var m in g.Edges[e].PlatformMarks) if (PathUV(e, m.off, out double pu, out _)) ev.Add(pu);
                if (geo != null) foreach (var sg in geo.Signals) if (sg.Edge >= 0 && T.ContainsKey(sg.Edge) && PathUV(sg.Edge, sg.Off, out double su, out _)) ev.Add(su);
                ev = ev.Where(u => u >= _u0 - 1 && u <= _u1 + 1).OrderBy(u => u).ToList();
                _knots = new List<(double u, double x)> { (ev[0], 0) };
                foreach (double u in ev.Skip(1)) { double du = u - _knots[^1].u; if (du < 3) continue; _knots.Add((u, _knots[^1].x + Gx(du))); }
            }

            // estirar donde una diagonal a 45° no cabe
            var cons = new List<(double lo, double hi, double need)>();
            foreach (int e in T.Keys)
            {
                if (internalEdge.Contains(e)) continue;
                var a = End(e, 0); var b = End(e, 1);
                double need = rowOf.TryGetValue(e, out int r)
                    ? (Math.Abs(a.r - r) + Math.Abs(b.r - r)) * Row + (a.r != r || b.r != r ? Gap : 0)
                    : Math.Abs(a.r - b.r) * Row + Gap;
                if (need > 0) cons.Add((Math.Min(a.u, b.u), Math.Max(a.u, b.u), need));
            }
            foreach (var (lo, hi, need) in cons.OrderBy(c => c.lo))
            {
                double have = X(hi) - X(lo);
                if (have < need) { _breaks.Add((hi - 1e-3, need - have)); _breaks.Sort((p, q) => p.u.CompareTo(q.u)); }
            }

            // ── dibujo
            float Y(int r) => r * Row;
            // primero las vías en fila (lo que ocupan de cada fila) y después los enlaces: su trozo horizontal, por una fila
            // libre; si no hay, en línea recta de aguja a aguja (si no, se dibujaban encima de una vía de la estación)
            var occ = new Dictionary<int, List<(float a, float b)>>();
            void Occupy(PointF[] pts)
            {
                for (int i = 1; i < pts.Length; i++)
                    if (Math.Abs(pts[i].Y - pts[i - 1].Y) < 0.1f && Math.Abs(pts[i].X - pts[i - 1].X) > 0.5f)
                    {
                        int rr = (int)Math.Round(pts[i].Y / Row);
                        if (!occ.TryGetValue(rr, out var l)) occ[rr] = l = new();
                        l.Add((Math.Min(pts[i].X, pts[i - 1].X), Math.Max(pts[i].X, pts[i - 1].X)));
                    }
            }
            bool Free(int rr, float x0, float x1)
            {
                float a0 = Math.Min(x0, x1) + 1, a1 = Math.Max(x0, x1) - 1;
                return a1 <= a0 || !occ.TryGetValue(rr, out var l) || !l.Any(q => q.a < a1 && a0 < q.b);
            }
            // (1.2.75, nudos) lo ya dibujado, para elegir el trazado de cada enlace que corte menos vías
            var segs = new List<(PointF a, PointF b)>();
            void AddSegs(PointF[] pts) { for (int i = 1; i < pts.Length; i++) segs.Add((pts[i - 1], pts[i])); }
            int CutCount(List<PointF> pts)
            {
                var s0 = pts[0]; var s1 = pts[^1]; int n = 0;
                for (int i = 1; i < pts.Count; i++)
                    foreach (var (c, d) in segs)
                        if (Cut(pts[i - 1], pts[i], c, d, out var x) && Dist(x, s0) > 1.5f && Dist(x, s1) > 1.5f) n++;
                return n;
            }
            foreach (int e in T.Keys.OrderBy(k => rowOf.ContainsKey(k) ? 0 : 1))
            {
                if (internalEdge.Contains(e)) continue;
                var a = End(e, 0); var b = End(e, 1);
                float xa = X(a.u), xb = X(b.u); int sgn = xb >= xa ? 1 : -1;
                var tr = new Track { Edge = e };
                if (rowOf.TryGetValue(e, out int r))
                {
                    tr.Flat = true; tr.RowIdx = r;
                    var pts = new List<PointF> { new PointF(xa, Y(a.r)), new PointF(xa + sgn * Math.Abs(a.r - r) * Row, Y(r)), new PointF(xb - sgn * Math.Abs(b.r - r) * Row, Y(r)), new PointF(xb, Y(b.r)) };
                    tr.Pts = Dedup(pts);
                    tr.Map = T[e].P.Select(p => (p.off, p.u)).ToArray();
                    Occupy(tr.Pts); AddSegs(tr.Pts);
                }
                else
                {
                    bool dvA = a.k == 'j' && divergeOf.TryGetValue(a.n, out int da) && da == e;
                    bool dvB = b.k == 'j' && divergeOf.TryGetValue(b.n, out int db) && db == e;
                    float dx = Math.Abs(a.r - b.r) * Row;
                    List<PointF> pts;
                    var direct = new List<PointF> { new PointF(xa, Y(a.r)), new PointF(xb, Y(b.r)) };
                    var diagA = new List<PointF> { new PointF(xa, Y(a.r)), new PointF(xa + sgn * dx, Y(b.r)), new PointF(xb, Y(b.r)) };   // diagonal junto a A, horizontal por la fila de B
                    var diagB = new List<PointF> { new PointF(xa, Y(a.r)), new PointF(xb - sgn * dx, Y(a.r)), new PointF(xb, Y(b.r)) };   // horizontal por la fila de A, diagonal junto a B
                    bool okA = Free(b.r, xa + sgn * dx, xb), okB = Free(a.r, xa, xb - sgn * dx);
                    if (Math.Abs(xb - xa) <= dx + 1 || a.r == b.r) pts = direct;
                    else
                    {
                        // (1.2.75, nudos) de los trazados posibles (diagonal junto a A, junto a B, en medio, o recto), el que no
                        // pisa otra vía en su fila y corta menos vías; a igualdad, la diagonal junto a la aguja de la que es
                        // desviada. Antes se elegía sin mirar lo que cortaba (cruces que no existen en la realidad).
                        float xm = (xa + xb) / 2;
                        var mid = new List<PointF> { new PointF(xa, Y(a.r)), new PointF(xm - sgn * dx / 2, Y(a.r)), new PointF(xm + sgn * dx / 2, Y(b.r)), new PointF(xb, Y(b.r)) };
                        bool okM = Free(a.r, xa, xm - sgn * dx / 2) && Free(b.r, xm + sgn * dx / 2, xb);
                        var cand = new List<(List<PointF> p, double score)>();
                        double prefA = dvA || !dvB ? 0 : 1, prefB = dvA || !dvB ? 1 : 0;
                        if (okA) cand.Add((diagA, prefA));
                        if (okB) cand.Add((diagB, prefB));
                        if (okM) cand.Add((mid, 2));
                        cand.Add((direct, 6));
                        pts = cand.Select(c => (c.p, sc: c.score + 10 * CutCount(c.p))).OrderBy(c => c.sc).First().p;
                    }
                    tr.Pts = Dedup(pts);
                    Occupy(tr.Pts); AddSegs(tr.Pts);
                }
                tr.LenPx = Length(tr.Pts);
                tr.EndNode[0] = a.n; tr.EndNode[1] = b.n; tr.EndKind[0] = a.k; tr.EndKind[1] = b.k;
                tr.V = WMed(T[e].P); tr.UMin = T[e].P.Min(p => p.u); tr.UMax = T[e].P.Max(p => p.u); tr.UA = T[e].P[0].u; tr.UB = T[e].P[^1].u;
                Tracks.Add(tr); _byEdge[e] = tr;
                if (a.k == 'e') Buffers.Add(tr.Pts[0]);
                if (b.k == 'e') Buffers.Add(tr.Pts[^1]);
            }
            foreach (var kv in groupAt) Slips.Add(new PointF(X(kv.Value.u), Y(nrow[kv.Key])));
            SlipNodes.AddRange(group.Keys);

            // agujas (no las de las travesías): su punto y el principio de cada rama, para pintar la puesta
            foreach (int n in nodes)
            {
                if (group.ContainsKey(n) || !divergeOf.ContainsKey(n) || !g.JunctionOf(n, out _, out _, out _, out var outs) || outs.Length != 2) continue;
                if (!NodeIn(n) || foreign.Contains(n)) continue;   // (solo las de dentro de la zona, y no las de otra estación)
                // (1.2.75) y solo si le llegan al menos dos de sus vías (al cortar a medio camino de otra estación, quedaba la
                // aguja sola al final de una vía cortada)
                if (g.EdgesAt(n).Count(f => _byEdge.ContainsKey(f) && _offRange.TryGetValue(f, out var fr) && (g.Edges[f].A == n ? fr.a <= 0.5 : fr.b >= g.Edges[f].Len - 0.5)) < 2) continue;   // (al menos dos)
                if (!nrow.TryGetValue(n, out int rn)) continue;
                var (nu, _) = NodeUV(n);
                var sw = new Switch { Node = n, At = new PointF(X(nu), Y(rn)), Hidden = g.SwitchHidden(n), Order = geo != null && geo.OrderOf.TryGetValue(n, out int o) ? o : -1 };
                for (int k = 0; k < 2; k++)
                    if (outs[k] >= 0 && _byEdge.TryGetValue(outs[k], out var tk)) sw.Legs[k] = Stub(tk, g.Edges[outs[k]].A == n, 12);
                Switches.Add(sw);
            }
            // cruces sin agujas: la X donde se cortan dos vías
            foreach (int e in T.Keys)
                foreach (double off in sciss.Parallels.Contains(e) ? Enumerable.Empty<double>() : g.Edges[e].Diamonds)
                    if (_byEdge.ContainsKey(e) && MapOff(e, off, out var cp) && !Crossings.Any(c => Dist(c, cp) < 6)) Crossings.Add(cp);
            // (tijeras) el cruce, donde se cortan sus dos diagonales en el dibujo
            var dTr = Tracks.Where(t => sciss.Diagonals.Contains(t.Edge)).ToList();
            for (int i = 0; i < dTr.Count; i++)
                for (int j = i + 1; j < dTr.Count; j++)
                {
                    var A = dTr[i].Pts; var B = dTr[j].Pts;
                    for (int a = 1; a < A.Length; a++)
                        for (int b = 1; b < B.Length; b++)
                        {
                            float r1 = A[a].X - A[a - 1].X, r2 = A[a].Y - A[a - 1].Y, s1 = B[b].X - B[b - 1].X, s2 = B[b].Y - B[b - 1].Y, den = r1 * s2 - r2 * s1;
                            if (Math.Abs(den) < 1e-6f) continue;
                            float t = ((B[b - 1].X - A[a - 1].X) * s2 - (B[b - 1].Y - A[a - 1].Y) * s1) / den, u = ((B[b - 1].X - A[a - 1].X) * r2 - (B[b - 1].Y - A[a - 1].Y) * r1) / den;
                            if (t <= 0.02f || t >= 0.98f || u <= 0.02f || u >= 0.98f) continue;
                            var cp = new PointF(A[a - 1].X + r1 * t, A[a - 1].Y + r2 * t);
                            if (!Crossings.Any(c => Dist(c, cp) < 6)) Crossings.Add(cp);
                        }
                }

            // andenes: barra bajo su vía y el número de vía
            var doneP = new HashSet<uint>();
            foreach (var tr in Tracks.Where(t => t.Flat))
            {
                var E = g.Edges[tr.Edge];
                foreach (var m in E.PlatformMarks)
                {
                    if (doneP.Contains(m.id)) continue;
                    var other = E.PlatformMarks.FirstOrDefault(o => o.id == m.linked);
                    if (other.id != m.linked || other.id == 0 && m.linked != 0) continue;
                    doneP.Add(m.id); doneP.Add(m.linked);
                    if (!MapOff(tr.Edge, m.off, out var p0) || !MapOff(tr.Edge, other.off, out var p1)) continue;
                    float x0 = Math.Min(p0.X, p1.X), x1 = Math.Max(p0.X, p1.X);
                    if (x1 - x0 < 4) continue;
                    string raw = E.PlatformNames.FirstOrDefault(pn => pn.id == m.id).raw ?? "";
                    var mm = Regex.Match(raw, @"(\d+)\s*$");
                    string pst = E.Platforms.Where(pl => Mine(pl.name)).OrderBy(pl => Math.Abs(pl.off - (m.off + other.off) / 2)).Select(pl => pl.name).FirstOrDefault();
                    Platforms.Add(new Platform { Bar = new RectangleF(x0, p0.Y, x1 - x0, 3f), Label = pst != null && mm.Success ? mm.Groups[1].Value : null });
                }
            }
            // señales: las del mapa de la sala que caen en una vía del esquema
            if (geo != null)
                foreach (var sg in geo.Signals)
                {
                    // solo en su propia vía (la más cercana de toda la ruta); si esa no está en el esquema, no sale
                    int e; double off;
                    if (sg.Edge >= 0) { e = sg.Edge; off = sg.Off; }
                    else if (!g.NearestOn(Enumerable.Range(0, g.Edges.Length), sg.Lat, sg.Lon, 6, out e, out off)) continue;
                    // (1.2.75) hasta 120 m más allá de lo dibujado de su tramo, en el final (al cortar las vías, la señal de entrada quedaba a
                    // pocos metros fuera de todos los esquemas y el jefe no la podía gobernar)
                    if (!_byEdge.ContainsKey(e) || !_offRange.TryGetValue(e, out var orr)) continue;
                    double offc = Math.Max(orr.a, Math.Min(orr.b, off));
                    if (Math.Abs(offc - off) > 120 || !MapOff(e, offc, out var p)) continue;
                    var tr = _byEdge[e];
                    // hacia dónde mira en el esquema: su sentido sobre la vía (A→B o B→A, por su rumbo) y hacia dónde va A→B
                    // en el esquema en ese punto (con las coordenadas que siguen la vía, no con el eje de la estación)
                    int face = 1;
                    if (!double.IsNaN(sg.Hdg))
                    {
                        double L0 = g.Edges[e].Len;
                        var (p1x, p1y) = g.PointAt(e, Math.Max(0, off - 3)); var (p2x, p2y) = g.PointAt(e, Math.Min(L0, off + 3));
                        bool alongAB = Math.Cos(sg.Hdg) * (p2x - p1x) + Math.Sin(sg.Hdg) * (p2y - p1y) >= 0;
                        if (PathUV(e, Math.Max(0, off - 3), out double ua, out _) && PathUV(e, Math.Min(L0, off + 3), out double ub, out _))
                            face = ((ub - ua >= 0) == alongAB) ? 1 : -1;
                    }
                    // todas (el jefe de circulación las necesita todas); si coinciden con otra, una por encima de la otra
                    int stack = Signals.Count(o => o.Face == face && Math.Abs(o.At.X - p.X) < 8 && Math.Abs(o.At.Y - p.Y) < 2);
                    Signals.Add(new Signal { Sig = sg, At = p, Face = face, RowIdx = tr.RowIdx, Stack = stack });
                }
            // salidas: la estación siguiente por esa vía
            foreach (int e in T.Keys)
                for (int side = 0; side < 2; side++)
                {
                    if (!xrow.ContainsKey((e, side)) || !_byEdge.TryGetValue(e, out var tr)) continue;
                    var p = side == 0 ? tr.Pts[0] : tr.Pts[^1];
                    bool right = (side == 0 ? T[e].P[0].u : T[e].P[^1].u) > (_u0 + _u1) / 2;
                    string nm = NextStation(e, side);
                    nm ??= "";   // (1.2.75: sin estación a la que se llegue, también con su flecha: si no, la vía acababa de golpe y parecía un fallo)
                    if (!Exits.Any(x => x.Station == nm && Math.Abs(x.At.Y - p.Y) < 1 && x.Right == right)) Exits.Add(new Exit { At = p, Right = right, Station = nm, Edge = e, Side = side });
                }

            var all = Tracks.SelectMany(t => t.Pts).ToList();
            Bounds = RectangleF.FromLTRB(all.Min(p => p.X), all.Min(p => p.Y), all.Max(p => p.X), all.Max(p => p.Y));
            return true;
        }

        string NextStation(int e, int side)
        {
            var g = _g; int start = side == 0 ? g.Edges[e].A : g.Edges[e].B;
            // hacia fuera: sin volver a pasar por las vías del esquema
            var q = new Queue<(int node, int d)>(); var seenN = new HashSet<int> { start }; var seenE = new HashSet<int>(_byEdge.Keys) { e };
            q.Enqueue((start, 0));
            while (q.Count > 0)
            {
                var (n, d) = q.Dequeue(); if (n < 0 || d > 400) continue;   // (1.2.75: más lejos; con 60 nodos muchas salidas se quedaban sin nombre)
                foreach (int f in g.EdgesAt(n))
                {
                    if (!seenE.Add(f)) continue;
                    var other = g.Edges[f].Platforms.FirstOrDefault(p => !Mine(p.name) && !string.IsNullOrWhiteSpace(p.name)).name;
                    if (other != null) return other;
                    int m = g.Edges[f].A == n ? g.Edges[f].B : g.Edges[f].A;
                    if (seenN.Add(m)) q.Enqueue((m, d + 1));
                }
            }
            return null;
        }

        // punto del esquema de un punto de un tramo
        public bool MapOff(int e, double off, out PointF p)
        {
            p = default;
            if (!_byEdge.TryGetValue(e, out var tr)) return false;
            if (tr.Flat && tr.Map != null && tr.Map.Length >= 2)
            {
                var m = tr.Map; if (off < m[0].off - 0.5 || off > m[^1].off + 0.5) return false;
                int i = 0; while (i + 2 < m.Length && m[i + 1].off < off) i++;
                double span = m[i + 1].off - m[i].off, t = span > 0 ? Math.Max(0, Math.Min(1, (off - m[i].off) / span)) : 0;
                double u = m[i].u + (m[i + 1].u - m[i].u) * t;
                float x = X(u);
                float lo = Math.Min(tr.Pts[Math.Min(1, tr.Pts.Length - 1)].X, tr.Pts[Math.Max(0, tr.Pts.Length - 2)].X), hi = Math.Max(tr.Pts[Math.Min(1, tr.Pts.Length - 1)].X, tr.Pts[Math.Max(0, tr.Pts.Length - 2)].X);
                if (hi - lo < 1) { lo = Math.Min(tr.Pts[0].X, tr.Pts[^1].X); hi = Math.Max(tr.Pts[0].X, tr.Pts[^1].X); }
                p = new PointF(Math.Max(lo, Math.Min(hi, x)), tr.RowIdx * Row);
                return true;
            }
            var E = _g.Edges[e];
            double f = E.Len > 0 ? Math.Max(0, Math.Min(1, off / E.Len)) : 0;
            p = Along(tr.Pts, f * tr.LenPx);
            return true;
        }

        // punto del esquema de un punto del mapa (lat/lon), si cae en una vía del esquema
        public bool Map(double lat, double lon, out PointF p)
        {
            p = default;
            return _g.NearestOn(_byEdge.Keys, lat, lon, 8, out int e, out double off) && MapOff(e, off, out p);
        }

        static PointF[] Stub(Track t, bool fromStart, float len)
        {
            var pts = fromStart ? t.Pts : t.Pts.Reverse().ToArray();
            var res = new List<PointF> { pts[0] }; float left = len;
            for (int i = 1; i < pts.Length && left > 0; i++)
            {
                float d = Dist(pts[i - 1], pts[i]);
                if (d >= left) { float k = left / d; res.Add(new PointF(pts[i - 1].X + (pts[i].X - pts[i - 1].X) * k, pts[i - 1].Y + (pts[i].Y - pts[i - 1].Y) * k)); break; }
                res.Add(pts[i]); left -= d;
            }
            return res.Count >= 2 ? res.ToArray() : null;
        }
        // ── videográfico (1.2.75): un trozo de un tramo (de o1 a o2 metros) en el dibujo, para pintar cada cantón aparte.
        // Cada tramo, una tabla de su posición (metros) a lo largo de su línea dibujada (las puntas, que suben o bajan de
        // fila, van con el metro de la punta).
        readonly Dictionary<int, (double off, float arc)[]> _arc = new();
        (double off, float arc)[] ArcTable(Track tr)
        {
            if (_arc.TryGetValue(tr.Edge, out var t)) return t;
            var r = _offRange.TryGetValue(tr.Edge, out var rr) ? rr : (a: 0.0, b: _g.Edges[tr.Edge].Len);
            var l = new List<(double, float)> { (r.a, 0f) };
            float best = 0;
            for (double off = r.a + 5; off < r.b - 2; off += 5)
            {
                if (!MapOff(tr.Edge, off, out var q)) continue;
                float arc = Project(tr.Pts, q);
                best = Math.Max(best, arc);   // (siempre hacia delante)
                l.Add((off, best));
            }
            l.Add((r.b, (float)tr.LenPx));
            return _arc[tr.Edge] = l.ToArray();
        }
        static float Project(PointF[] pts, PointF q)
        {
            float acc = 0, bestD = float.MaxValue, bestA = 0;
            for (int i = 1; i < pts.Length; i++)
            {
                var a = pts[i - 1]; var b = pts[i]; float dx = b.X - a.X, dy = b.Y - a.Y, L2 = dx * dx + dy * dy, L = (float)Math.Sqrt(L2);
                float t = L2 > 1e-6f ? Math.Max(0, Math.Min(1, ((q.X - a.X) * dx + (q.Y - a.Y) * dy) / L2)) : 0;
                float px = a.X + dx * t, py = a.Y + dy * t, d = (q.X - px) * (q.X - px) + (q.Y - py) * (q.Y - py);
                if (d < bestD - 1e-4f) { bestD = d; bestA = acc + L * t; }
                acc += L;
            }
            return bestA;
        }
        float ArcOf(Track tr, double off)
        {
            var t = ArcTable(tr);
            if (off <= t[0].off) return t[0].arc;
            for (int i = 1; i < t.Length; i++)
                if (off <= t[i].off) { double k = t[i].off - t[i - 1].off; return t[i - 1].arc + (float)((off - t[i - 1].off) / (k > 0 ? k : 1)) * (t[i].arc - t[i - 1].arc); }
            return t[^1].arc;
        }
        // el trozo dibujado (puntos del esquema) de un tramo entre dos metros; null si el tramo no sale
        public PointF[] Piece(int e, double o1, double o2)
        {
            if (!_byEdge.TryGetValue(e, out var tr) || tr.Pts.Length < 2) return null;
            float a = ArcOf(tr, Math.Min(o1, o2)), b = ArcOf(tr, Math.Max(o1, o2));
            if (b - a < 0.3f) return null;
            var l = new List<PointF> { Along(tr.Pts, a) }; float acc = 0;
            for (int i = 1; i < tr.Pts.Length; i++) { acc += Dist(tr.Pts[i - 1], tr.Pts[i]); if (acc > a && acc < b) l.Add(tr.Pts[i]); }
            l.Add(Along(tr.Pts, b));
            return l.ToArray();
        }
        // lo que se dibuja de un tramo (metros)
        public bool DrawnRange(int e, out double a, out double b)
        {
            a = b = 0;
            if (!_byEdge.ContainsKey(e)) return false;
            if (_offRange.TryGetValue(e, out var r)) { a = r.a; b = r.b; } else { a = 0; b = _g.Edges[e].Len; }
            return true;
        }
        public IEnumerable<int> DrawnEdges => _byEdge.Keys;
        // un punto del mapa en una vía del esquema (tramo y metro)
        public bool Locate(double lat, double lon, out int e, out double off) => _g.NearestOn(_byEdge.Keys, lat, lon, 8, out e, out off);

        static PointF Along(PointF[] pts, double d)
        {
            for (int i = 1; i < pts.Length; i++)
            {
                float l = Dist(pts[i - 1], pts[i]);
                if (d <= l) { float k = l > 0 ? (float)(d / l) : 0; return new PointF(pts[i - 1].X + (pts[i].X - pts[i - 1].X) * k, pts[i - 1].Y + (pts[i].Y - pts[i - 1].Y) * k); }
                d -= l;
            }
            return pts[^1];
        }
        static float Dist(PointF a, PointF b) => (float)Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
        // ¿Se cortan los segmentos ab y cd (en un punto de dentro de los dos)?
        static bool Cut(PointF a, PointF b, PointF c, PointF d, out PointF x)
        {
            x = default;
            float r1 = b.X - a.X, r2 = b.Y - a.Y, s1 = d.X - c.X, s2 = d.Y - c.Y, den = r1 * s2 - r2 * s1;
            if (Math.Abs(den) < 1e-6f) return false;
            float t = ((c.X - a.X) * s2 - (c.Y - a.Y) * s1) / den, u = ((c.X - a.X) * r2 - (c.Y - a.Y) * r1) / den;
            if (t <= 0.001f || t >= 0.999f || u <= 0.001f || u >= 0.999f) return false;
            x = new PointF(a.X + r1 * t, a.Y + r2 * t);
            return true;
        }
        static float Length(PointF[] p) { float l = 0; for (int i = 1; i < p.Length; i++) l += Dist(p[i - 1], p[i]); return l; }
        static PointF[] Dedup(List<PointF> pts)
        {
            var r = new List<PointF>();
            foreach (var p in pts) if (r.Count == 0 || Dist(r[^1], p) > 0.3f) r.Add(p);
            if (r.Count == 1) r.Add(r[0]);
            return r.ToArray();
        }
    }
}
