// PUESTO DE MANDO en una sola pantalla (1.2.76): toda la ruta en un tablero que se mueve y se amplía, con el trazado real
// de las vías (la proyección local del .tdb, como el mapa) y lo del videográfico encima: cantones (libres, ocupados,
// itinerarios, anulándose, en cola), agujas (el lado puesto, su número de la estación, azul si está enclavada), señales
// con su figura del RCF (el mástil hacia el lado en el que está la señal y la figura a lo largo de la vía), andenes con el
// número de su vía, estaciones y trenes. Lo que se dibuja depende del zoom: de lejos, las vías y las estaciones; de cerca,
// todo. Unidades del tablero: metros (x hacia el este, y hacia el sur); en pantalla P = origen + zoom · tablero.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Text.RegularExpressions;

namespace SelectOR
{
    public sealed partial class RouteBoard
    {
        public RouteGraph G; public RoomMapGeo Geo;
        public List<(int e, double a, double b)> Circuits = new();
        public sealed class Sw { public RouteGraph.SwitchGeo S; public int Node, Order; public PointF At; public PointF[][] Legs = new PointF[2][]; public int Number; public string Station; public string Label; }   // Label: (1.2.81) como en su esquema («7», «12a»)
        public sealed class Sg { public RoomMapGeo.Sig S; public PointF At; public PointF Dir; public PointF SideV; }   // Dir: hacia donde rige; SideV: hacia donde está la señal
        public sealed class Plat { public int E; public double A, B; public int Side; public string Label; public string Station; }
        public readonly List<Sw> Switches = new();
        // (1.2.81, petición) los escapes en tijera, como en el esquema: S = a0, a1 (una paralela), b0, b1 (la otra); diagonales a0-b1
        // y b0-a1; StraightK: la rama paralela de cada aguja; Edges: paralela a, paralela b, diagonal a0-b1, diagonal b0-a1
        public sealed class Tij { public Sw[] S = new Sw[4]; public int[] StraightK = new int[4]; public int[] Edges = new int[4]; public string Name; }
        public readonly List<Tij> Scissors = new();
        public readonly HashSet<int> ScissorNodes = new();
        public readonly List<Sg> Signals = new();
        public readonly List<Plat> Platforms = new();
        public readonly List<(string name, PointF at, RectangleF box)> Stations = new();   // box: lo que ocupan sus andenes
        public readonly List<PointF> Buffers = new();
        public RectangleF Bounds;
        public Dictionary<int, List<(double off, int dir, int obj)>> Sbe;

        // ── Ensanche en las estaciones: el trazado es el real, pero en cada estación las vías se separan Spread veces a lo ancho
        // (4,5 m entre vías → unos 18 m), con su eje (el de sus andenes) y un paso suave hacia la línea. Todo lo del tablero
        // (vías, agujas, señales, andenes, trenes) pasa por Warp, así que no cambia el orden de las vías ni hay cruces nuevos.
        public const double Spread = 4.0;
        // (1.2.81) y a lo largo: las cabeceras (entre el final de los andenes y las últimas agujas) se estiran Stretch veces,
        // para que agujas y señales no se amontonen; la vía de línea de al lado (la rampa) se encoge lo mismo, así que fuera
        // de ella no se mueve nada. P0..P1: lo que no se estira (los andenes; en un grupo de agujas, su centro).
        public static double Stretch = 1;   // (probado 1,7: al encuadrar una estación el zoom baja lo mismo y no gana sitio; apagado)
        sealed class Field { public double Cx, Cy, Ax, Ay, U0, U1, V0, P0, P1; public string Name; }
        readonly List<Field> _fields = new();
        public (double x, double y) Warp(double x, double y)
        {
            double sx = 0, sy = 0, sw = 0;
            foreach (var f in _fields)
            {
                double dx = x - f.Cx, dy = y - f.Cy, u = dx * f.Ax + dy * f.Ay, v = -dx * f.Ay + dy * f.Ax;
                const double Ramp = 260, Side = 220;
                double wu = u < f.U0 ? Smooth(1 - (f.U0 - u) / Ramp) : u > f.U1 ? Smooth(1 - (u - f.U1) / Ramp) : 1;
                double au = Along(f, u, Ramp);   // (1.2.81) a lo largo (su rampa puede ser más larga que la del ensanche)
                if (wu <= 0 && au == 0) continue;
                double av = Math.Abs(v);
                if (av > f.V0 + Side) continue;
                // a lo ancho: dentro de la estación, × Spread; fuera, se aparta lo mismo que el borde (y se apaga lejos)
                double nv = av <= f.V0 ? av * Spread : f.V0 * Spread + (av - f.V0);
                double fade = av <= f.V0 ? 1 : Smooth(1 - (av - f.V0) / Side);
                double dv = (nv - av) * Math.Sign(v) * wu * fade;
                double du = au * fade;   // (1.2.81) la estación, estirada a lo largo
                sx += -f.Ay * dv + f.Ax * du; sy += f.Ax * dv + f.Ay * du; sw += wu * fade;
            }
            if (sw > 1) { sx /= sw; sy /= sw; }
            return (x + sx, y + sy);
        }
        static double Smooth(double t) { t = Math.Max(0, Math.Min(1, t)); return t * t * (3 - 2 * t); }
        // (1.2.81) cuánto se mueve a lo largo un punto en «u» del eje de un ensanche: en las cabeceras, hacia fuera en
        // proporción a lo que se aleja de los andenes; en la rampa, de vuelta a cero poco a poco (sin plegarse: la pendiente
        // de la vuelta nunca pasa de 0,6)
        static double Along(Field f, double u, double ramp)
        {
            if (Stretch <= 1) return 0;
            double k = Stretch - 1, tl = Math.Max(0, f.P0 - f.U0), tr = Math.Max(0, f.U1 - f.P1);
            // la rampa de vuelta, lo larga que haga falta para no plegarse (pendiente ≤ 0,6)
            double rl = Math.Max(ramp, 1.5 * k * tl / 0.6), rr = Math.Max(ramp, 1.5 * k * tr / 0.6);
            if (u >= f.P0 && u <= f.P1) return 0;
            if (u < f.P0)
            {
                if (u >= f.U0) return -k * (f.P0 - u);
                if (u >= f.U0 - rl) return -k * tl * Smooth((u - (f.U0 - rl)) / rl);
                return 0;
            }
            if (u <= f.U1) return k * (u - f.P1);
            if (u <= f.U1 + rr) return k * tr * Smooth(((f.U1 + rr) - u) / rr);
            return 0;
        }
        public PointF W(double x, double y) { var (a, b) = Warp(x, y); return new PointF((float)a, (float)b); }
        // (pruebas) los ensanches: nombre, centro, medio ancho
        public IEnumerable<(string name, double cx, double cy, double v0, double u0, double u1)> FieldsInfo => _fields.Select(f => (f.Name, f.Cx, f.Cy, f.V0, f.U0, f.U1));
        float[][] _wx, _wy; double[][] _woff;   // cada tramo, deformado (y repartido cada pocos metros)

