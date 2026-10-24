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
    public sealed class RouteBoard
    {
        public RouteGraph G; public RoomMapGeo Geo;
        public List<(int e, double a, double b)> Circuits = new();
        public sealed class Sw { public RouteGraph.SwitchGeo S; public int Node, Order; public PointF At; public PointF[][] Legs = new PointF[2][]; public int Number; public string Station; }
        public sealed class Sg { public RoomMapGeo.Sig S; public PointF At; public PointF Dir; public PointF SideV; }   // Dir: hacia donde rige; SideV: hacia donde está la señal
        public sealed class Plat { public int E; public double A, B; public int Side; public string Label; public string Station; }
        public readonly List<Sw> Switches = new();
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
        sealed class Field { public double Cx, Cy, Ax, Ay, U0, U1, V0; public string Name; }
        readonly List<Field> _fields = new();
        public (double x, double y) Warp(double x, double y)
        {
            double sx = 0, sy = 0, sw = 0;
            foreach (var f in _fields)
            {
                double dx = x - f.Cx, dy = y - f.Cy, u = dx * f.Ax + dy * f.Ay, v = -dx * f.Ay + dy * f.Ax;
                const double Ramp = 260, Side = 220;
                double wu = u < f.U0 ? Smooth(1 - (f.U0 - u) / Ramp) : u > f.U1 ? Smooth(1 - (u - f.U1) / Ramp) : 1;
                if (wu <= 0) continue;
                double av = Math.Abs(v);
                if (av > f.V0 + Side) continue;
                // a lo ancho: dentro de la estación, × Spread; fuera, se aparta lo mismo que el borde (y se apaga lejos)
                double nv = av <= f.V0 ? av * Spread : f.V0 * Spread + (av - f.V0);
                double fade = av <= f.V0 ? 1 : Smooth(1 - (av - f.V0) / Side);
                double dv = (nv - av) * Math.Sign(v) * wu * fade;
                sx += -f.Ay * dv; sy += f.Ax * dv; sw += wu * fade;
            }
            if (sw > 1) { sx /= sw; sy /= sw; }
            return (x + sx, y + sy);
        }
        static double Smooth(double t) { t = Math.Max(0, Math.Min(1, t)); return t * t * (3 - 2 * t); }
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
                _fields.Add(new Field { Name = kv.Key, Cx = cx, Cy = cy, Ax = ax, Ay = ay, U0 = u0 - 120, U1 = u1 + 120, V0 = Math.Max(14, v0 + 10) });
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
                _fields.Add(new Field { Name = "agujas", Cx = cx, Cy = cy, Ax = ax, Ay = ay, U0 = u0 - 60, U1 = u1 + 60, V0 = Math.Max(10, v0 + 6) });
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
            }
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
            foreach (var sw in geo.Switches)
            {
                if (sw.Hidden) continue;
                var (ax, ay) = g.Proj(sw.Lat, sw.Lon);
                var w = new Sw { S = sw, Node = sw.Node, Order = sw.Order, At = b.W(ax, ay) };
                for (int k = 0; k < 2; k++)
                    if (sw.Branch[k] != null && sw.Branch[k].Count >= 2)
                        w.Legs[k] = sw.Branch[k].Select(q => { var (x, y) = g.Proj(q.lat, q.lon); return b.W(x, y); }).ToArray();
                var num = numberOf?.Invoke(sw.Node);
                if (num.HasValue) { w.Station = num.Value.st; w.Number = num.Value.n; }
                b.Switches.Add(w);
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
            return b;
        }

        // el lado del tramo e en el metro off sin otra vía cerca (+1 a la derecha del sentido A→B, −1 a la izquierda)
        int FreeSide(int e, double off)
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
            if (near)
            {
                using var cut = new Pen(SchematicDraw.Bg, lw + 2.2f) { StartCap = LineCap.Flat, EndCap = LineCap.Flat };
                using var nf = Theme.Font(Math.Max(6.4f, Math.Min(9f, 2.6f * zoom)));
                foreach (var sw in b.Switches)
                {
                    var q = P(sw.At); if (!view.Contains(q)) continue;
                    int pos = live?.SwitchPos != null && sw.Order >= 0 ? live.SwitchPos(sw.Order) : -1;
                    if (pos == 0 || pos == 1)
                    {
                        var leg = sw.Legs[1 - pos];
                        if (leg != null && leg.Length >= 2)
                        {
                            var sl = leg.Select(P).ToArray();
                            float L = 0; for (int i = 1; i < sl.Length; i++) L += Dist(sl[i - 1], sl[i]);
                            var c1 = At(sl, Math.Min(L * 0.6f, lw * 0.9f + 1.5f)); var c2 = At(sl, Math.Min(L * 0.9f, lw * 0.9f + 1.5f + Math.Max(5f, 2.2f * zoom)));
                            g.DrawLine(cut, c1, c2);
                        }
                    }
                    var hb = new RectangleF(q.X - 5, q.Y - 5, 10, 10);
                    if (zoom >= 0.8f && sw.Number > 0)
                    {
                        bool locked = live?.Locked != null && sw.Order >= 0 && live.Locked(sw.Order);
                        string t = sw.Number.ToString();
                        var sz = g.MeasureString(t, nf);
                        // del lado contrario a la rama desviada
                        var away = Away(sw);
                        var box = new RectangleF(q.X + away.X * (lw + 8) - sz.Width / 2 - 1, q.Y + away.Y * (lw + 8) - sz.Height / 2, sz.Width + 2, sz.Height);
                        using (var bb = new SolidBrush(Color.FromArgb(230, 22, 26, 32))) g.FillRectangle(bb, box);
                        using (var bp = new Pen(locked ? SchematicDraw.LockCol : Color.FromArgb(95, 105, 115), 1f)) g.DrawRectangle(bp, box.X, box.Y, box.Width, box.Height);
                        using (var tb = new SolidBrush(locked ? SchematicDraw.LockCol : Color.FromArgb(190, 196, 204))) g.DrawString(t, nf, tb, box.X + 1, box.Y);
                        hb = RectangleF.Union(hb, box);
                    }
                    swBoxes.Add((sw, hb));
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
                    float dy = (ly < 0 ? -1 : 1) * (lw / 2 + 6f * sk);
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
                        var col = tr.player ? SchematicDraw.Occ : SchematicDraw.AiCol;
                        var pts = tr.body.Select(P).ToArray();
                        if (!InView(pts)) continue;
                        if (pts.Length >= 2)
                            using (var pen = new Pen(Color.FromArgb(235, col), Math.Max(2.5f, lw - 1.5f)) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawLines(pen, pts);
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
