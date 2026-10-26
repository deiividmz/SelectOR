// HORARIO DE LA SALA (1.2.77): el mapa de la ruta a la derecha de la tabla. Cada servicio de la lista, con su recorrido
// (origen → destino por la red de la ruta, como «Probar camino») del mismo color que su nombre en la tabla; el elegido,
// más grueso y encuadrado, y los demás más apagados. Encima, los trenes IA que circulan ahora por la sala (cuando
// SelectOR está conectado a ella), del color de su servicio.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SelectOR
{
    public sealed partial class HorarioSalaForm
    {
        readonly Func<Task<RouteMapData>> _mapData;
        readonly Func<List<(string name, double lat, double lon, double kmh)>> _aiTrains;
        RouteLiveMap _map;
        Timer _aiTimer;
        List<(string name, double lat, double lon, double kmh)> _ai = new();
        readonly Dictionary<string, List<(double lat, double lon)>> _paths = new(StringComparer.CurrentCultureIgnoreCase);   // "origen|destino" → recorrido (null: sin vía)
        int _pathsGen;

        static readonly Color[] Palette =
        {
            Color.FromArgb(79, 195, 247), Color.FromArgb(255, 183, 77), Color.FromArgb(129, 199, 132), Color.FromArgb(240, 98, 146),
            Color.FromArgb(179, 136, 255), Color.FromArgb(255, 241, 118), Color.FromArgb(77, 208, 225), Color.FromArgb(255, 138, 101),
            Color.FromArgb(174, 213, 129), Color.FromArgb(149, 117, 205), Color.FromArgb(244, 143, 177), Color.FromArgb(144, 202, 249)
        };
        static Color ColorOf(int i) => Palette[((i % Palette.Length) + Palette.Length) % Palette.Length];

        void BuildMap(Panel body)
        {
            var gap = new Panel { Dock = DockStyle.Right, Width = Theme.Px(12), BackColor = Theme.Bg };
            _map = new RouteLiveMap { Dock = DockStyle.Right, Width = Theme.Px(560) };
            _map.EmptyText = Tr("Cargando el mapa de la ruta…");
            _map.Overlay = DrawOverlay;
            body.Controls.Add(gap); body.Controls.Add(_map);
            gap.SendToBack(); _map.SendToBack();   // (el mapa a la derecha del todo; las tarjetas ocupan el resto)
            body.Resize += (s, e) => _map.Width = Math.Max(Theme.Px(380), (int)(body.ClientSize.Width * 0.45));
            _cards.SelectedChanged += FocusSelected;
            if (_aiTrains != null)
            {
                _aiTimer = new Timer { Interval = 2000 };
                _aiTimer.Tick += (s, e) => RefreshAi();
                _aiTimer.Start();
                FormClosed += (s, e) => { _aiTimer.Stop(); _aiTimer.Dispose(); };
            }
        }

        async Task LoadMapAsync()
        {
            if (_mapData == null) { _map.EmptyText = Tr("Sin mapa de la ruta."); return; }
            RouteMapData d = null;
            try { d = await _mapData(); } catch { d = null; }
            if (IsDisposed) return;
            if (d == null) { _map.EmptyText = Tr("No se ha podido leer el mapa de la ruta."); return; }
            _map.SetData(d);
            UpdateMapStatus();
            FocusSelected();
        }

        // Recorridos de los servicios (en segundo plano: la red de CGL es grande).
        async void ServicesChanged()
        {
            if (_map == null) return;
            int gen = ++_pathsGen;
            UpdateMapStatus(); _map.Invalidate();
            await EnsureLists();
            if (IsDisposed || gen != _pathsGen || _g == null) return;
            var g = _g;
            var want = _services.Select(s => (s.Origin, s.Destination)).Distinct().Where(p => !_paths.ContainsKey(p.Origin + "|" + p.Destination)).ToList();
            if (want.Count > 0)
            {
                var got = await Task.Run(() => want.Select(p => (key: p.Origin + "|" + p.Destination, pts: PathPoints(g, p.Origin, p.Destination))).ToList());
                if (IsDisposed) return;
                foreach (var (key, pts) in got) _paths[key] = pts;
            }
            if (gen != _pathsGen) return;
            UpdateMapStatus();
            FocusSelected();
            _map.Invalidate();
        }

        List<(double lat, double lon)> PathOf(Service s) => _paths.TryGetValue(s.Origin + "|" + s.Destination, out var p) ? p : null;

        void FocusSelected()
        {
            if (_map?.Data == null) return;
            int i = _cards.SelectedIndex;
            var p = i >= 0 && i < _services.Count ? PathOf(_services[i]) : null;
            if (p != null && p.Count > 1) _map.FitLL(p);
            else if (i < 0) _map.FitAll();
            else _map.Invalidate();
        }

        void RefreshAi()
        {
            try { _ai = _aiTrains?.Invoke() ?? new List<(string, double, double, double)>(); } catch { _ai = new(); }
            UpdateMapStatus();
            _map.Invalidate();
        }

        void UpdateMapStatus()
        {
            if (_map == null) return;
            int sinVia = _services.Count(s => _paths.TryGetValue(s.Origin + "|" + s.Destination, out var p) && p == null);
            string st = string.Format(Tr("{0} servicios"), _services.Count);
            if (_aiTrains != null) st += " · " + (_ai.Count == 1 ? Tr("1 tren IA circulando") : string.Format(Tr("{0} trenes IA circulando"), _ai.Count));
            if (sinVia > 0) st += " · " + string.Format(Tr("{0} sin vía"), sinVia);
            _map.Status = st;
        }

        int ServiceIndexOf(string aiName)
        {
            string svc = RoomAi.Label(aiName);
            // (1.2.78) el despachador les pone detrás su hora de salida («C4: Sur-Orobón 09:30»): sin ella
            var m = System.Text.RegularExpressions.Regex.Match(svc, @"^(.*\S)\s+\d{1,2}:\d{2}$");
            string bare = m.Success ? m.Groups[1].Value : svc;
            int i = _services.FindIndex(s => string.Equals(s.Name.Replace('_', ' '), bare, StringComparison.CurrentCultureIgnoreCase));
            return i >= 0 ? i : _services.FindIndex(s => string.Equals(s.Name.Replace('_', ' '), svc, StringComparison.CurrentCultureIgnoreCase));
        }

        void DrawOverlay(Graphics g, Func<double, double, PointF> scr)
        {
            int sel = _cards.SelectedIndex;
            var order = Enumerable.Range(0, _services.Count).Where(i => i != sel).ToList();
            if (sel >= 0 && sel < _services.Count) order.Add(sel);   // (el elegido, encima)
            using var font = Theme.Font(8.25f, FontStyle.Bold);

            foreach (int i in order)
            {
                var s = _services[i]; var path = PathOf(s);
                if (path == null || path.Count < 2) continue;
                bool isSel = i == sel, dim = sel >= 0 && !isSel;
                var col = ColorOf(i);
                int a = !s.Active ? 90 : dim ? 95 : 235;
                var pts = path.Select(p => scr(p.lat, p.lon)).ToArray();
                if (isSel)
                {
                    using var halo = new Pen(Color.FromArgb(120, 0, 0, 0), 9f) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
                    g.DrawLines(halo, pts);
                }
                using (var pen = new Pen(Color.FromArgb(a, col), isSel ? 5.5f : 3f) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    if (!s.Active) pen.DashStyle = DashStyle.Dash;
                    g.DrawLines(pen, pts);
                }
                // origen (relleno) y destino (anillo)
                float r = isSel ? 6.5f : 4.5f;
                using (var fb = new SolidBrush(Color.FromArgb(a, col))) g.FillEllipse(fb, pts[0].X - r, pts[0].Y - r, 2 * r, 2 * r);
                using (var wp = new Pen(Color.FromArgb(a, 255, 255, 255), 1.5f)) g.DrawEllipse(wp, pts[0].X - r, pts[0].Y - r, 2 * r, 2 * r);
                var e = pts[pts.Length - 1];
                using (var bb = new SolidBrush(Color.FromArgb(a, 20, 22, 26))) g.FillEllipse(bb, e.X - r, e.Y - r, 2 * r, 2 * r);
                using (var cp = new Pen(Color.FromArgb(a, col), 2.5f)) g.DrawEllipse(cp, e.X - r, e.Y - r, 2 * r, 2 * r);
                // nombre del servicio, a lo largo de su recorrido (repartidos para que no caigan todos en el mismo sitio)
                if (!dim || isSel)
                {
                    var mid = pts[Math.Min(pts.Length - 1, (int)(pts.Length * (0.3 + 0.4 * ((i * 7) % 10) / 10.0)))];
                    Chip(g, font, s.Name, mid, Color.FromArgb(a, col), Color.FromArgb(20, 22, 26));
                }
                if (isSel)
                {
                    using var fs = Theme.Font(8.5f);
                    Tag(g, fs, s.Origin, pts[0]); Tag(g, fs, s.Destination, e);
                }
            }

            // trenes IA que circulan ahora
            foreach (var t in _ai)
            {
                int i = ServiceIndexOf(t.name);
                var col = i >= 0 ? ColorOf(i) : RoomAi.Color;
                bool dim = sel >= 0 && i != sel;
                var p = scr(t.lat, t.lon);
                float r = 7f;
                var dia = new[] { new PointF(p.X, p.Y - r), new PointF(p.X + r, p.Y), new PointF(p.X, p.Y + r), new PointF(p.X - r, p.Y) };
                using (var fb = new SolidBrush(Color.FromArgb(dim ? 130 : 255, col))) g.FillPolygon(fb, dia);
                using (var wp = new Pen(Color.White, 1.6f)) g.DrawPolygon(wp, dia);
                if (!dim)
                {
                    string label = RoomAi.Label(t.name) + (t.kmh >= 1 ? "  " + Math.Round(t.kmh) + " km/h" : "");
                    Chip(g, font, label, new PointF(p.X, p.Y - r - 12), Color.FromArgb(235, 28, 30, 34), col);
                }
            }
        }

        static void Chip(Graphics g, Font f, string text, PointF at, Color back, Color fore)
        {
            var sz = TextRenderer.MeasureText(g, text, f, Size.Empty, TextFormatFlags.NoPadding);
            var rc = new RectangleF(at.X - sz.Width / 2f - 6, at.Y - sz.Height / 2f - 3, sz.Width + 12, sz.Height + 6);
            using (var path = Rounded(rc, rc.Height / 2))
            using (var b = new SolidBrush(back)) g.FillPath(b, path);
            TextRenderer.DrawText(g, text, f, new Point((int)(rc.X + 6), (int)(rc.Y + 3)), fore, TextFormatFlags.NoPadding);
        }

        static void Tag(Graphics g, Font f, string text, PointF at)
        {
            var sz = TextRenderer.MeasureText(g, text, f, Size.Empty, TextFormatFlags.NoPadding);
            var pt = new Point((int)(at.X + 10), (int)(at.Y - sz.Height / 2f));
            using (var b = new SolidBrush(Color.FromArgb(200, 16, 18, 22))) g.FillRectangle(b, pt.X - 4, pt.Y - 2, sz.Width + 8, sz.Height + 4);
            TextRenderer.DrawText(g, text, f, pt, Color.FromArgb(235, 238, 240), TextFormatFlags.NoPadding);
        }

        static GraphicsPath Rounded(RectangleF r, float rad)
        {
            var p = new GraphicsPath(); float d = Math.Max(1, Math.Min(rad * 2, Math.Min(r.Width, r.Height)));
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure(); return p;
        }

        // ── el recorrido (lat/lon) por la red de la ruta: lo mismo que PathKm, pero guardando por dónde se llega.
        //    (Aproximado: no mira por qué lado se puede pasar cada aguja; el despachador busca el camino de verdad.)
        public static List<(double lat, double lon)> PathPoints(RouteGraph g, string from, string to)
        {
            if (g == null) return null;
            bool Is(string n, string want) => n != null && string.Equals(n.Trim(), want?.Trim(), StringComparison.CurrentCultureIgnoreCase);
            var dist = new Dictionary<int, double>(); var pq = new PriorityQueue<int, double>();
            var prev = new Dictionary<int, (int node, int edge)>();
            var seed = new Dictionary<int, (int edge, double off)>();
            void Seed(int n, double d, int e, double off)
            {
                if (n < 0) return;
                if (!dist.TryGetValue(n, out double o) || d < o) { dist[n] = d; pq.Enqueue(n, d); seed[n] = (e, off); prev.Remove(n); }
            }
            var targets = new List<(int e, double off)>();
            for (int e = 0; e < g.Edges.Length; e++)
            {
                var E = g.Edges[e];
                foreach (var (off, n) in E.Platforms.Concat(E.Sidings))
                {
                    if (Is(n, from)) { Seed(E.A, off, e, off); Seed(E.B, E.Len - off, e, off); }
                    if (Is(n, to)) targets.Add((e, off));
                }
            }
            double best = double.MaxValue; int bestNode = -1, bestEdge = -1; double bestOff = 0;
            (int e, double o1, double o2)? same = null;
            for (int e = 0; e < g.Edges.Length; e++)
            {
                var E = g.Edges[e];
                foreach (var (o1, n1) in E.Platforms.Concat(E.Sidings))
                    if (Is(n1, from))
                        foreach (var (_, o2) in targets.Where(t => t.e == e))
                            if (Math.Abs(o2 - o1) < best) { best = Math.Abs(o2 - o1); same = (e, o1, o2); }
            }
            while (pq.TryDequeue(out int n, out double dn))
            {
                if (dn > dist[n] + 1e-6 || dn > best) continue;
                foreach (int e in g.EdgesAt(n))
                {
                    var E = g.Edges[e];
                    foreach (var (te, off) in targets)
                        if (te == e)
                        {
                            double c = dn + (E.A == n ? off : E.Len - off);
                            if (c < best) { best = c; bestNode = n; bestEdge = e; bestOff = off; same = null; }
                        }
                    int m = E.A == n ? E.B : E.A; if (m < 0 || m == n) continue;
                    double nd = dn + E.Len;
                    if (!dist.TryGetValue(m, out double om) || nd < om) { dist[m] = nd; pq.Enqueue(m, nd); prev[m] = (n, e); seed.Remove(m); }
                }
            }
            if (best == double.MaxValue) return null;
            var outPts = new List<(double lat, double lon)>();
            void Add(int e, double a, double b) { foreach (var (x, y) in Slice(g.Edges[e], a, b)) outPts.Add(g.Unproj(x, y)); }
            if (same != null) { Add(same.Value.e, same.Value.o1, same.Value.o2); return outPts.Count > 1 ? outPts : null; }
            // de atrás hacia delante: el último tramo, los enteros y el primero
            var parts = new List<(int e, double a, double b)>();
            var Et = g.Edges[bestEdge];
            parts.Add((bestEdge, Et.A == bestNode ? 0 : Et.Len, bestOff));
            int cur = bestNode, guard = 0;
            while (prev.TryGetValue(cur, out var pv) && guard++ < 100000)
            {
                var E = g.Edges[pv.edge];
                parts.Add((pv.edge, E.A == pv.node ? 0 : E.Len, E.A == pv.node ? E.Len : 0));
                cur = pv.node;
            }
            if (seed.TryGetValue(cur, out var sd))
            {
                var E = g.Edges[sd.edge];
                parts.Add((sd.edge, sd.off, E.A == cur ? 0 : E.Len));
            }
            parts.Reverse();
            foreach (var (e, a, b) in parts) Add(e, a, b);
            return outPts.Count > 1 ? outPts : null;
        }

        // Los vértices de un tramo entre dos distancias desde su inicio (en el sentido de a hacia b).
        static IEnumerable<(double x, double y)> Slice(RouteGraph.Edge E, double a, double b)
        {
            if (E.X == null || E.X.Length == 0) yield break;
            if (E.X.Length == 1 || E.Cum == null) { yield return (E.X[0], E.Y[0]); yield break; }
            (double x, double y) At(double d)
            {
                d = Math.Max(0, Math.Min(E.Len, d));
                for (int i = 0; i + 1 < E.X.Length; i++)
                    if (d <= E.Cum[i + 1] || i + 2 == E.X.Length)
                    {
                        double seg = E.Cum[i + 1] - E.Cum[i], t = seg > 1e-9 ? (d - E.Cum[i]) / seg : 0;
                        t = Math.Max(0, Math.Min(1, t));
                        return (E.X[i] + (E.X[i + 1] - E.X[i]) * t, E.Y[i] + (E.Y[i + 1] - E.Y[i]) * t);
                    }
                return (E.X[E.X.Length - 1], E.Y[E.Y.Length - 1]);
            }
            yield return At(a);
            if (a <= b) { for (int i = 0; i < E.X.Length; i++) if (E.Cum[i] > a && E.Cum[i] < b) yield return (E.X[i], E.Y[i]); }
            else { for (int i = E.X.Length - 1; i >= 0; i--) if (E.Cum[i] < a && E.Cum[i] > b) yield return (E.X[i], E.Y[i]); }
            yield return At(b);
        }
    }
}