        void MakeFields()
        {
            var byStation = new Dictionary<string, List<(double x, double y)>>(StringComparer.Ordinal);
            for (int e = 0; e < G.Edges.Length; e++)
                foreach (var (off, name) in G.Edges[e].Platforms)
                    if (!string.IsNullOrWhiteSpace(name)) { if (!byStation.TryGetValue(name, out var l)) byStation[name] = l = new(); l.Add(G.PointAt(e, off)); }
            foreach (var (st, e, a, b) in PlatformRanges())
                if (st != null) { if (!byStation.TryGetValue(st, out var l)) byStation[st] = l = new(); for (double o = a; o <= b; o += 10) l.Add(G.PointAt(e, o)); }
            foreach (var kv in byStation)
            {
                var pts = kv.Value; if (pts.Count < 2) continue;
                double cx = pts.Average(q => q.x), cy = pts.Average(q => q.y);
                double sxx = 0, syy = 0, sxy = 0; foreach (var (x, y) in pts) { sxx += (x - cx) * (x - cx); syy += (y - cy) * (y - cy); sxy += (x - cx) * (y - cy); }
                double ang = 0.5 * Math.Atan2(2 * sxy, sxx - syy), ax = Math.Cos(ang), ay = Math.Sin(ang);
                double u0 = double.MaxValue, u1 = double.MinValue, v0 = 0;
                foreach (var (x, y) in pts) { double u = (x - cx) * ax + (y - cy) * ay, v = -(x - cx) * ay + (y - cy) * ax; u0 = Math.Min(u0, u); u1 = Math.Max(u1, u); v0 = Math.Max(v0, Math.Abs(v)); }
                // (lo que hay a lo ancho entre sus andenes y un poco más; a lo largo, las cabeceras con sus agujas)
                _fields.Add(new Field { Name = kv.Key, Cx = cx, Cy = cy, Ax = ax, Ay = ay, U0 = u0 - 120, U1 = u1 + 120, V0 = Math.Max(14, v0 + 10), P0 = (u0 + u1) / 2, P1 = (u0 + u1) / 2 });   // (toda la estación, desde su centro)
            }
            // fuera de las estaciones: los grupos de agujas (escaleras y escapes entre vías de una línea de varias vías)
            int nst = _fields.Count;
            bool InStation(double x, double y)
            {
                for (int i = 0; i < nst; i++)
                {
                    var f = _fields[i]; double dx = x - f.Cx, dy = y - f.Cy, u = dx * f.Ax + dy * f.Ay, v = -dx * f.Ay + dy * f.Ax;
                    if (u >= f.U0 + 60 && u <= f.U1 - 60 && Math.Abs(v) <= f.V0 - 2) return true;   // (bien dentro: lo del borde, a un grupo propio)
                }
                return false;
            }
            var sws = Geo.Switches.Where(w => !w.Hidden).Select(w => G.Proj(w.Lat, w.Lon)).Where(q => !InStation(q.x, q.y)).ToList();
            var grp = new int[sws.Count]; for (int i = 0; i < grp.Length; i++) grp[i] = i;
            int Find(int i) { while (grp[i] != i) i = grp[i] = grp[grp[i]]; return i; }
            for (int i = 0; i < sws.Count; i++)
                for (int j = i + 1; j < sws.Count; j++)
                {
                    double dx = sws[i].x - sws[j].x, dy = sws[i].y - sws[j].y;
                    if (dx * dx + dy * dy < 130 * 130) grp[Find(i)] = Find(j);
                }
            foreach (var cl in Enumerable.Range(0, sws.Count).GroupBy(Find).Where(g2 => g2.Count() >= 3))
            {
                var at = cl.Select(i => sws[i]).ToList();
                double x0 = at.Min(q => q.x) - 150, x1 = at.Max(q => q.x) + 150, y0 = at.Min(q => q.y) - 150, y1 = at.Max(q => q.y) + 150;
                var pts = new List<(double x, double y)>();
                for (int e = 0; e < G.Edges.Length; e++)
                {
                    var E = G.Edges[e];
                    for (double o = 0; o <= E.Len; o += 10)
                    {
                        var (x, y) = G.PointAt(e, o);
                        if (x < x0 || x > x1 || y < y0 || y > y1) continue;
                        if (at.Any(q => (q.x - x) * (q.x - x) + (q.y - y) * (q.y - y) < 150 * 150)) pts.Add((x, y));
                    }
                }
                if (pts.Count < 4) continue;
                double cx = pts.Average(q => q.x), cy = pts.Average(q => q.y);
                double sxx = 0, syy = 0, sxy = 0; foreach (var (x, y) in pts) { sxx += (x - cx) * (x - cx); syy += (y - cy) * (y - cy); sxy += (x - cx) * (y - cy); }
                double ang = 0.5 * Math.Atan2(2 * sxy, sxx - syy), ax = Math.Cos(ang), ay = Math.Sin(ang);
                double u0 = double.MaxValue, u1 = double.MinValue, v0 = 0;
                foreach (var (x, y) in at) { double u = (x - cx) * ax + (y - cy) * ay; u0 = Math.Min(u0, u); u1 = Math.Max(u1, u); }
                foreach (var (x, y) in pts) { double u = (x - cx) * ax + (y - cy) * ay, v = -(x - cx) * ay + (y - cy) * ax; if (u >= u0 - 40 && u <= u1 + 40) v0 = Math.Max(v0, Math.Abs(v)); }
                if (v0 > 70) continue;   // (no es un haz de vías paralelas: un nudo de líneas que se cruzan)
                _fields.Add(new Field { Name = "agujas", Cx = cx, Cy = cy, Ax = ax, Ay = ay, U0 = u0 - 60, U1 = u1 + 60, V0 = Math.Max(10, v0 + 6), P0 = (u0 + u1) / 2, P1 = (u0 + u1) / 2 });
            }
        }

        List<(string st, int e, double a, double b)> _pr;
        List<(string st, int e, double a, double b)> PlatformRanges()
        {
            if (_pr != null) return _pr;
            _pr = new();
            for (int e = 0; e < G.Edges.Length; e++)
            {
                var E = G.Edges[e]; if (E.PlatformMarks.Count == 0) continue;
                var marks = E.PlatformMarks.OrderBy(m => m.off).ToList(); var used = new HashSet<int>();
                for (int i = 0; i < marks.Count; i++)
                {
                    if (used.Contains(i)) continue;
                    int j = marks.FindIndex(i + 1, m => m.id == marks[i].linked || m.linked == marks[i].id);
                    double ra, rb;
                    if (j > i) { ra = marks[i].off; rb = marks[j].off; used.Add(i); used.Add(j); }
                    else if (marks[i].start) { ra = marks[i].off; rb = E.Len; } else { ra = 0; rb = marks[i].off; }
                    double mid = (ra + rb) / 2;
                    string st = E.Platforms.OrderBy(p => Math.Abs(p.off - mid)).Select(p => p.name).FirstOrDefault();
                    if (rb - ra >= 5) _pr.Add((st, e, ra, rb));
                }
            }
            return _pr;
        }

        void WarpEdges()
        {
            int n = G.Edges.Length; _wx = new float[n][]; _wy = new float[n][]; _woff = new double[n][];
            for (int e = 0; e < n; e++)
            {
                var E = G.Edges[e]; if (E.X.Length < 2) continue;
                // repartido cada 8 m como mucho (para que el ensanche siga la vía), y los vértices de siempre
                var offs = new List<double>();
                for (int i = 0; i < E.X.Length; i++)
                {
                    if (i > 0) { double a = E.Cum[i - 1], b = E.Cum[i]; int k = (int)((b - a) / 8); for (int j = 1; j <= k; j++) offs.Add(a + (b - a) * j / (k + 1)); }
                    offs.Add(E.Cum[i]);
                }
                _woff[e] = offs.ToArray(); _wx[e] = new float[offs.Count]; _wy[e] = new float[offs.Count];
                for (int i = 0; i < offs.Count; i++) { var (x, y) = G.PointAt(e, offs[i]); var (wx, wy) = Warp(x, y); _wx[e][i] = (float)wx; _wy[e][i] = (float)wy; }
                SmoothEdge(e);
            }
        }

