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
        const double Scale = 0.9, Gap = 8, VMax = 650, VPar = 140;

        public sealed class Track
        {
            public int Edge; public PointF[] Pts; public bool Flat; public int RowIdx = -1;
            // (para la auditoría) el nodo y el tipo de cada extremo (j aguja/unión · g travesía · e topera · x salida),
            // su posición lateral típica y su tramo a lo largo del eje
            public int[] EndNode = new int[2]; public char[] EndKind = new char[2]; public double V, UMin, UMax, UA, UB;   // UA/UB: posición real a lo largo en sus extremos
            internal (double off, double u)[] Map;   // (vía en fila) del punto del tramo a la posición a lo largo
            internal double LenPx;
        }
        public sealed class Switch { public int Node, Order = -1; public PointF At; public PointF[][] Legs = new PointF[2][]; public bool Hidden; public int Straight = -1; public int[] LegEdge = { -1, -1 }; public bool[] LegFromA = new bool[2]; public double[] LegLen = new double[2]; }   // LegEdge…: (1.2.81) el tramo de cada rama, para pintarla con el itinerario   // Straight: (1.2.81) la rama directa (0/1, como THROWNODE)
        public sealed class Signal { public RoomMapGeo.Sig Sig; public PointF At; public int Face; public int RowIdx; public int Stack; }   // Stack: varias en el mismo sitio, una encima de otra
        public sealed class Platform { public RectangleF Bar; public string Label; public bool Above; }   // Above: (1.2.81) la barra encima de la vía
        public sealed class Exit { public PointF At; public bool Right; public string Station; public int Edge = -1, Side; }   // Edge/Side: el tramo y su extremo por donde sale

        public string Station;
        public readonly List<Track> Tracks = new();
        public readonly List<Switch> Switches = new();
        public readonly List<PointF> Slips = new(), Crossings = new();
        public readonly List<(PointF At, PointF Dir, int Edge)> Bridges = new();   // (1.2.81) pasos superiores: un enlace que cruza una vía sin cruce a nivel (Dir: hacia dónde va el de arriba)
        public readonly List<int> SlipNodes = new();   // las agujas de las travesías (no se dibujan sueltas)
        // (1.2.81) cada travesía: su punto y sus agujas (de izquierda a derecha), con su número en la foto de agujas, para
        // cambiarlas desde el esquema como en el mapa grande del HUD
        public sealed class SlipGroup { public PointF At; public int[] Nodes; public int[] Orders; }
        public readonly List<SlipGroup> SlipGroups = new();
        // (1.2.81, petición) cada escape en tijera, como un símbolo: sus cuatro agujas (arriba izquierda, arriba derecha, abajo
        // izquierda, abajo derecha), las dos paralelas (arriba, abajo) y las dos diagonales (la que baja hacia la derecha, la que
        // sube), con las agujas de cada una; sus tramos no se dibujan sueltos (HiddenEdges)
        public sealed class ScissorGroup
        {
            public int[] Nodes = new int[4], Orders = new int[4], Straight = new int[4];
            public int ParTop, ParBot, DiagDown, DiagUp;
            public PointF[] At = new PointF[4];
            public PointF Center => new PointF((At.Min(p => p.X) + At.Max(p => p.X)) / 2, (At.Min(p => p.Y) + At.Max(p => p.Y)) / 2);
        }
        public readonly List<ScissorGroup> ScissorGroups = new();
        public readonly HashSet<int> HiddenEdges = new();
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
        public sealed class Zone { public string Name; public List<(double x, double y)> Anchors; public bool Line; public HashSet<int> Skip; }   // Skip: (1.2.81) tramos que ya dibuja entera una estación
        List<(double x, double y)> _anchors; bool _line; HashSet<int> _skip;

        // (panel por línea) girado o no: flip = 1 como sale, −1 media vuelta
        public static Schematic Build(RouteGraph g, RoomMapGeo geo, Zone z, int flip)
        {
            var s = new Schematic { _g = g, Station = z.Name, _anchors = z.Line ? z.Anchors : null, _line = z.Line, _flip = flip, _skip = z.Line ? z.Skip : null };
            return s.Make(geo) ? s : null;
        }
        public int Flip => (int)_flip;

        public static Schematic Build(RouteGraph g, RoomMapGeo geo, Zone z)
        {
            if (!z.Line) return Build(g, geo, z.Name);
            var s = new Schematic { _g = g, Station = z.Name, _anchors = z.Anchors, _line = true, _skip = z.Skip };
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
            var uncAt = new Dictionary<(double x, double y), (int e, double off)>();   // (1.2.81) de qué vía es cada punto
            for (int e = 0; e < g.Edges.Length; e++)
            {
                var E = g.Edges[e];
                for (double off = 0; off <= E.Len; off += 40)
                {
                    var (x, y) = g.PointAt(e, off);
                    if (!built.Any(b => b.Covers(e, off))) { unc.Add((x, y)); uncAt[(x, y)] = (e, off); }
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
            // (1.2.81) lo que una estación ya dibuja entero no se repite en un tramo de línea (en «Junto a Aranda (2)» salían otra
            // vez, sueltos y desordenados, los finales de las vías muertas de Aranda)
            var whole = new HashSet<int>();
            for (int e = 0; e < g.Edges.Length; e++)
                if ((g.Edges[e].A >= 0 && g.IsDeadEnd(g.Edges[e].A) || g.Edges[e].B >= 0 && g.IsDeadEnd(g.Edges[e].B))   // (solo vías muertas: si no, una aguja del tramo se quedaba sin una de sus vías)
                    && built.Any(b => b.Drawn(e) && b._offRange.TryGetValue(e, out var r) && r.a <= 1 && r.b >= g.Edges[e].Len - 1 && !b.Missing.Contains(e))) whole.Add(e);
            for (int c = 0; c < nc; c++)
            {
                var pts = Enumerable.Range(0, unc.Count).Where(i => comp[i] == c).Select(i => unc[i]).ToList();
                if (pts.Count < 4) continue;   // menos de ~150 m
                // en trozos de hasta 3 km a lo largo de su eje; (1.2.81) y, si un trozo se curva mucho (más de 500 m a un lado de su
                // eje) y por eso se queda sin dibujar parte de su vía, en dos, cada uno con su eje (las señales 4018 y 4039, junto a
                // Castañeda, no salían en ningún esquema)
                List<((double x, double y) p, double u)> ByAxis(List<(double x, double y)> ps, out double spread)
                {
                    double cx = ps.Average(p => p.x), cy = ps.Average(p => p.y), sxx = 0, syy = 0, sxy = 0;
                    foreach (var (x, y) in ps) { sxx += (x - cx) * (x - cx); syy += (y - cy) * (y - cy); sxy += (x - cx) * (y - cy); }
                    double a0 = 0.5 * Math.Atan2(2 * sxy, sxx - syy), ux = Math.Cos(a0), uy = Math.Sin(a0);
                    spread = ps.Max(p => Math.Abs(-(p.x - cx) * uy + (p.y - cy) * ux));
                    return ps.Select(p => (p, u: (p.x - cx) * ux + (p.y - cy) * uy)).OrderBy(t => t.u).ToList();
                }
                var chunks = new List<List<(double x, double y)>>();
                void Add(List<(double x, double y)> ps, int depth)
                {
                    if (ps.Count < 4) return;
                    var bu = ByAxis(ps, out double spread);
                    double umin = bu[0].u, umax = bu[^1].u;
                    int parts = Math.Max(1, (int)Math.Ceiling((umax - umin) / 3000));
                    if (parts == 1 && spread > 500 && ps.Count >= 8 && depth < 4)
                    {
                        // solo si, dibujado entero, deja sin dibujar parte de su vía (si no, se queda como estaba)
                        var trial = new Schematic { _g = g, Station = "?", _anchors = bu.Select(t => t.p).ToList(), _line = true, _skip = whole };
                        int miss = trial.Make(null) ? ps.Count(p => uncAt.TryGetValue(p, out var eo) && !trial.Covers(eo.e, eo.off)) : ps.Count;
                        if (miss > Math.Max(2, ps.Count / 20)) parts = 2;
                    }
                    if (parts == 1) { chunks.Add(bu.Select(t => t.p).ToList()); return; }
                    for (int k = 0; k < parts; k++)
                    {
                        double lo = umin + (umax - umin) * k / parts, hi = umin + (umax - umin) * (k + 1) / parts;
                        Add(bu.Where(t => t.u >= lo - 1 && t.u <= hi + 1).Select(t => t.p).ToList(), depth + 1);
                    }
                }
                Add(pts, 0);
                foreach (var chunk in chunks)
                {
                    string Near((double x, double y) p) => st.OrderBy(t => (t.x - p.x) * (t.x - p.x) + (t.y - p.y) * (t.y - p.y)).First().name;
                    var first = chunk[0]; var last = chunk[^1];
                    string a = Near(first), b = Near(last);
                    string name = a == b ? "Junto a " + a : "Línea " + string.Join(" – ", new[] { a, b }.OrderBy(x => x, StringComparer.CurrentCulture));   // (sin traducir: el nombre es el identificador de la dependencia en la sala, igual para todos)
                    string nm = name; int n = 2; while (!used.Add(nm)) nm = name + " (" + n++ + ")";
                    zones.Add(new Zone { Name = nm, Anchors = chunk, Line = true, Skip = whole });
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
            // (1.2.81, nudos) y, más allá de esa franja (hasta VPar), las vías que van paralelas a la estación (menos de 10°):
            // en el Museo, el haz de vías de la línea pasa paralelo a los andenes a 60 m y, fuera de la franja, se rehacía a
            // ciegas (por los papeles de las agujas) y salía torcido y cruzado
            double vPar = _line ? vNear : Math.Max(vNear, VPar);
            bool ParPt(int e, double off)
            {
                if (vPar <= vNear) return false;
                var E = g.Edges[e]; var (x, y) = g.PointAt(e, off); double u = U(x, y), v = V(x, y);
                if (u < loU || u > hiU || Math.Abs(v) > vPar) return false;
                var (x1, y1) = g.PointAt(e, Math.Max(0, off - 6)); var (x2, y2) = g.PointAt(e, Math.Min(E.Len, off + 6));
                double du = U(x2, y2) - U(x1, y1), dv = V(x2, y2) - V(x1, y1);
                return Math.Abs(du) > 1 && Math.Abs(dv) < Math.Abs(du) * 0.176;   // (tan 10°)
            }
            bool NearQ(int e, (double x, double y, double off) q) => NearPt(q.x, q.y) || ParPt(e, q.off);
            bool NearNode(int n, double x, double y) => NearPt(x, y) || g.EdgesAt(n).Any(e => ParPt(e, g.Edges[e].A == n ? 0 : g.Edges[e].Len));
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
                if (_skip != null && _skip.Contains(e)) continue;
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
                // (1.2.81) un tramo corto (menos de 25 m) que sale de una aguja que sí entra: casi entero, acabado en salida (si no,
                // la aguja salía sin una de sus vías: la 374 en «Línea Lanuza – Parque Grande»)
                if (E.Len < 25 && firstNear < 0 && (hasA && ns[E.A].d <= ns[E.A].lim || hasB && ns[E.B].d <= ns[E.B].lim) && !Q.All(q => q.d <= q.lim))
                {
                    double far = Q.Max(q => q.d);
                    for (int i = 0; i < Q.Count; i++) Q[i] = (Q[i].u, Q[i].v, Q[i].d, Math.Max(Q[i].lim, far - 0.5), Q[i].off);
                }
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
                // (1.2.81) un escape en tijera (sus cuatro agujas en una sola pieza de la ruta) no es una travesía: cuatro agujas y
                // la X de sus diagonales (en Aranda, Estación Sur e Ingeniero Pizias salían como un rombo y no se podían mover)
                if (l.All(n => sciss.Straight.ContainsKey(n))) continue;
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
            // (1.2.81) ¿cruza este tramo, en la realidad, otro de los dibujados (sin aguja en común)?
            bool CrossesReal(int e)
            {
                var E = g.Edges[e];
                foreach (int f in T.Keys)
                {
                    if (f == e) continue;
                    var F = g.Edges[f];
                    if (F.A >= 0 && (F.A == E.A || F.A == E.B) || F.B >= 0 && (F.B == E.A || F.B == E.B)) continue;
                    for (int i = 1; i < E.X.Length; i++)
                        for (int k = 1; k < F.X.Length; k++)
                            if (Cut(new PointF(E.X[i - 1], E.Y[i - 1]), new PointF(E.X[i], E.Y[i]), new PointF(F.X[k - 1], F.Y[k - 1]), new PointF(F.X[k], F.Y[k]), out _)) return true;
                }
                return false;
            }
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
                // (1.2.81, nudos) un tramo entre dos agujas que cambia mucho de lado (más de 10 m, con una pendiente de más de 1 a 4
                // respecto al eje) y que en la realidad cruza otra vía es un paso de un lado al otro de la estación, aunque sea la
                // vía directa de sus agujas: en fila, la línea de Lanuza en el Museo arrastraba al fondo la vía a la que sube y
                // todo se cruzaba
                if (jA && jB && ext < 250 && dvEnds >= 10 && dvEnds >= 0.25 * ext && !E.Platforms.Any(pl => Mine(pl.name))
                    && !sciss.Diagonals.Contains(e) && !sciss.Parallels.Contains(e) && CrossesReal(e)) return false;
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
                if (trunkOf.TryGetValue(n, out int tk) && straightOf.TryGetValue(n, out int sk))
                {
                    // (1.2.81) en una aguja de tijera, la paralela sigue en la fila del tronco que lleva a un andén de la estación (por
                    // su otra punta), no en la del que se va a otro sitio: en Ingeniero Pizias la vía del andén 2 se iba con el tramo
                    // de la playa y dejaba la playa entre las dos vías de los andenes
                    if (sciss.Straight.ContainsKey(n) && g.Edges[sk].A >= 0 && g.Edges[sk].B >= 0)
                    {
                        int m = g.Edges[sk].A == n ? g.Edges[sk].B : g.Edges[sk].A;
                        bool Plat(int e) => g.Edges[e].Platforms.Any(pl => Mine(pl.name));
                        if (sciss.Straight.ContainsKey(m) && trunkOf.TryGetValue(m, out int tm) && Plat(tm) && !Plat(tk)) continue;
                    }
                    Join(tk, sk); continue;
                }
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
            // (1.2.81, nudos) solo los puntos con su posición real (dentro de la franja): fuera, la v es la enderezada (la de
            // donde salió la vía) y en un nudo no dice nada de qué vía va a qué lado
            bool Real(int e, double off) { var (x, y) = g.PointAt(e, off); return NearQ(e, (x, y, off)); }
            var cptsR = chains.Select(ch => ch.SelectMany(e => T[e].P.Where(p => Real(e, p.off)).Select(p => (p.u, p.v))).OrderBy(p => p.u).ToList()).ToList();
            // (1.2.81, nudos) una vía «débil»: casi sin posición real y que en la realidad va muy oblicua al eje de la estación
            // (más de 35°: una línea que cruza la estación en ángulo); su lado enderezado no dice nada
            var weakC = new Dictionary<int, bool>();
            bool Weak(int c)
            {
                if (weakC.TryGetValue(c, out bool w)) return w;
                w = false;
                if (cpts[c].Count > 0 && cptsR[c].Count < 0.2 * cpts[c].Count)
                {
                    double du = 0, dv = 0;
                    foreach (int e in chains[c]) { var E = g.Edges[e]; du += Math.Abs(U(E.X[^1], E.Y[^1]) - U(E.X[0], E.Y[0])); dv += Math.Abs(V(E.X[^1], E.Y[^1]) - V(E.X[0], E.Y[0])); }
                    w = dv > 0.7 * du;
                }
                weakC[c] = w; return w;
            }
            double? VAtR(int c, double u)
            {
                var l = cptsR[c]; if (l.Count == 0 || u < l[0].u || u > l[^1].u) return null;
                int k = l.FindIndex(p => p.u >= u); if (k <= 0) return l[Math.Max(0, k)].v;
                var (ua, va) = l[k - 1]; var (ub, vb) = l[k];
                if (ub - ua > 30) return null;
                return ub - ua < 1e-6 ? va : va + (vb - va) * (u - ua) / (ub - ua);
            }
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
                    int S = ChainOfEdge(st);
                    // (1.2.81) la recta es un enlace corto (no va en fila): manda la vía del tronco, que sigue en la misma dirección
                    // (si no, la desviada no tenía lado y salía al contrario: Aranda 829, Ingeniero Pizias 1370)
                    if (S < 0 && trunkOf.TryGetValue(n, out int tkn)) S = ChainOfEdge(tkn);
                    if (S < 0) continue;
                    int D = ChainOfEdge(dv);
                    if (D < 0)
                    {
                        // la desviada es un enlace: la vía a la que llega (hasta tres enlaces seguidos: en un escape en tijera,
                        // la desviada lleva a otra aguja y de ahí a otro enlace)
                        var seenE = new HashSet<int> { dv, st }; var qn = new Queue<(int node, int depth, int via)>();
                        int m0 = g.Edges[dv].A == n ? g.Edges[dv].B : g.Edges[dv].A; if (m0 >= 0) qn.Enqueue((m0, 0, dv));
                        while (D < 0 && qn.Count > 0)
                        {
                            var (m, dep, via) = qn.Dequeue();
                            // (1.2.81) primero la vía que sigue al enlace en esa aguja (si llega por el tronco, la recta; si por una
                            // rama, el tronco): cogiendo la primera que salía, en Ingeniero Pizias la 1370 tomaba la desviada de la 154
                            int cont = trunkOf.TryGetValue(m, out int tm) ? (via == tm ? (straightOf.TryGetValue(m, out int sm) ? sm : -1) : tm) : -1;
                            if (cont >= 0 && !seenE.Contains(cont) && T.ContainsKey(cont)) { int cc = ChainOfEdge(cont); if (cc >= 0 && cc != S) { D = cc; break; } }
                            foreach (int f in g.EdgesAt(m))
                            {
                                if (!seenE.Add(f) || !T.ContainsKey(f)) continue;
                                int c = ChainOfEdge(f);
                                if (c >= 0 && c != S) { D = c; break; }
                                if (c < 0 && dep < 2) { int o2 = g.Edges[f].A == m ? g.Edges[f].B : g.Edges[f].A; if (o2 >= 0) qn.Enqueue((o2, dep + 1, f)); }
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
                    // (1.2.81) si donde coinciden a lo largo las dos vías están claramente (más de 5 m) al revés de lo que dice el
                    // giro de la aguja (el enlace sale hacia un lado y la vía a la que llega queda al otro: una S, o una vía que
                    // tuerce), no se fuerza nada: mandaba el giro y la vía salía en el lado contrario (Ingeniero Pizias, Santa Ana)
                    const double X3T = 5;   // (m; probado 3, 5, 8 y 12 con la auditoría: 5 es lo que menos fallos deja)
                    if (COverl(S, D))
                    {
                        double lo3 = Math.Max(cpts[S][0].u, cpts[D][0].u), hi3 = Math.Min(cpts[S][^1].u, cpts[D][^1].u);
                        int more3 = 0, less3 = 0;
                        for (double u3 = lo3; u3 <= hi3; u3 += 8)
                        {
                            var vs3 = VAtR(S, u3); var vd3 = VAtR(D, u3);
                            if (vs3 == null || vd3 == null || Math.Abs(vd3.Value - vs3.Value) < X3T) continue;
                            if (vd3 > vs3) more3++; else less3++;
                        }
                        if (more3 + less3 >= 3 && Math.Max(more3, less3) >= 0.8 * (more3 + less3) && (more3 > less3) != dvMore) { if (Environment.GetEnvironmentVariable("SELECTOR_ESQ_DEBUG") is string dq && dq.Length > 0 && Station.Contains(dq)) Console.WriteLine($"  lado sin forzar (X3T): aguja {n} cadena {S}/{D} más {more3} menos {less3} dvMore {dvMore}"); continue; }
                    }
                    int r = dvMore ? -1 : 1;                 // rel(S, D)
                    if (forced.TryGetValue((S, D), out int old) && old != r) { forced[(S, D)] = 0; forced[(D, S)] = 0; continue; }   // (contradicción: nada)
                    forced[(S, D)] = r; forced[(D, S)] = -r;
                    if (Environment.GetEnvironmentVariable("SELECTOR_ESQ_DEBUG") is string dbg0 && dbg0.Length > 0 && Station.Contains(dbg0)) Console.WriteLine($"  lado: aguja {n} recta {st} (cadena {S}) desviada {dv} (cadena {D}) cruz {cross:F2} su {su:F1} → rel {r}");
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
            // −1: la cadena a va a la derecha de b (menos v), +1 a la izquierda, 0 no se sabe; con la geometría real: desde cada
            // punto de a (cada 10 m), la vía b más cercana (a menos de 25 m), a qué lado queda yendo hacia donde crece u
            // ¿salen de agujas cercanas (a menos de 120 m por la vía)? (si no, pueden cruzarse lejos, por un paso superior)
            bool Kin(int a, int b)
            {
                var na = new HashSet<int>(chains[a].SelectMany(e => new[] { g.Edges[e].A, g.Edges[e].B }).Where(n => n >= 0));
                var nb = new HashSet<int>(chains[b].SelectMany(e => new[] { g.Edges[e].A, g.Edges[e].B }).Where(n => n >= 0));
                var dist = new Dictionary<int, double>(); var pq2 = new PriorityQueue<int, double>();
                foreach (int n in na) { dist[n] = 0; pq2.Enqueue(n, 0); }
                while (pq2.TryDequeue(out int n, out double d))
                {
                    if (d > dist[n] + 1e-6) continue;
                    if (nb.Contains(n)) return true;
                    foreach (int e in g.EdgesAt(n))
                    {
                        if (chains[a].Contains(e) || chains[b].Contains(e)) continue;
                        var E = g.Edges[e]; int m = E.A == n ? E.B : E.A; if (m < 0) continue;
                        double nd = d + E.Len; if (nd > 120) continue;
                        if (!dist.TryGetValue(m, out double om) || nd < om) { dist[m] = nd; pq2.Enqueue(m, nd); }
                    }
                }
                return false;
            }
            int LocalSide(int a, int b, double lo, double hi)
            {
                if (!Kin(a, b)) return 0;
                int left = 0, right = 0;
                var bp = new List<(double x, double y)>();
                foreach (int e in chains[b]) { var E = g.Edges[e]; for (double o = 0; o <= E.Len; o += 4) bp.Add(g.PointAt(e, o)); }
                foreach (int e in chains[a])
                {
                    var E = g.Edges[e];
                    for (double o = 3; o <= E.Len - 3; o += 10)
                    {
                        if (!PathUV(e, o - 3, out double u1, out _) || !PathUV(e, o + 3, out double u2, out _) || Math.Abs(u2 - u1) < 1) continue;
                        if ((u1 + u2) / 2 < lo || (u1 + u2) / 2 > hi) continue;
                        { var (rx, ry) = g.PointAt(e, o); double ru = U(rx, ry); if (ru < loU - 120 || ru > hiU + 120) continue; }   // (cerca de la estación)
                        var (x1, y1) = g.PointAt(e, o - 3); var (x2, y2) = g.PointAt(e, o + 3); var (px, py) = g.PointAt(e, o);
                        double dx = x2 - x1, dy = y2 - y1; if (u2 < u1) { dx = -dx; dy = -dy; }
                        double bd = 25 * 25; (double x, double y) q = default; bool f = false;
                        foreach (var c in bp) { double d = (c.x - px) * (c.x - px) + (c.y - py) * (c.y - py); if (d < bd) { bd = d; q = c; f = true; } }
                        if (!f || bd < 1) continue;
                        double dl0 = Math.Sqrt(dx * dx + dy * dy), dot = (dx * (q.x - px) + dy * (q.y - py)) / Math.Max(1e-6, dl0);
                        if (Math.Abs(dot) > 0.5 * Math.Sqrt(bd)) continue;   // (enfrente, no por delante o por detrás)
                        double cr = dx * (q.y - py) - dy * (q.x - px);   // > 0: b a la izquierda (más v)
                        if (cr > 0) left++; else right++;   // (con media vuelta, u y v cambian a la vez: el signo ya sale bien)
                    }
                }
                if (Environment.GetEnvironmentVariable("SELECTOR_ESQ_DEBUG") is string dl && dl.Length > 0 && Station.Contains(dl)) Console.WriteLine($"  lado local {a} [{string.Join(",", chains[a])}] / {b} [{string.Join(",", chains[b])}]: izq {left} dcha {right}");
                if (left + right < 1 || Math.Max(left, right) < 0.75 * (left + right)) return 0;   // (salen de agujas cercanas: basta un punto; una que se separa deprisa solo tiene uno enfrente)
                return left > right ? -1 : 1;
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
                    // (1.2.81, nudos) si una de las dos vías apenas tiene posición real (una línea que cruza la estación en ángulo),
                    // solo con las posiciones reales: su lado enderezado no dice nada
                    // (1.2.81) primero con las posiciones reales (dentro de la franja); si no hay, el lado de verdad mirado de cerca,
                    // punto a punto (fuera de la franja el lado enderezado puede engañar: en Aranda la 469 sale en curva por fuera de
                    // todo y se metía entre la 470 y la 472); y si tampoco, el enderezado (no en las vías «débiles»)
                    bool weak = Weak(a) || Weak(b);
                    int less = 0, more = 0;
                    for (double u = lo; u <= hi; u += 8)
                    {
                        var va = VAtR(a, u); var vb = VAtR(b, u);
                        if (va == null || vb == null || Math.Abs(va.Value - vb.Value) < 0.8) continue;
                        if (va < vb) less++; else more++;
                    }
                    if (less + more < 3)
                    {
                        int ls = LocalSide(a, b, lo, hi);
                        if (Environment.GetEnvironmentVariable("SELECTOR_ESQ_DEBUG") is string dl2 && dl2.Length > 0 && Station.Contains(dl2))
                        {
                            int l2 = 0, m2 = 0; for (double u = lo; u <= hi; u += 8) { var va = VAt(a, u); var vb = VAt(b, u); if (va == null || vb == null || Math.Abs(va.Value - vb.Value) < 0.8) continue; if (va < vb) l2++; else m2++; }
                            Console.WriteLine($"  REL {a}[{string.Join(",", chains[a])}] / {b}[{string.Join(",", chains[b])}]: local {ls} · enderezado {l2}/{m2}");
                        }
                        if (ls != 0) { less = ls < 0 ? 1 : 0; more = ls > 0 ? 1 : 0; }
                        else if (!weak)
                        {
                            less = more = 0;
                            for (double u = lo; u <= hi; u += 8)
                            {
                                var va = VAt(a, u); var vb = VAt(b, u);
                                if (va == null || vb == null || Math.Abs(va.Value - vb.Value) < 0.8) continue;
                                if (va < vb) less++; else more++;
                            }
                        }
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
                // (1.2.81, nudos) una vía casi sin posición real, junto a la vía de la que sale (la de su aguja), en vez de por su
                // lado enderezado (en Parque Grande, la salida 771 acababa entre los andenes y cortaba todo)
                if (Weak(c))
                {
                    int bestP = -1;
                    for (int i = 0; i < lvl.Count && bestP < 0; i++)
                        foreach (int m in lvl[i].cs)
                            if (forced.TryGetValue((c, m), out int fr) && fr != 0) { bestP = fr < 0 ? i : i + 1; break; }
                    if (bestP >= 0) p = bestP;
                }
                if (lo <= hi) p = Math.Max(lo, Math.Min(hi, p));
                lvl.Insert(p, (cv[c], new List<int> { c }));
            }
            // (1.2.81) reparación: una vía que incumple el orden con otra (colocada antes de saber lo de las siguientes: en Aranda
            // la 469 iba junto a la 470 y luego resultó ir detrás de la 473), a una fila nueva donde cumpla con todas, si la hay
            for (int pass = 0; pass < 3; pass++)
            {
                bool moved = false;
                foreach (int c in Enumerable.Range(0, chains.Count))
                {
                    int cur = lvl.FindIndex(l => l.cs.Contains(c)); if (cur < 0) continue;
                    bool bad = false; int lo = 0, hi = lvl.Count - 1;
                    for (int i = 0; i < lvl.Count; i++)
                        foreach (int m in lvl[i].cs)
                        {
                            if (m == c) continue;
                            int r = Rel(m, c);
                            if (r < 0) { if (i >= cur) bad = true; lo = Math.Max(lo, i + 1); }
                            else if (r > 0) { if (i <= cur) bad = true; hi = Math.Min(hi, i); }
                        }
                    if (!bad || lo > hi + 1) continue;
                    // a una fila nueva en la posición lo (entre las de antes y las de después), quitándola de la suya
                    lvl[cur].cs.Remove(c);
                    int at = lo;
                    if (lvl[cur].cs.Count == 0) { lvl.RemoveAt(cur); if (cur < at) at--; }
                    lvl.Insert(Math.Max(0, Math.Min(lvl.Count, at)), (cv[c], new List<int> { c }));
                    moved = true;
                    if (Environment.GetEnvironmentVariable("SELECTOR_ESQ_DEBUG") is string dr && dr.Length > 0 && Station.Contains(dr)) Console.WriteLine($"  reparada cadena {c} [{string.Join(",", chains[c])}] → nivel {at}");
                }
                if (!moved) break;
            }
            // (1.2.81, petición) las dos vías de un escape en tijera, en filas vecinas (en la realidad van juntas): si había otras
            // filas en medio, la de una pasa a una fila nueva junto a la otra, siempre que no cambie el orden con las vías que
            // coinciden con ella a lo largo. Si no, el símbolo quedaba lejos de las dos y las vías llegaban con muchas vueltas
            {
                int LevelOfC(int c) { for (int i = 0; i < lvl.Count; i++) if (lvl[i].cs.Contains(c)) return i; return -1; }
                bool Fits(int c, int at)   // ¿cumple con todas en una fila nueva en la posición «at»? (las de antes, < at)
                {
                    for (int i = 0; i < lvl.Count; i++)
                        foreach (int m in lvl[i].cs)
                        {
                            if (m == c) continue;
                            int r = Rel(m, c);
                            if (r < 0 && i >= at || r > 0 && i < at) return false;
                        }
                    return true;
                }
                var pairs = new List<(int a, int b)>();
                foreach (int d in sciss.Diagonals)
                {
                    var E = g.Edges[d]; if (E.A < 0 || E.B < 0) continue;
                    if (!sciss.Straight.TryGetValue(E.A, out int pa) || !sciss.Straight.TryGetValue(E.B, out int pb)) continue;
                    if (!chainOf.TryGetValue(pa, out var ca) || !chainOf.TryGetValue(pb, out var cb) || ca == cb) continue;
                    pairs.Add((chains.IndexOf(ca), chains.IndexOf(cb)));
                }
                foreach (var (a, b) in pairs.Distinct())
                {
                    int la = LevelOfC(a), lb = LevelOfC(b);
                    if (Environment.GetEnvironmentVariable("SELECTOR_ESQ_DEBUG") is string dt && dt.Length > 0 && Station.Contains(dt)) Console.WriteLine($"  tijera: cadenas {a} [{string.Join(",", chains[a])}] (nivel {la}) y {b} [{string.Join(",", chains[b])}] (nivel {lb})");
                    if (la < 0 || lb < 0 || Math.Abs(la - lb) <= 1) continue;
                    // probar a llevar cada una junto a la otra (la de arriba debajo de la de abajo, o al revés)
                    foreach (var (mv, to) in new[] { (b, la), (a, lb) })
                    {
                        int from = LevelOfC(mv), other = LevelOfC(to == la ? a : b);
                        int at = from > other ? other + 1 : other;   // (justo después de la otra si iba detrás; justo antes si iba delante)
                        lvl[from].cs.Remove(mv); bool emptied = lvl[from].cs.Count == 0;
                        var saved = lvl[from];
                        if (emptied) { lvl.RemoveAt(from); if (from < at) at--; }
                        if (Fits(mv, at)) { lvl.Insert(at, (cv[mv], new List<int> { mv })); break; }
                        if (Environment.GetEnvironmentVariable("SELECTOR_ESQ_DEBUG") is string dt2 && dt2.Length > 0 && Station.Contains(dt2))
                            for (int i = 0; i < lvl.Count; i++) foreach (int m in lvl[i].cs) { int r = Rel(m, mv); if (r < 0 && i >= at || r > 0 && i < at) Console.WriteLine($"    no cabe {mv} en {at}: choca con {m} [{string.Join(",", chains[m])}] nivel {i} rel {r}"); }
                        // no cabe: como estaba
                        if (emptied) lvl.Insert(from, saved); saved.cs.Add(mv);
                    }
                }
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
            // (depuración: SELECTOR_ESQ_DEBUG=<dependencia>) papeles de las agujas, cadenas y filas
            if (Environment.GetEnvironmentVariable("SELECTOR_ESQ_DEBUG") is string dbg && dbg.Length > 0 && Station.Contains(dbg))
            {
                foreach (int n in nodes.Where(trunkOf.ContainsKey))
                    Console.WriteLine($"  aguja {n}: tronco {trunkOf[n]} (fila {(rowOf.TryGetValue(trunkOf[n], out int r1) ? r1 : -1)}) recta {straightOf[n]} (fila {(rowOf.TryGetValue(straightOf[n], out int r2) ? r2 : -1)}) desviada {divergeOf[n]} (fila {(rowOf.TryGetValue(divergeOf[n], out int r3) ? r3 : -1)})");
                foreach (var ch in chains) Console.WriteLine($"  cadena {chains.IndexOf(ch)} [{string.Join(",", ch)}] v {CMed(ch):F1} fila {(rowOf.TryGetValue(ch[0], out int rr) ? rr : -1)}");
                foreach (var kv in forced.Where(k => k.Value != 0 && k.Key.Item1 < k.Key.Item2)) Console.WriteLine($"  forzado: cadena {kv.Key.Item1} {(kv.Value < 0 ? "antes que" : "después de")} cadena {kv.Key.Item2}");
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
                    // (1.2.81) una vía en fila que pasa a través de la travesía (le llegan dos tramos de la misma fila): en esa fila
                    // (en Ingeniero Pizias la travesía de la línea general quedaba 5 filas más abajo y la línea bajaba y subía)
                    var thru = T.Keys.Where(e => !internalEdge.Contains(e) && rowOf.ContainsKey(e) && (Rep(g.Edges[e].A) == n || Rep(g.Edges[e].B) == n))
                        .GroupBy(e => rowOf[e]).Where(q => q.Count() >= 2).Select(q => q.Key).ToList();
                    if (thru.Count == 1) { nrow[n] = thru[0]; continue; }
                    nrow[n] = best >= 0 && bd < 6 ? best : VRow(ga.v);
                    continue;
                }
                // (1.2.81) una aguja de tijera, en la fila de su paralela (su tronco puede irse lejos: Ingeniero Pizias 1427)
                if (sciss.Straight.TryGetValue(n, out int spar) && rowOf.TryGetValue(spar, out int rsp)) { nrow[n] = rsp; continue; }
                if (trunkOf.TryGetValue(n, out int t) && rowOf.TryGetValue(t, out int rt)) { nrow[n] = rt; continue; }
                if (straightOf.TryGetValue(n, out int st) && rowOf.TryGetValue(st, out int rs)) { nrow[n] = rs; continue; }
                // aguja de una escalera (ni el tronco ni la recta son vías en fila): en la fila de la vía a la que da paso
                if (divergeOf.TryGetValue(n, out int dvn) && rowOf.TryGetValue(dvn, out int rd)) { nrow[n] = rd; continue; }
                foreach (int e in g.EdgesAt(n)) if (rowOf.TryGetValue(e, out int r)) { nrow[n] = r; break; }
                if (!nrow.ContainsKey(n)) nrow[n] = VRow(NodeUV(n).v);
            }

            // (1.2.81, petición) una tijera con sus dos vías en filas lejanas: las agujas de una de sus vías, en la fila vecina
            // de la otra (esa vía da un quiebro a 45° antes y después de la tijera), si las filas de en medio están libres a esa
            // altura. Así la X queda pequeña y entre sus dos vías, en vez de un recuadro alto
            {
                var tdone = new HashSet<int>();
                int Oth(int e, int n) => g.Edges[e].A == n ? g.Edges[e].B : g.Edges[e].A;
                foreach (int n0 in sciss.Straight.Keys)
                {
                    if (tdone.Contains(n0) || !nrow.ContainsKey(n0)) continue;
                    int n1 = Oth(sciss.Straight[n0], n0); if (n1 < 0 || !sciss.Straight.ContainsKey(n1)) continue;
                    // la otra pareja: las del otro lado de las diagonales de n0 y n1
                    int m0 = g.EdgesAt(n0).Where(sciss.Diagonals.Contains).Select(e => Oth(e, n0)).FirstOrDefault(-1);
                    if (m0 < 0 || !sciss.Straight.ContainsKey(m0)) continue;
                    int m1 = Oth(sciss.Straight[m0], m0); if (m1 < 0) continue;
                    var four = new[] { n0, n1, m0, m1 }; foreach (int q in four) tdone.Add(q);
                    if (four.Any(q => !nrow.ContainsKey(q))) continue;
                    // (1.2.81) las dos agujas de cada lado, a la misma altura a lo largo (en la realidad están casi enfrentadas, a 0-10
                    // m; el esquema lo exageraba hasta más de 100 px y la tijera salía torcida, con una aguja dentro de su símbolo)
                    if (ns.ContainsKey(n0) && ns.ContainsKey(n1) && ns.ContainsKey(m0) && ns.ContainsKey(m1))
                    {
                        int tL = ns[n0].u <= ns[n1].u ? n0 : n1, tR = tL == n0 ? n1 : n0, bL = ns[m0].u <= ns[m1].u ? m0 : m1, bR = bL == m0 ? m1 : m0;
                        double uL = Math.Min(ns[tL].u, ns[bL].u), uR = Math.Max(ns[tR].u, ns[bR].u);
                        if (uR - uL > 2)
                        {
                            void SetU(int q, double u) { var o = ns[q]; ns[q] = (u, o.v, o.d, o.s, o.lim); }
                            SetU(tL, uL); SetU(bL, uL); SetU(tR, uR); SetU(bR, uR);
                        }
                    }
                    int ra = nrow[n0], rb = nrow[m0];
                    if (nrow[n1] != ra || nrow[m1] != rb || Math.Abs(ra - rb) <= 1) continue;
                    // lo que ocupa a lo largo, con sitio para los quiebros
                    double ua = four.Min(q => NodeUV(q).u), ub = four.Max(q => NodeUV(q).u), pad = 12 + 3 * Math.Abs(ra - rb);
                    bool RowFree(int r) => !rowOf.Any(kv => kv.Value == r && T.ContainsKey(kv.Key) && !sciss.Parallels.Contains(kv.Key) && !sciss.Diagonals.Contains(kv.Key)
                                                            && T[kv.Key].P.Min(p => p.u) < ub + pad && T[kv.Key].P.Max(p => p.u) > ua - pad);
                    int lo = Math.Min(ra, rb), hi = Math.Max(ra, rb);
                    var low = ra < rb ? new[] { m0, m1 } : new[] { n0, n1 }; var up = ra < rb ? new[] { n0, n1 } : new[] { m0, m1 };
                    // la de abajo, a la fila vecina de la de arriba; o la de arriba, a la vecina de la de abajo; o, si en medio hay
                    // alguna fila ocupada, lo más cerca que se pueda por cada lado
                    var freeRows = Enumerable.Range(lo + 1, hi - lo - 1).Select(r => (r, f: RowFree(r))).ToList();
                    int upTo = lo; foreach (var (r, f) in freeRows) { if (!f) break; upTo = r; }                     // hasta dónde puede bajar la de arriba
                    int downTo = hi; foreach (var (r, f) in freeRows.AsEnumerable().Reverse()) { if (!f) break; downTo = r; }   // hasta dónde puede subir la de abajo
                    if (downTo == lo + 1) foreach (int q in low) nrow[q] = lo + 1;
                    else if (upTo == hi - 1) foreach (int q in up) nrow[q] = hi - 1;
                    else
                    {
                        if (downTo < hi) foreach (int q in low) nrow[q] = downTo;
                        if (upTo > lo) foreach (int q in up) nrow[q] = upTo;
                    }
                }
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
                        // (1.2.81) a qué lado sale: hacia donde sigue la vía desde ese extremo (no por dónde cae en la zona: el
                        // principio de un trozo cortado a la derecha del centro salía como salida de la derecha y se montaba en otra vía)
                        bool toRight = side == 0 ? T[e].P[0].u > T[e].P[^1].u : T[e].P[^1].u > T[e].P[0].u;
                        if (toRight == right) ex.Add((p.v, en.r, e, side));
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

            // (1.2.81) una vía en fila que sale de una aguja o travesía en diagonal, si de ahí mismo sale un enlace que sube (o baja)
            // más lejos hacia el mismo lado: la vía espera un trecho por la fila de la aguja y le deja la diagonal (si no, las dos
            // diagonales iban una encima de otra o el enlace cortaba la vía: Estación Sur, la 801 y la 1)
            double ShiftAt(int e, int side)
            {
                if (!rowOf.TryGetValue(e, out int r)) return 0;
                var en = End(e, side); if (en.k != 'j' && en.k != 'g' || en.r == r) return 0;
                var ot = End(e, 1 - side); int dir = Math.Sign(r - en.r), ux = Math.Sign(ot.u - en.u);
                foreach (int l in T.Keys)
                {
                    if (l == e || rowOf.ContainsKey(l) || internalEdge.Contains(l)) continue;
                    for (int sd = 0; sd < 2; sd++)
                    {
                        var le = End(l, sd); if (le.n != en.n) continue;
                        var lo2 = End(l, 1 - sd);
                        if (Math.Sign(lo2.r - en.r) == dir && Math.Abs(lo2.r - en.r) > Math.Abs(r - en.r) && Math.Sign(lo2.u - en.u) == ux) return 2 * Row;
                    }
                }
                return 0;
            }
            // estirar donde una diagonal a 45° no cabe
            var cons = new List<(double lo, double hi, double need)>();
            foreach (int e in T.Keys)
            {
                if (internalEdge.Contains(e)) continue;
                if (sciss.Diagonals.Contains(e)) continue;   // (1.2.81: las diagonales de una tijera van en su símbolo: sin hueco a 45°)
                var a = End(e, 0); var b = End(e, 1);
                double need = rowOf.TryGetValue(e, out int r)
                    ? (Math.Abs(a.r - r) + Math.Abs(b.r - r)) * Row + (a.r != r || b.r != r ? Gap : 0)
                    : Math.Abs(a.r - b.r) * Row + Gap;
                need += ShiftAt(e, 0) + ShiftAt(e, 1);
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
            var segs = new List<(PointF a, PointF b, int e)>();
            void AddSegs(PointF[] pts, int e) { for (int i = 1; i < pts.Length; i++) segs.Add((pts[i - 1], pts[i], e)); }
            // (1.2.81, nudos) cortar una vía que en la realidad también se cruza (paso superior) apenas cuenta
            var realX = new Dictionary<(int, int), bool>();
            bool RealPair(int a, int b)
            {
                if (realX.TryGetValue((a, b), out bool r)) return r;
                var EA = g.Edges[a]; var EB = g.Edges[b]; r = false;
                bool shared = EA.A >= 0 && (EA.A == EB.A || EA.A == EB.B) || EA.B >= 0 && (EA.B == EB.A || EA.B == EB.B);
                // (sin contar lo que se toca junto a los extremos: dentro de una travesía las vías se juntan sin cruzarse)
                bool NearEnd(PointF q) { foreach (var E in new[] { EA, EB }) if (Math.Abs(q.X - E.X[0]) + Math.Abs(q.Y - E.Y[0]) < 4 || Math.Abs(q.X - E.X[^1]) + Math.Abs(q.Y - E.Y[^1]) < 4) return true; return false; }
                for (int i = 1; i < EA.X.Length && !r && !shared; i++)
                    for (int k = 1; k < EB.X.Length && !r; k++)
                        r = Cut(new PointF(EA.X[i - 1], EA.Y[i - 1]), new PointF(EA.X[i], EA.Y[i]), new PointF(EB.X[k - 1], EB.Y[k - 1]), new PointF(EB.X[k], EB.Y[k]), out var cq) && !NearEnd(cq);
                realX[(a, b)] = r; realX[(b, a)] = r; return r;
            }
            // los cruces reales del tramo e con las vías en fila ya dibujadas: su punto en el esquema, de A a B
            List<PointF> RealThrough(int e)
            {
                var E = g.Edges[e]; var cuts = new List<(double off, PointF p)>();
                foreach (var tf in Tracks)
                {
                    if (!tf.Flat || !RealPair(e, tf.Edge)) continue;
                    var F = g.Edges[tf.Edge];
                    for (int i = 1; i < E.X.Length; i++)
                        for (int k = 1; k < F.X.Length; k++)
                        {
                            double r1 = E.X[i] - E.X[i - 1], r2 = E.Y[i] - E.Y[i - 1], s1 = F.X[k] - F.X[k - 1], s2 = F.Y[k] - F.Y[k - 1], den = r1 * s2 - r2 * s1;
                            if (Math.Abs(den) < 1e-9) continue;
                            double cx = F.X[k - 1] - E.X[i - 1], cy = F.Y[k - 1] - E.Y[i - 1];
                            double t = (cx * s2 - cy * s1) / den, w = (cx * r2 - cy * r1) / den;
                            if (t < 0 || t > 1 || w < 0 || w > 1) continue;
                            double offE = E.Cum[i - 1] + t * (E.Cum[i] - E.Cum[i - 1]), offF = F.Cum[k - 1] + w * (F.Cum[k] - F.Cum[k - 1]);
                            if (!MapOff(tf.Edge, offF, out var p)) continue;
                            // lejos de las agujas de esa vía (a menos de 24 px parecía que el enlace se unía a ella)
                            const float Clear = 44;
                            var tp = tf.Pts; float L = 0; for (int q = 1; q < tp.Length; q++) L += Dist(tp[q - 1], tp[q]);
                            if (L > 3 * Clear)
                            {
                                if (Dist(p, tp[0]) < Clear) p = AlongPts(tp, Clear);
                                else if (Dist(p, tp[^1]) < Clear) p = AlongPts(tp, L - Clear);
                            }
                            cuts.Add((offE, p));
                        }
                }
                return cuts.OrderBy(c => c.off).Select(c => c.p).ToList();
            }
            double CutCount(List<PointF> pts, int e)
            {
                var s0 = pts[0]; var s1 = pts[^1]; double n = 0;
                for (int i = 1; i < pts.Count; i++)
                    foreach (var (c, d, f) in segs)
                    {
                        if (Cut(pts[i - 1], pts[i], c, d, out var x) && Dist(x, s0) > 1.5f && Dist(x, s1) > 1.5f) n += RealPair(e, f) ? 0.15 : 1;
                        else if (Collinear(pts[i - 1], pts[i], c, d) > 4) n += 3;   // (1.2.81) una encima de otra (Estación Sur: dos diagonales desde la misma travesía)
                    }
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
                    float sA = (float)ShiftAt(e, 0), sB = (float)ShiftAt(e, 1);
                    if (Math.Abs(xb - xa) < Math.Abs(a.r - r) * Row + Math.Abs(b.r - r) * Row + sA + sB) sA = sB = 0;   // (si no cabe, como siempre)
                    var pts = new List<PointF> { new PointF(xa, Y(a.r)), new PointF(xa + sgn * sA, Y(a.r)), new PointF(xa + sgn * (sA + Math.Abs(a.r - r) * Row), Y(r)), new PointF(xb - sgn * (sB + Math.Abs(b.r - r) * Row), Y(r)), new PointF(xb - sgn * sB, Y(b.r)), new PointF(xb, Y(b.r)) };
                    tr.Pts = Dedup(pts);
                    tr.Map = T[e].P.Select(p => (p.off, p.u)).ToArray();
                    Occupy(tr.Pts); AddSegs(tr.Pts, e);
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
                        pts = cand.Select(c => (c.p, sc: c.score + 10 * CutCount(c.p, e))).OrderBy(c => c.sc).First().p;
                    }
                    // (1.2.81, nudos) un enlace que en la realidad cruza vías en fila (un paso superior): pasa por cada una en el
                    // punto real del cruce (si no, en el Museo la línea de Lanuza cortaba en el dibujo vías que no cruza)
                    var through = RealThrough(e);
                    if (through.Count > 0)
                    {
                        int sy = Math.Sign(Y(b.r) - Y(a.r)), sx = Math.Sign(xb - xa);
                        var path = new List<PointF> { new PointF(xa, Y(a.r)) };
                        // llega y sale a 45° (por su fila hasta ahí)
                        float k0 = through[0].X - sx * Math.Abs(through[0].Y - Y(a.r)), k1 = through[^1].X + sx * Math.Abs(Y(b.r) - through[^1].Y);
                        if ((k0 - xa) * sx > 1) path.Add(new PointF(k0, Y(a.r)));
                        path.AddRange(through);
                        if ((xb - k1) * sx > 1) path.Add(new PointF(k1, Y(b.r)));
                        path.Add(new PointF(xb, Y(b.r)));
                        // siempre hacia delante: si un cruce queda un poco por detrás del anterior (por la holgura), a su altura
                        bool mono = true;
                        for (int i = 1; i < path.Count; i++)
                        {
                            if (sy != 0 && Math.Sign(path[i].Y - path[i - 1].Y) == -sy) mono = false;
                            if (sx != 0 && (path[i].X - path[i - 1].X) * sx < 0)
                            {
                                if (Math.Abs(path[i].X - path[i - 1].X) > 30 || i == path.Count - 1) mono = false;
                                else path[i] = new PointF(path[i - 1].X, path[i].Y);
                            }
                        }
                        if (Environment.GetEnvironmentVariable("SELECTOR_ESQ_DEBUG") is string dq && dq.Length > 0 && Station.Contains(dq)) Console.WriteLine($"  enlace {e} por cruces reales: {string.Join(" ", path.Select(q => $"({q.X:F0},{q.Y:F0})"))} mono {mono}");
                        if (mono)
                        {
                            pts = path;
                            int i0 = path.Count - through.Count - 1 - ((xb - k1) * sx > 1 ? 1 : 0);
                            for (int i = i0; i < i0 + through.Count; i++)
                            {
                                float dx2 = path[i + 1].X - path[i - 1].X, dy2 = path[i + 1].Y - path[i - 1].Y, dl = (float)Math.Sqrt(dx2 * dx2 + dy2 * dy2);
                                if (dl > 0.1f) Bridges.Add((path[i], new PointF(dx2 / dl, dy2 / dl), e));
                            }
                        }
                    }
                    tr.Pts = Dedup(pts);
                    Occupy(tr.Pts); AddSegs(tr.Pts, e);
                }
                tr.LenPx = Length(tr.Pts);
                tr.EndNode[0] = a.n; tr.EndNode[1] = b.n; tr.EndKind[0] = a.k; tr.EndKind[1] = b.k;
                tr.V = WMed(T[e].P); tr.UMin = T[e].P.Min(p => p.u); tr.UMax = T[e].P.Max(p => p.u); tr.UA = T[e].P[0].u; tr.UB = T[e].P[^1].u;
                Tracks.Add(tr); _byEdge[e] = tr;
                if (a.k == 'e') Buffers.Add(tr.Pts[0]);
                if (b.k == 'e') Buffers.Add(tr.Pts[^1]);
            }
            foreach (var kv in groupAt)
            {
                var sat = new PointF(X(kv.Value.u), Y(nrow[kv.Key]));
                Slips.Add(sat);
                var gn = group.Where(q => q.Value == kv.Key).Select(q => q.Key).OrderBy(n => NodeUV(n).u).ThenBy(n => NodeUV(n).v).ToArray();
                SlipGroups.Add(new SlipGroup { At = sat, Nodes = gn, Orders = gn.Select(n => geo != null && geo.OrderOf.TryGetValue(n, out int o) ? o : -1).ToArray() });
            }
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
                {
                    if (outs[k] >= 0 && _byEdge.TryGetValue(outs[k], out var tk)) sw.Legs[k] = Stub(tk, g.Edges[outs[k]].A == n, 12);
                    if (outs[k] >= 0) { sw.LegEdge[k] = outs[k]; sw.LegFromA[k] = g.Edges[outs[k]].A == n; sw.LegLen[k] = g.Edges[outs[k]].Len; }
                    if (outs[k] >= 0 && straightOf.TryGetValue(n, out int stE) && stE == outs[k]) sw.Straight = k;   // (1.2.81) la directa de verdad (StraightLeg)
                }
                Switches.Add(sw);
            }
            // (1.2.81) los escapes en tijera: sus cuatro agujas, unidas por sus paralelas y diagonales
            {
                var scs = Switches.Where(w => sciss.Straight.ContainsKey(w.Node)).ToDictionary(w => w.Node);
                var done = new HashSet<int>();
                foreach (var w0 in scs.Values)
                {
                    if (done.Contains(w0.Node)) continue;
                    var comp = new List<int>(); var q = new Queue<int>(); q.Enqueue(w0.Node); done.Add(w0.Node);
                    while (q.Count > 0)
                    {
                        int n = q.Dequeue(); comp.Add(n);
                        foreach (int e in g.EdgesAt(n))
                        {
                            if (!sciss.Diagonals.Contains(e) && !sciss.Parallels.Contains(e)) continue;
                            int m = g.Edges[e].A == n ? g.Edges[e].B : g.Edges[e].A;
                            if (m >= 0 && scs.ContainsKey(m) && done.Add(m)) q.Enqueue(m);
                        }
                    }
                    if (comp.Count != 4) continue;
                    var ws = comp.Select(n => scs[n]).ToList();
                    float my = ws.Average(w => w.At.Y);
                    var top = ws.Where(w => w.At.Y < my - 0.5f).OrderBy(w => w.At.X).ToList(); var bot = ws.Where(w => w.At.Y > my + 0.5f).OrderBy(w => w.At.X).ToList();
                    if (top.Count != 2 || bot.Count != 2) continue;
                    var order = new[] { top[0], top[1], bot[0], bot[1] };
                    var grp = new ScissorGroup();
                    for (int i = 0; i < 4; i++) { grp.Nodes[i] = order[i].Node; grp.Orders[i] = order[i].Order; grp.Straight[i] = order[i].Straight; grp.At[i] = order[i].At; }
                    int EdgeBetween(int a, int b) { foreach (int e in g.EdgesAt(a)) { var E = g.Edges[e]; if (E.A == a && E.B == b || E.B == a && E.A == b) return e; } return -1; }
                    grp.ParTop = EdgeBetween(grp.Nodes[0], grp.Nodes[1]); grp.ParBot = EdgeBetween(grp.Nodes[2], grp.Nodes[3]);
                    grp.DiagDown = EdgeBetween(grp.Nodes[0], grp.Nodes[3]); grp.DiagUp = EdgeBetween(grp.Nodes[2], grp.Nodes[1]);
                    if (grp.ParTop < 0 || grp.ParBot < 0 || grp.DiagDown < 0 || grp.DiagUp < 0) continue;
                    ScissorGroups.Add(grp);
                    foreach (int e in new[] { grp.ParTop, grp.ParBot, grp.DiagDown, grp.DiagUp }) HiddenEdges.Add(e);
                }
            }
            // cruces sin agujas: la X donde se cortan dos vías
            // (1.2.81) cada cruce, una vez, donde se cortan en el dibujo las dos vías que se cruzan (las dos traen su marca; cada
            // una en su sitio, en Industrias CAF salían dos X separadas y una sobre un tramo sin cruce)
            var dmk = new List<(int e, double off, double x, double y)>();
            foreach (int e in T.Keys)
                if (_byEdge.ContainsKey(e) && !sciss.Parallels.Contains(e))
                    foreach (double off in g.Edges[e].Diamonds) { var (dx0, dy0) = g.PointAt(e, off); dmk.Add((e, off, dx0, dy0)); }
            var usedD = new HashSet<int>();
            for (int i = 0; i < dmk.Count; i++)
            {
                if (usedD.Contains(i)) continue;
                var a = dmk[i]; PointF? at = null;
                for (int j = 0; j < dmk.Count && at == null; j++)
                {
                    var b = dmk[j];
                    if (j == i || usedD.Contains(j) || b.e == a.e || Math.Abs(a.x - b.x) + Math.Abs(a.y - b.y) > 4) continue;
                    var A = _byEdge[a.e].Pts; var B = _byEdge[b.e].Pts; PointF? best = null; float bd = float.MaxValue;
                    MapOff(a.e, a.off, out var ref0);
                    for (int u = 1; u < A.Length; u++)
                        for (int w = 1; w < B.Length; w++)
                            if (Cut(A[u - 1], A[u], B[w - 1], B[w], out var x) && Dist(x, ref0) < bd) { bd = Dist(x, ref0); best = x; }
                    if (best != null) { at = best; usedD.Add(j); }
                }
                usedD.Add(i);
                if (at == null && MapOff(a.e, a.off, out var cp0)) at = cp0;
                if (at is PointF cp && !Crossings.Any(c => Dist(c, cp) < 6)) Crossings.Add(cp);
            }
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
                    // (1.2.81) del lado en que está de verdad, como en el trazado real: el de la vía vecina más lejana (el andén ocupa
                    // ese hueco; en un andén central, la vía de al lado está más separada que en una entrevía normal). Siempre debajo,
                    // el andén de la vía 3 de Dos Hermanos (por fuera) y los centrales (Aranda, Carlos Abadias…) salían al revés
                    double midOff = (m.off + other.off) / 2;
                    int rs = RouteBoard.PlatformSide(g, tr.Edge, midOff);
                    var (pxr, pyr) = g.PointAt(tr.Edge, midOff);
                    var (ax1, ay1) = g.PointAt(tr.Edge, Math.Max(0, midOff - 2)); var (ax2, ay2) = g.PointAt(tr.Edge, Math.Min(E.Len, midOff + 2));
                    double tdx = ax2 - ax1, tdy = ay2 - ay1, tl = Math.Sqrt(tdx * tdx + tdy * tdy);
                    bool above = false;
                    if (tl > 1e-6) { tdx /= tl; tdy /= tl; above = V(pxr - tdy * rs * 3, pyr + tdx * rs * 3) < V(pxr, pyr); }
                    Platforms.Add(new Platform { Bar = new RectangleF(x0, p0.Y, x1 - x0, 3f), Label = pst != null && mm.Success ? mm.Groups[1].Value : null, Above = above });
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
                    bool right = side == 0 ? T[e].P[0].u > T[e].P[^1].u : T[e].P[^1].u > T[e].P[0].u;   // (1.2.81) hacia donde sale la vía
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
                // (1.2.81) entre los puntos que están en su fila (con el trecho por la fila de la aguja, el segundo punto ya no lo está)
                var onRow = tr.Pts.Where(q => Math.Abs(q.Y - tr.RowIdx * Row) < 0.1f).Select(q => q.X).ToList();
                float lo = onRow.Count >= 2 ? onRow.Min() : Math.Min(tr.Pts[0].X, tr.Pts[^1].X), hi = onRow.Count >= 2 ? onRow.Max() : Math.Max(tr.Pts[0].X, tr.Pts[^1].X);
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
        // lo que dos segmentos van uno encima del otro (paralelos, a menos de 1,5 px), en px; 0 si no
        static float Collinear(PointF a, PointF b, PointF c, PointF d)
        {
            float dx = b.X - a.X, dy = b.Y - a.Y, L = (float)Math.Sqrt(dx * dx + dy * dy); if (L < 0.5f) return 0;
            dx /= L; dy /= L;
            float ex = d.X - c.X, ey = d.Y - c.Y, M = (float)Math.Sqrt(ex * ex + ey * ey); if (M < 0.5f) return 0;
            if (Math.Abs(dx * ey - dy * ex) / M > 0.02f) return 0;                       // no paralelos
            if (Math.Abs((c.X - a.X) * dy - (c.Y - a.Y) * dx) > 1.5f) return 0;          // no en la misma recta
            float t0 = (c.X - a.X) * dx + (c.Y - a.Y) * dy, t1 = (d.X - a.X) * dx + (d.Y - a.Y) * dy;
            return Math.Max(0, Math.Min(L, Math.Max(t0, t1)) - Math.Max(0, Math.Min(t0, t1)));
        }
        static PointF AlongPts(PointF[] p, float d)
        {
            for (int i = 1; i < p.Length; i++)
            {
                float s = Dist(p[i - 1], p[i]);
                if (d <= s || i == p.Length - 1) { float t = s > 0 ? Math.Min(1, d / s) : 0; return new PointF(p[i - 1].X + (p[i].X - p[i - 1].X) * t, p[i - 1].Y + (p[i].Y - p[i - 1].Y) * t); }
                d -= s;
            }
            return p[^1];
        }
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