        // (1.2.77) curvas suaves: sin picos en los vértices del .tdb ni escalones donde empieza el ensanche de una
        // estación. Suavizado gaussiano a lo largo del tramo (varias pasadas de 1-2-1, unos 20 m), con los extremos
        // quietos: los primeros 10 m junto a cada aguja o unión no se mueven y luego entra poco a poco (las ramas de
        // las agujas y el tramo de al lado siguen encajando). Las señales y los andenes van sobre esta misma vía (At).
        const int SmoothPasses = 16; const double SmoothKeep = 10, SmoothRamp = 30;
        void SmoothEdge(int e)
        {
            var x = _wx[e]; var y = _wy[e]; var o = _woff[e]; int n = x.Length;
            if (n < 5) return;
            double len = o[n - 1];
            var w = new float[n];
            for (int i = 0; i < n; i++) { double d = Math.Min(o[i] - o[0], len - o[i]); w[i] = (float)Math.Max(0, Math.Min(1, (d - SmoothKeep) / SmoothRamp)); }
            if (w.All(v => v == 0)) return;
            var ox = (float[])x.Clone(); var oy = (float[])y.Clone();
            var tx = new float[n]; var ty = new float[n];
            for (int p = 0; p < SmoothPasses; p++)
            {
                tx[0] = x[0]; ty[0] = y[0]; tx[n - 1] = x[n - 1]; ty[n - 1] = y[n - 1];
                for (int i = 1; i < n - 1; i++)
                {
                    // (con la distancia real a cada vecino: los puntos no están repartidos del todo igual)
                    double da = Math.Max(0.01, o[i] - o[i - 1]), db = Math.Max(0.01, o[i + 1] - o[i]), ka = db / (da + db), kb = da / (da + db);
                    tx[i] = (float)(0.5 * x[i] + 0.5 * (ka * x[i - 1] + kb * x[i + 1]));
                    ty[i] = (float)(0.5 * y[i] + 0.5 * (ka * y[i - 1] + kb * y[i + 1]));
                }
                (x, tx) = (tx, x); (y, ty) = (ty, y);
            }
            for (int i = 0; i < n; i++) { _wx[e][i] = ox[i] + (x[i] - ox[i]) * w[i]; _wy[e][i] = oy[i] + (y[i] - oy[i]) * w[i]; }
        }

        // el punto (deformado) de un tramo en un metro
        public PointF At(int e, double off)
        {
            var o = _woff?[e]; if (o == null) { var (x, y) = G.PointAt(e, off); return W(x, y); }
            if (off <= o[0]) return new PointF(_wx[e][0], _wy[e][0]);
            if (off >= o[^1]) return new PointF(_wx[e][^1], _wy[e][^1]);
            int lo = 0, hi = o.Length - 1; while (hi - lo > 1) { int m = (lo + hi) / 2; if (o[m] <= off) lo = m; else hi = m; }
            double t = (off - o[lo]) / Math.Max(1e-9, o[hi] - o[lo]);
            return new PointF((float)(_wx[e][lo] + (_wx[e][hi] - _wx[e][lo]) * t), (float)(_wy[e][lo] + (_wy[e][hi] - _wy[e][lo]) * t));
        }

        static readonly Regex TrackNo = new Regex(@"\bv[ií]a\s*(\d+[a-zA-Z]?)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // numberOf(nodo) → (estación, número de la aguja en su estación); null si no se sabe
        public static RouteBoard Build(RouteGraph g, RoomMapGeo geo, Func<int, (string st, int n)?> numberOf = null)
        {
            var b = new RouteBoard { G = g, Geo = geo };
            b.Sbe = SchematicCircuits.SignalsByEdge(g, geo);
            b.MakeFields(); b.WarpEdges();
            Finish(b, g, geo, numberOf);
            return b;
        }

        // (Build) con la geometría de cada tramo ya hecha: cantones, agujas, señales, andenes y estaciones
        static void Finish(RouteBoard b, RouteGraph g, RoomMapGeo geo, Func<int, (string st, int n)?> numberOf)
        {
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            for (int e = 0; e < g.Edges.Length; e++) if (b._wx[e] != null) for (int i = 0; i < b._wx[e].Length; i++) { x0 = Math.Min(x0, b._wx[e][i]); x1 = Math.Max(x1, b._wx[e][i]); y0 = Math.Min(y0, b._wy[e][i]); y1 = Math.Max(y1, b._wy[e][i]); }
            b.Bounds = RectangleF.FromLTRB(x0, y0, x1, y1);
            // cantones: cada tramo entero, partido en sus señales
            for (int e = 0; e < g.Edges.Length; e++)
            {
                var E = g.Edges[e]; if (E.X.Length < 2 || E.Len < 0.3) continue;
                var cuts = new List<double> { 0, E.Len };
                if (b.Sbe.TryGetValue(e, out var l)) cuts.AddRange(l.Select(q => q.off).Where(o => o > 1 && o < E.Len - 1));
                cuts.Sort();
                for (int i = 1; i < cuts.Count; i++) if (cuts[i] - cuts[i - 1] > 0.5) b.Circuits.Add((e, cuts[i - 1], cuts[i]));
                if (E.A < 0) b.Buffers.Add(b.At(e, 0));
                if (E.B < 0) b.Buffers.Add(b.At(e, E.Len));
            }
            // agujas: su punto y el principio de sus dos ramas (la 0 y la 1, como THROWNODE)
            // (1.2.81) también las «unidas» de las travesías y tijeras (comparten identificador): el puesto las pide por su nodo
            // (THROWNODE), como el mapa grande del HUD desde la 1.2.71
            foreach (var sw in geo.Switches)
            {
                var (ax, ay) = g.Proj(sw.Lat, sw.Lon);
                var w = new Sw { S = sw, Node = sw.Node, Order = sw.Order, At = b.W(ax, ay) };
                for (int k = 0; k < 2; k++)
                    if (sw.Branch[k] != null && sw.Branch[k].Count >= 2)
                        w.Legs[k] = sw.Branch[k].Select(q => { var (x, y) = g.Proj(q.lat, q.lon); return b.W(x, y); }).ToArray();
                var num = numberOf?.Invoke(sw.Node);
                if (num.HasValue) { w.Station = num.Value.st; w.Number = num.Value.n; }
                b.Switches.Add(w);
            }
            foreach (var t in geo.ScissorList)
            {
                var ws = t.S.Select(s => b.Switches.FirstOrDefault(w => w.Node == s.Node)).ToArray();
                if (ws.Any(w => w == null)) continue;
                b.Scissors.Add(new Tij { S = ws, StraightK = t.StraightK.ToArray(), Edges = t.Edges.ToArray() });
                foreach (var w in ws) b.ScissorNodes.Add(w.Node);
            }
            // señales: en su vía, mirando hacia donde rigen, y el lado en el que están
            foreach (var sg in geo.Signals)
            {
                if (sg.Edge < 0 || !b.Sbe.TryGetValue(sg.Edge, out var l)) continue;
                int dir = 0; foreach (var q in l) if (q.obj == sg.Obj && Math.Abs(q.off - sg.Off) < 0.01) { dir = q.dir; break; }
                if (dir == 0) foreach (var q in l) if (q.obj == sg.Obj) { dir = q.dir; break; }
                if (dir == 0) continue;
                var E = g.Edges[sg.Edge];
                var (px, py) = g.PointAt(sg.Edge, sg.Off);
                var (qx1, qy1) = g.PointAt(sg.Edge, Math.Max(0, sg.Off - 3)); var (qx2, qy2) = g.PointAt(sg.Edge, Math.Min(E.Len, sg.Off + 3));
                double dx = (qx2 - qx1) * dir, dy = (qy2 - qy1) * dir, dl = Math.Sqrt(dx * dx + dy * dy); if (dl < 1e-6) continue;
                dx /= dl; dy /= dl;
                var (rx, ry) = g.Proj(sg.Lat, sg.Lon);
                double cross = dx * (ry - py) - dy * (rx - px);
                // (la señal, a su lado de la vía; si está encima, a la derecha de la marcha, como en España)
                int side = Math.Abs(cross) > 0.4 ? Math.Sign(cross) : 1;
                var wp = b.At(sg.Edge, sg.Off); var w1 = b.At(sg.Edge, Math.Max(0, sg.Off - 3)); var w2 = b.At(sg.Edge, Math.Min(E.Len, sg.Off + 3));
                double wdx = (w2.X - w1.X) * dir, wdy = (w2.Y - w1.Y) * dir, wl = Math.Sqrt(wdx * wdx + wdy * wdy);
                if (wl < 1e-6) { wdx = dx; wdy = dy; } else { wdx /= wl; wdy /= wl; }
                b.Signals.Add(new Sg { S = sg, At = wp, Dir = new PointF((float)wdx, (float)wdy), SideV = new PointF((float)(-wdy * side), (float)(wdx * side)) });
            }
            // andenes: entre sus marcas de principio y fin en cada tramo; el número de la vía del nombre del andén
            for (int e = 0; e < g.Edges.Length; e++)
            {
                var E = g.Edges[e];
                if (E.Platforms.Count == 0 && E.PlatformMarks.Count == 0) continue;
                var marks = E.PlatformMarks.OrderBy(m => m.off).ToList();
                var ranges = new List<(double a, double b, uint id)>();
                var used = new HashSet<int>();
                for (int i = 0; i < marks.Count; i++)
                {
                    if (used.Contains(i)) continue;
                    int j = marks.FindIndex(i + 1, m => m.id == marks[i].linked || m.linked == marks[i].id);
                    if (j > i) { ranges.Add((marks[i].off, marks[j].off, marks[i].id)); used.Add(i); used.Add(j); }
                    else ranges.Add(marks[i].start ? (marks[i].off, E.Len, marks[i].id) : (0, marks[i].off, marks[i].id));
                }
                foreach (var (ra, rb, id) in ranges)
                {
                    if (rb - ra < 5) continue;
                    double mid = (ra + rb) / 2;
                    string st = E.Platforms.OrderBy(p => Math.Abs(p.off - mid)).Select(p => p.name).FirstOrDefault();
                    string raw = E.PlatformNames.OrderBy(p => Math.Abs(p.off - mid)).Select(p => p.raw).FirstOrDefault();
                    var m = raw != null ? TrackNo.Match(raw) : Match.Empty;
                    b.Platforms.Add(new Plat { E = e, A = ra, B = rb, Station = st, Label = m.Success ? m.Groups[1].Value : null, Side = b.FreeSide(e, mid) });
                }
            }
            // estaciones: el centro de sus andenes y lo que ocupan
            foreach (var grp in b.Platforms.Where(p => !string.IsNullOrWhiteSpace(p.Station)).GroupBy(p => p.Station))
            {
                var pts = new List<PointF>();
                foreach (var p in grp) { var pc = b.Piece(p.E, p.A, p.B); if (pc != null) pts.AddRange(pc); }
                if (pts.Count == 0) continue;
                var box = RectangleF.FromLTRB(pts.Min(q => q.X), pts.Min(q => q.Y), pts.Max(q => q.X), pts.Max(q => q.Y));
                b.Stations.Add((grp.Key, new PointF(box.X + box.Width / 2, box.Y + box.Height / 2), box));
            }
        }

        // el lado del tramo e en el metro off sin otra vía cerca (+1 a la derecha del sentido A→B, −1 a la izquierda)
        int FreeSide(int e, double off) => PlatformSide(G, e, off);
        // (1.2.81, también para el esquema) el lado de la vía en el que está su andén: +1 a la izquierda de su sentido (A→B), −1
        // a la derecha; el de la vía vecina más lejana
        public static int PlatformSide(RouteGraph G, int e, double off)
        {
            var (x1, y1) = G.PointAt(e, Math.Max(0, off - 2)); var (x2, y2) = G.PointAt(e, Math.Min(G.Edges[e].Len, off + 2));
            double dx = x2 - x1, dy = y2 - y1, l = Math.Sqrt(dx * dx + dy * dy); if (l < 1e-6) return 1;
            dx /= l; dy /= l;
            var (px, py) = G.PointAt(e, off);
            double Near(int s)
            {
                double best = 99;
                for (double d = 2.5; d <= 6; d += 1.75)
                {
                    double qx = px - dy * s * d, qy = py + dx * s * d;
                    for (int f = 0; f < G.Edges.Length; f++)
                    {
                        if (f == e) continue;
                        var F = G.Edges[f];
                        for (int i = 1; i < F.X.Length; i++)
                        {
                            double ax = F.X[i - 1], ay = F.Y[i - 1], bx = F.X[i], by = F.Y[i];
                            if (Math.Min(ax, bx) > qx + 8 || Math.Max(ax, bx) < qx - 8 || Math.Min(ay, by) > qy + 8 || Math.Max(ay, by) < qy - 8) continue;
                            double sx = bx - ax, sy = by - ay, L2 = sx * sx + sy * sy;
                            double t = L2 < 1e-9 ? 0 : Math.Max(0, Math.Min(1, ((qx - ax) * sx + (qy - ay) * sy) / L2));
                            double ex = ax + sx * t - qx, ey = ay + sy * t - qy, dd = Math.Sqrt(ex * ex + ey * ey);
                            if (dd < best) best = dd;
                        }
                    }
                }
                return best;
            }
            return Near(1) >= Near(-1) ? 1 : -1;
        }

        // los puntos del tramo e entre dos metros
        public PointF[] Piece(int e, double a, double b)
        {
            var E = G.Edges[e]; if (E.X.Length < 2 || _woff?[e] == null) return null;
            if (a > b) (a, b) = (b, a);
            a = Math.Max(0, a); b = Math.Min(E.Len, b); if (b - a < 0.05) return null;
            var l = new List<PointF> { At(e, a) };
            var o = _woff[e];
            for (int i = 0; i < o.Length; i++) if (o[i] > a && o[i] < b) l.Add(new PointF(_wx[e][i], _wy[e][i]));
            l.Add(At(e, b));
            return l.ToArray();
        }

        // el tramo y el metro de un punto del mapa (para los trenes): el más cercano, a menos de maxM
        public bool Locate(double lat, double lon, out int e, out double off) { bool ok = G.Nearest(lat, lon, 8, out e, out off, out _); return ok; }
    }

    public static class RouteBoardDraw
    {
        static readonly ConditionalWeak<RouteBoard, List<(RouteBoard.Sg sg, RectangleF box)>> _sigBoxes = new();
        static readonly ConditionalWeak<RouteBoard, List<(RouteBoard.Sw sw, RectangleF box)>> _swBoxes = new();
        static readonly ConditionalWeak<RouteBoard, List<(string name, RectangleF box)>> _stBoxes = new();
        // los rótulos de las estaciones en el último dibujo (para el clic)
        public static IEnumerable<(string name, RectangleF box)> StationLabels(RouteBoard b) => b != null && _stBoxes.Get(b, out var l) ? l : Enumerable.Empty<(string, RectangleF)>();

        sealed class ConditionalWeak<K, V> where K : class where V : class
        {
            readonly System.Runtime.CompilerServices.ConditionalWeakTable<K, V> _t = new();
            public void Set(K k, V v) => _t.AddOrUpdate(k, v);
            public bool Get(K k, out V v) => _t.TryGetValue(k, out v);
        }

        // la señal o la aguja bajo el ratón (en el último dibujo)
        public static RouteBoard.Sg SignalAt(RouteBoard b, Point p, float slack = 4)
        {
            if (b == null || !_sigBoxes.Get(b, out var l)) return null;
            RouteBoard.Sg best = null; float bd = float.MaxValue;
            foreach (var (sg, box) in l) { var r = RectangleF.Inflate(box, slack, slack); if (!r.Contains(p)) continue; float d = Sq(r, p); if (d < bd) { bd = d; best = sg; } }
            return best;
        }
        static readonly ConditionalWeak<RouteBoard, List<(RouteBoard.Tij t, RectangleF box)>> _tijBoxes = new();
        // (1.2.81) la tijera bajo el ratón (en el último dibujo)
        public static RouteBoard.Tij ScissorAt(RouteBoard b, Point p)
        {
            if (b == null || !_tijBoxes.Get(b, out var l)) return null;
            foreach (var (t, box) in l) if (box.Contains(p)) return t;
            return null;
        }
        public static RouteBoard.Sw SwitchAt(RouteBoard b, Point p, float slack = 6)
        {
            if (b == null || !_swBoxes.Get(b, out var l)) return null;
            RouteBoard.Sw best = null; float bd = float.MaxValue;
            foreach (var (sw, box) in l) { var r = RectangleF.Inflate(box, slack, slack); if (!r.Contains(p)) continue; float d = Sq(r, p); if (d < bd) { bd = d; best = sw; } }
            return best;
        }
        static float Sq(RectangleF r, Point p) { float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2; return (cx - p.X) * (cx - p.X) + (cy - p.Y) * (cy - p.Y); }

        // zoom en píxeles por metro
        public static void Draw(Graphics g, RouteBoard b, SchematicLive live, PointF origin, float zoom, Rectangle area)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using (var bg = new SolidBrush(SchematicDraw.Bg)) g.FillRectangle(bg, area);
            if (b == null) return;
            PointF P(PointF q) => new PointF(origin.X + q.X * zoom, origin.Y + q.Y * zoom);
            var view = RectangleF.Inflate(area, 60, 60);
            bool InView(PointF[] pts) { foreach (var q in pts) if (view.Contains(q)) return true; var bb = Bounds(pts); return bb.IntersectsWith(view); }
            float lw = Math.Max(1.6f, Math.Min(7f, 1.0f + zoom * 1.7f));
            bool detail = zoom >= 0.08f, near = zoom >= 0.45f, close = zoom >= 1.1f;

            // andenes (de cerca): una barra junto a su vía, del lado sin otra vía
            float ph = Math.Max(3f, Math.Min(10f, 3.2f * zoom));
            if (zoom >= 0.3f)
                using (var pb = new SolidBrush(SchematicDraw.PlatCol))
                    foreach (var p in b.Platforms)
                    {
                        var pc = b.Piece(p.E, p.A, p.B); if (pc == null) continue;
                        var sp = pc.Select(P).ToArray(); if (!InView(sp)) continue;
                        var off = Offset(sp, (lw / 2 + 1.5f + ph / 2) * p.Side);
                        if (off != null) using (var pen = new Pen(SchematicDraw.PlatCol, ph) { StartCap = LineCap.Flat, EndCap = LineCap.Flat }) g.DrawLines(pen, off);
                    }

            // vías: cada cantón de su color (de lejos, todas iguales y sin huecos)
            float gap = near ? Math.Max(1.2f, Math.Min(2.6f, 1.2f * zoom)) : 0;
            bool Over(List<(double a, double b)> l, double a, double bb) => l != null && l.Any(q => q.a < bb - 0.5 && a + 0.5 < q.b);
            var pens = new Dictionary<Color, Pen>();
            Pen PenOf(Color c) { if (!pens.TryGetValue(c, out var pn)) pens[c] = pn = new Pen(c, lw) { LineJoin = LineJoin.Round, StartCap = LineCap.Flat, EndCap = LineCap.Flat }; return pn; }
            foreach (var (e, a, bb) in b.Circuits)
            {
                var pc = b.Piece(e, a, bb); if (pc == null) continue;
                var sp = pc.Select(P).ToArray(); if (!InView(sp)) continue;
                var pts = gap > 0 ? Trim(sp, gap) : sp; if (pts == null || pts.Length < 2) continue;
                Color col = SchematicDraw.TrackCol;
                if (live != null && detail)
                {
                    live.Occupied.TryGetValue(e, out var oc);
                    if (Over(oc, a, bb)) col = SchematicDraw.Busy;
                    else if (live.Cancelling.TryGetValue(e, out var cl) && Over(cl, a, bb)) col = SchematicDraw.CancelCol;
                    else if (live.Route.TryGetValue(e, out var rl))
                    {
                        var hit = rl.Where(q => q.a < bb - 0.5 && a + 0.5 < q.b).ToList();
                        if (hit.Count > 0) col = hit.All(q => q.callOn) ? SchematicDraw.CallOnCol : SchematicDraw.RouteCol;
                    }
                }
                g.DrawLines(PenOf(col), pts);
            }
            foreach (var pn in pens.Values) pn.Dispose();
            // itinerarios en cola: encima, en discontinuo azul
            if (live != null && detail && live.Queued.Count > 0)
                using (var qp = new Pen(SchematicDraw.QueueColor, Math.Max(1.4f, lw - 1.5f)) { DashStyle = DashStyle.Dash })
                    foreach (var kv in live.Queued)
                        foreach (var (a, bb) in kv.Value) { var pc = b.Piece(kv.Key, a, bb); if (pc != null && pc.Length >= 2) g.DrawLines(qp, pc.Select(P).ToArray()); }
            // toperas
            if (near)
                using (var bp = new Pen(SchematicDraw.TrackCol, lw))
                    foreach (var q0 in b.Buffers) { var q = P(q0); if (view.Contains(q)) g.DrawEllipse(bp, q.X - 2, q.Y - 2, 4, 4); }

            // agujas: el lado que no está puesto, cortado junto a la aguja; su número de la estación (azul si está enclavada)
            var swBoxes = new List<(RouteBoard.Sw, RectangleF)>(); _swBoxes.Set(b, swBoxes);
            var numTaken = new List<RectangleF>();   // (1.2.81) los números de agujas ya puestos
            if (near)
            {
                using var nf = Theme.Font(Math.Max(6.4f, Math.Min(9f, 2.6f * zoom)));
                foreach (var sw in b.Switches)
                {
                    if (b.ScissorNodes.Contains(sw.Node)) continue;   // (1.2.81: van con su tijera)
                    var q = P(sw.At); if (!view.Contains(q)) continue;
                    int pos = live?.SwitchPos != null && sw.Order >= 0 ? live.SwitchPos(sw.Order) : -1;
                    // (1.2.81) la rama puesta marcada y la otra cortada (SchematicDraw.DrawSwitchState)
                    if (live?.SwitchPos != null)
                    {
                        bool lk = live.Locked != null && sw.Order >= 0 && live.Locked(sw.Order);
                        var l0 = sw.Legs[0]?.Select(P).ToArray(); var l1 = sw.Legs[1]?.Select(P).ToArray();
                        Color? lc = null;
                        if ((pos == 0 || pos == 1) && b.G.JunctionOf(sw.Node, out _, out _, out _, out var outs) && outs != null && pos < outs.Length && outs[pos] >= 0)
                            lc = SchematicDraw.LegLiveCol(live, outs[pos], b.G.Edges[outs[pos]].A == sw.Node, b.G.Edges[outs[pos]].Len);
                        SchematicDraw.DrawSwitchState(g, pos == 1 ? l1 : l0, pos == 1 ? l0 : l1, pos, lk, lw, sw.S?.Straight ?? -1, lc);
                        if (sw.Order >= 0 && live.Discord.Contains(sw.Order)) SchematicDraw.DrawDiscord(g, q, lw);
                    }
                    var hb = new RectangleF(q.X - 5, q.Y - 5, 10, 10);
                    if (zoom >= 0.8f && sw.Number > 0)
                    {
                        bool locked = live?.Locked != null && sw.Order >= 0 && live.Locked(sw.Order);
                        string t = sw.Label ?? sw.Number.ToString();
                        var sz = g.MeasureString(t, nf);
                        // del lado contrario a la rama desviada
                        var away = Away(sw);
                        // (1.2.81) donde no pise a otro número: más lejos de la aguja o del otro lado (con una raya fina hasta él)
                        RectangleF BoxAt(float sgn, float d) => new RectangleF(q.X + sgn * away.X * d - sz.Width / 2 - 1, q.Y + sgn * away.Y * d - sz.Height / 2, sz.Width + 2, sz.Height);
                        var box = BoxAt(1, lw + 8);
                        foreach (var (sgn, d) in new[] { (1f, lw + 8), (1f, lw + 20), (-1f, lw + 8), (-1f, lw + 20), (1f, lw + 34), (-1f, lw + 34) })
                        {
                            var cand = BoxAt(sgn, d); var inf = RectangleF.Inflate(cand, 1, 1);
                            if (numTaken.Any(r => r.IntersectsWith(inf))) continue;
                            box = cand;
                            if (d > lw + 8) using (var lp = new Pen(Color.FromArgb(120, 95, 105, 115), 1f)) g.DrawLine(lp, q.X, q.Y, box.X + box.Width / 2, box.Y + box.Height / 2);
                            break;
                        }
                        numTaken.Add(box);
                        using (var bb = new SolidBrush(Color.FromArgb(230, 22, 26, 32))) g.FillRectangle(bb, box);
                        using (var bp = new Pen(locked ? SchematicDraw.LockCol : Color.FromArgb(95, 105, 115), 1f)) g.DrawRectangle(bp, box.X, box.Y, box.Width, box.Height);
                        using (var tb = new SolidBrush(locked ? SchematicDraw.LockCol : Color.FromArgb(190, 196, 204))) g.DrawString(t, nf, tb, box.X + 1, box.Y);
                        hb = RectangleF.Union(hb, box);
                    }
                    swBoxes.Add((sw, hb));
                }
            }

            // (1.2.81, petición) las tijeras: sus cuatro vías sobre su trazado, lo puesto iluminado como en el esquema (paralela a
            // directa en blanco, diagonal en amarillo; con un tren, en rojo; con un itinerario, de su color), lo demás apagado; y su
            // nombre (T1…) en vez de los cuatro números
            var tijBoxes = new List<(RouteBoard.Tij, RectangleF)>(); _tijBoxes.Set(b, tijBoxes);
            if (near)
            {
                using var nf2 = Theme.Font(Math.Max(6.4f, Math.Min(9f, 2.6f * zoom)), FontStyle.Bold);
                foreach (var t in b.Scissors)
                {
                    var sp = t.S.Select(w => P(w.At)).ToArray();
                    if (!sp.Any(q => view.Contains(q))) continue;
                    int[] pos = t.S.Select(w => live?.SwitchPos != null && w.Order >= 0 ? live.SwitchPos(w.Order) : -1).ToArray();
                    bool known = pos.All(x => x >= 0);
                    bool Par(int i, int j) => known && pos[i] == t.StraightK[i] && pos[j] == t.StraightK[j];
                    bool Dia(int i, int j) => known && t.StraightK[i] >= 0 && t.StraightK[j] >= 0 && pos[i] == 1 - t.StraightK[i] && pos[j] == 1 - t.StraightK[j];
                    bool locked = live?.Locked != null && t.S.Any(w => w.Order >= 0 && live.Locked(w.Order));
                    var off = Color.FromArgb(62, 70, 80);
                    Color Col(int e, bool set, Color sc)
                    {
                        if (live != null && live.Occupied.TryGetValue(e, out var oc) && oc.Count > 0) return SchematicDraw.Busy;
                        if (live != null && live.Cancelling.TryGetValue(e, out var cn) && cn.Count > 0) return SchematicDraw.CancelCol;
                        if (live != null && live.Route.TryGetValue(e, out var rl) && rl.Count > 0) return rl.All(q => q.callOn) ? SchematicDraw.CallOnCol : SchematicDraw.RouteCol;
                        return set ? (locked ? SchematicDraw.LockCol : sc) : off;
                    }
                    var vias = new[] { (t.Edges[0], Par(0, 1), SchematicDraw.SwitchStraightCol), (t.Edges[1], Par(2, 3), SchematicDraw.SwitchStraightCol),
                                       (t.Edges[2], Dia(0, 3), SchematicDraw.SwitchDivergeCol), (t.Edges[3], Dia(2, 1), SchematicDraw.SwitchDivergeCol) };
                    foreach (bool lit in new[] { false, true })
                        foreach (var (e, set, sc) in vias)
                        {
                            if (e < 0) continue;
                            var col = live?.SwitchPos != null ? Col(e, set, sc) : SchematicDraw.TrackCol; bool on = col != off;
                            if (on != lit) continue;
                            var pc = b.Piece(e, 0, b.G.Edges[e].Len); if (pc == null || pc.Length < 2) continue;
                            using var pn = new Pen(col, on ? lw + 0.6f : lw) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                            g.DrawLines(pn, pc.Select(P).ToArray());
                        }
                    // (1.2.81) itinerario en cola por la tijera: el discontinuo azul, encima de ella
                    if (live != null && live.Queued.Count > 0)
                        using (var qp = new Pen(SchematicDraw.QueueColor, Math.Max(1.4f, lw - 1.5f)) { DashStyle = DashStyle.Dash })
                            foreach (var (e, _, _) in vias)
                            {
                                if (e < 0 || !live.Queued.TryGetValue(e, out var ql) || ql.Count == 0) continue;
                                if (live.Route.TryGetValue(e, out var rq) && rq.Count > 0) continue;
                                foreach (var (a, bb) in ql) { var pc = b.Piece(e, a, bb); if (pc != null && pc.Length >= 2) g.DrawLines(qp, pc.Select(P).ToArray()); }
                            }
                    var hb =RectangleF.Inflate(Bounds(sp), 6, 6);
                    if (zoom >= 0.8f && t.Name != null)
                    {
                        var sz = g.MeasureString(t.Name, nf2);
                        float cx = sp.Average(q => q.X), cy = sp.Min(q => q.Y);
                        var box = new RectangleF(cx - sz.Width / 2 - 1, cy - lw - 6 - sz.Height, sz.Width + 2, sz.Height);
                        for (int k = 0; k < 4 && numTaken.Any(r => r.IntersectsWith(RectangleF.Inflate(box, 1, 1))); k++) box.Y -= sz.Height + 3;
                        numTaken.Add(box);
                        using (var bb = new SolidBrush(Color.FromArgb(230, 22, 26, 32))) g.FillRectangle(bb, box);
                        using (var bp = new Pen(locked ? SchematicDraw.LockCol : !known && live?.SwitchPos != null ? SchematicDraw.SwitchUnknownCol : Color.FromArgb(95, 105, 115), 1f)) g.DrawRectangle(bp, box.X, box.Y, box.Width, box.Height);
                        using (var tb = new SolidBrush(locked ? SchematicDraw.LockCol : Color.FromArgb(190, 196, 204))) g.DrawString(t.Name, nf2, tb, box.X + 1, box.Y);
                        hb = RectangleF.Union(hb, box);
                    }
                    tijBoxes.Add((t, hb));
                }
            }

            // señales (de cerca): el mástil hacia su lado y la figura a lo largo de la vía; más de cerca, el número
            var sigBoxes = new List<(RouteBoard.Sg, RectangleF)>(); _sigBoxes.Set(b, sigBoxes);
            if (near)
            {
                float sk = Math.Max(0.8f, Math.Min(3.2f, zoom / 1.6f));
                float lr0 = 2.2f * sk;
                using var mp = new Pen(Color.FromArgb(150, 160, 170), 1.1f * Math.Min(2f, sk));
                using var op = new Pen(Color.FromArgb(220, 15, 17, 19), 1.1f);
                foreach (var sg in b.Signals)
                {
                    var q = P(sg.At); if (!view.Contains(q)) continue;
                    // girado: la vía hacia +x; si quedara boca abajo, media vuelta y la figura hacia −x
                    double ang = Math.Atan2(sg.Dir.Y, sg.Dir.X) * 180 / Math.PI; int face = 1;
                    if (ang > 90 || ang <= -90) { ang += ang > 90 ? -180 : 180; face = -1; }
                    double ar = ang * Math.PI / 180;
                    float ly = (float)(-Math.Sin(ar) * sg.SideV.X + Math.Cos(ar) * sg.SideV.Y);   // el lado de la señal en el giro
                    // (1.2.81) si su figura pisaría la de otra señal o un número de aguja, el mástil más largo (una fila más
                    // afuera, como las señales apiladas del esquema)
                    float side = ly < 0 ? -1 : 1, baseDy = lw / 2 + 6f * sk, stepDy = 5f * lr0 + 2;
                    int lvl = 0;
                    for (int L = 0; L < 4; L++)
                    {
                        var est = EstBox(q, ar, 3.5f * sk, side * (baseDy + L * stepDy), face, lr0);
                        if (!sigBoxes.Any(x => x.Item2.IntersectsWith(est)) && !numTaken.Any(r => r.IntersectsWith(est))) { lvl = L; break; }
                        if (L == 3) lvl = 0;
                    }
                    float dy = side * (baseDy + lvl * stepDy);
                    var st = g.Save();
                    g.TranslateTransform(q.X, q.Y); g.RotateTransform((float)ang);
                    float arm = 3.5f * sk;
                    g.DrawLine(mp, 0, (ly < 0 ? -1 : 1) * lw / 2, 0, dy);
                    g.DrawLine(mp, 0, dy, arm * face, dy);
                    RectangleF local;
                    var fig = live?.Figure?.Invoke(sg.S);
                    if (fig != null && close)
                    {
                        if (RoomMapDraw.Flashing(fig)) live.Blinking = true;
                        var fb = RoomMapDraw.DrawFigure(g, fig, new PointF(arm * face, dy), lr0, false, face);
                        local = fb;
                        if (fig.Pap != null)
                        {
                            float fs = 6.4f * Math.Min(2f, sk);
                            using var pf = Theme.Font(fs, FontStyle.Bold);
                            var psz = g.MeasureString(fig.Pap, pf);
                            float px = face > 0 ? fb.Right + 2 : fb.Left - 2 - psz.Width;
                            var ps = RoomMapDraw.DrawPap(g, fig, px, dy - psz.Height / 2, fs);
                            local = RectangleF.Union(local, new RectangleF(px, dy - psz.Height / 2, ps.Width, ps.Height));
                        }
                    }
                    else
                    {
                        int asp = live?.Aspect?.Invoke(sg.S) ?? -1;
                        float r = 2.6f * sk, lx = (arm + r) * face;
                        using (var lb = new SolidBrush(asp < 0 ? Color.FromArgb(70, 76, 84) : RoomMapDraw.AspectColor(asp))) g.FillEllipse(lb, lx - r, dy - r, 2 * r, 2 * r);
                        g.DrawEllipse(op, lx - r, dy - r, 2 * r, 2 * r);
                        local = new RectangleF(lx - r, dy - r, 2 * r, 2 * r);
                    }
                    if (zoom >= 2.2f && sg.S.Obj >= 0)
                    {
                        string t = sg.S.Obj.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        using var fi = Theme.Font(6.4f * Math.Min(1.6f, sk));
                        var sz = g.MeasureString(t, fi);
                        float tx = face > 0 ? local.Right + 1 : local.Left - sz.Width - 1;
                        using (var ib = new SolidBrush(Color.FromArgb(150, 160, 170))) g.DrawString(t, fi, ib, tx, dy - sz.Height / 2);
                        local = RectangleF.Union(local, new RectangleF(tx, dy - sz.Height / 2, sz.Width, sz.Height));
                    }
                    if (live != null && live.Flash.Contains(sg.S.Obj) && DateTime.Now.Millisecond < 600)
                        using (var fr = new Pen(Color.FromArgb(255, 213, 79), 2f)) g.DrawRectangle(fr, local.X - 3, local.Y - 3, local.Width + 6, local.Height + 6);
                    local = RectangleF.Union(local, new RectangleF(-2, Math.Min(0, dy), 4, Math.Abs(dy)));
                    var m = g.Transform; var corners = new[] { new PointF(local.Left, local.Top), new PointF(local.Right, local.Top), new PointF(local.Right, local.Bottom), new PointF(local.Left, local.Bottom) };
                    m.TransformPoints(corners); m.Dispose();
                    g.Restore(st);
                    sigBoxes.Add((sg, Bounds(corners)));
                    if (live?.SignalWhy?.Invoke(sg.S.Obj) != null) { var bx = Bounds(corners); RoomMapDraw.WhyBadge(g, bx.Left - 2, bx.Top - 2, Math.Max(0.9f, Math.Min(1.5f, zoom))); }   // (1.2.77)
                }
            }

            // número de vía de cada andén (de cerca), al principio de su barra
            if (zoom >= 0.6f)
                using (var f = Theme.Font(Math.Max(6.8f, Math.Min(12f, 3f * zoom)), FontStyle.Bold))
                using (var tagB = new SolidBrush(SchematicDraw.TagBg)) using (var tagT = new SolidBrush(SchematicDraw.TagTxt))
                using (var edge = new Pen(Color.FromArgb(70, 120, 180), 1f))
                {
                    var done = new HashSet<string>();
                    foreach (var p in b.Platforms)
                    {
                        if (p.Label == null) continue;
                        var pc = b.Piece(p.E, p.A, p.B); if (pc == null) continue;
                        var sp = pc.Select(P).ToArray(); if (!InView(sp)) continue;
                        var off = Offset(sp, (lw / 2 + 1.5f + ph / 2) * p.Side); if (off == null) continue;
                        var at = off[0];
                        string key = p.Station + "|" + p.Label + "|" + (int)(at.X / 40) + "," + (int)(at.Y / 40);
                        if (!done.Add(key)) continue;
                        var sz = g.MeasureString(p.Label, f);
                        var box = new RectangleF(at.X - sz.Width / 2 - 1, at.Y - sz.Height / 2, sz.Width + 2, sz.Height);
                        g.FillRectangle(tagB, box); g.DrawRectangle(edge, box.X, box.Y, box.Width, box.Height);
                        g.DrawString(p.Label, f, tagT, box.X + 1, box.Y);
                    }
                }

            // trenes: su cuerpo (naranja; los sueltos y de la IA, en gris) y su rótulo
            if (live != null)
                using (var f = Theme.Font(7.4f, FontStyle.Bold)) using (var fs = Theme.Font(6.8f))
                    foreach (var tr in live.Trains)
                    {
                        if (tr.body == null || tr.body.Length < 1) continue;
                        var col = tr.player ? SchematicDraw.Occ : tr.ai ? RoomAi.Color : SchematicDraw.AiCol;
                        var pts = tr.body.Select(P).ToArray();
                        if (!InView(pts)) continue;
                        if (pts.Length >= 2)
                        {
                            using (var pen = new Pen(Color.FromArgb(235, col), Math.Max(2.5f, lw - 1.5f)) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawLines(pen, pts);
                            SchematicDraw.DrawHeadArrow(g, pts, col, lw);   // (1.2.81) hacia dónde va
                        }
                        else using (var bb = new SolidBrush(col)) g.FillEllipse(bb, pts[0].X - 4, pts[0].Y - 4, 8, 8);
                        var mid = pts[pts.Length / 2];
                        var sz = g.MeasureString(tr.name, f);
                        var box = new RectangleF(mid.X - sz.Width / 2 - 3, mid.Y - 14 - sz.Height, sz.Width + 6, sz.Height);
                        using (var bb = new SolidBrush(Color.FromArgb(235, 26, 31, 38))) g.FillRectangle(bb, box);
                        using (var bp = new Pen(col, 1f)) g.DrawRectangle(bp, box.X, box.Y, box.Width, box.Height);
                        using (var tb = new SolidBrush(col)) g.DrawString(tr.name, f, tb, box.X + 3, box.Y);
                        if (near && !string.IsNullOrEmpty(tr.sub)) { var s2 = g.MeasureString(tr.sub, fs); using var sb = new SolidBrush(SchematicDraw.Txt); g.DrawString(tr.sub, fs, sb, mid.X - s2.Width / 2, mid.Y + 7); }
                    }

            // estaciones: de lejos, su nombre junto a un punto; de cerca, en su recuadro encima de los andenes (como ADIF)
            using (var sf = Theme.Font(near ? Math.Min(13f, 7.5f + zoom * 1.6f) : 8.2f, FontStyle.Bold))
            using (var sb = new SolidBrush(SchematicDraw.StationCol))
            using (var fr = new Pen(Color.FromArgb(120, SchematicDraw.StationCol), 1.2f))
            using (var bb = new SolidBrush(Color.FromArgb(225, 18, 21, 26)))
            {
                var taken = new List<RectangleF>();
                var labels = new List<(string, RectangleF)>(); _stBoxes.Set(b, labels);
                foreach (var st in b.Stations.OrderByDescending(s => s.box.Width + s.box.Height))
                {
                    var c = P(st.at); if (!view.Contains(c)) continue;
                    string t = near ? st.name.ToUpperInvariant() : st.name;
                    var sz = g.MeasureString(t, sf);
                    RectangleF r;
                    if (near)
                    {
                        float top = P(new PointF(st.box.Left, st.box.Top)).Y;
                        r = new RectangleF(c.X - sz.Width / 2 - 8, Math.Min(top, c.Y) - 34 - sz.Height, sz.Width + 16, sz.Height + 6);
                    }
                    else r = new RectangleF(c.X + 6, c.Y - sz.Height / 2, sz.Width, sz.Height);
                    if (taken.Any(q => q.IntersectsWith(r))) continue;
                    taken.Add(r); labels.Add((st.name, r));
                    if (!near) { g.FillRectangle(sb, c.X - 3.5f, c.Y - 3.5f, 7, 7); g.FillRectangle(bb, r); g.DrawString(t, sf, sb, r.X, r.Y); }
                    else { g.FillRectangle(bb, r); g.DrawRectangle(fr, r.X, r.Y, r.Width, r.Height); g.DrawString(t, sf, sb, r.X + 8, r.Y + 3); }
                }
            }
        }

        // hacia dónde poner el número de una aguja: al lado contrario de sus ramas
        // (1.2.81) lo que ocupará en pantalla la figura de una señal (aproximado: 8 luces de largo y 5 de alto) con su mástil a dy
        static RectangleF EstBox(PointF q, double ar, float arm, float dy, int face, float lr)
        {
            float x0 = face > 0 ? arm : -arm - 8 * lr, x1 = face > 0 ? arm + 8 * lr : -arm, y0 = dy - 2.5f * lr, y1 = dy + 2.5f * lr;
            float c = (float)Math.Cos(ar), s = (float)Math.Sin(ar);
            var pts = new[] { (x0, y0), (x1, y0), (x1, y1), (x0, y1) }.Select(p => new PointF(q.X + p.Item1 * c - p.Item2 * s, q.Y + p.Item1 * s + p.Item2 * c)).ToArray();
            return Bounds(pts);
        }

        static PointF Away(RouteBoard.Sw sw)
        {
            float sx = 0, sy = 0; int n = 0;
            foreach (var leg in sw.Legs) if (leg != null && leg.Length >= 2) { sx += leg[^1].X - sw.At.X; sy += leg[^1].Y - sw.At.Y; n++; }
            if (n == 0) return new PointF(0, 1);
            float l = (float)Math.Sqrt(sx * sx + sy * sy); if (l < 1e-3) return new PointF(0, 1);
            // perpendicular a las ramas, hacia fuera
            float px = -sy / l, py = sx / l;
            if (sw.Legs[0] != null && sw.Legs[1] != null)
            {
                float dx = sw.Legs[1][^1].X - sw.Legs[0][^1].X, dy = sw.Legs[1][^1].Y - sw.Legs[0][^1].Y;   // hacia la desviada
                if (px * dx + py * dy > 0) { px = -px; py = -py; }
            }
            return new PointF(px, py);
        }

        static PointF[] Offset(PointF[] p, float d)
        {
            if (p.Length < 2) return null;
            var r = new PointF[p.Length];
            for (int i = 0; i < p.Length; i++)
            {
                var a = p[Math.Max(0, i - 1)]; var c = p[Math.Min(p.Length - 1, i + 1)];
                float dx = c.X - a.X, dy = c.Y - a.Y, l = (float)Math.Sqrt(dx * dx + dy * dy); if (l < 1e-4) { r[i] = p[i]; continue; }
                r[i] = new PointF(p[i].X - dy / l * d, p[i].Y + dx / l * d);
            }
            return r;
        }
        static RectangleF Bounds(PointF[] p) { float x0 = p.Min(q => q.X), y0 = p.Min(q => q.Y), x1 = p.Max(q => q.X), y1 = p.Max(q => q.Y); return RectangleF.FromLTRB(x0, y0, x1 + 1, y1 + 1); }
        static PointF[] Trim(PointF[] p, float gap)
        {
            float len = 0; for (int i = 1; i < p.Length; i++) len += Dist(p[i - 1], p[i]);
            if (len < 2 * gap + 1) return len >= 1.5f ? p : null;
            var l = new List<PointF>(); float acc = 0, a = gap, b = len - gap;
            l.Add(At(p, a));
            for (int i = 1; i < p.Length; i++) { acc += Dist(p[i - 1], p[i]); if (acc > a && acc < b) l.Add(p[i]); }
            l.Add(At(p, b));
            return l.ToArray();
        }
        static PointF At(PointF[] p, float d)
        {
            for (int i = 1; i < p.Length; i++) { float l = Dist(p[i - 1], p[i]); if (d <= l) return Lerp(p[i - 1], p[i], l > 0 ? d / l : 0); d -= l; }
            return p[^1];
        }
        static float Dist(PointF a, PointF b) => (float)Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
        static PointF Lerp(PointF a, PointF b, float t) { t = Math.Max(0, Math.Min(1, t)); return new PointF(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t); }
    }
}
