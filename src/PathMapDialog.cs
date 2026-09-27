// Mapa 2D de la ruta (tipo Track Viewer): esquema de vías (TDB) + recorrido (.pat) + origen +
// nombres de estaciones. Zoom hacia el cursor y arrastre. Complemento independiente; no toca OR.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Orts.Formats.Msts;

namespace SelectOR
{
    public class PathMapDialog : Form
    {
        readonly string _patPath, _routeDir, _start, _end;
        readonly bool _routeOnly;
        readonly bool _overview;   // true = encuadra TODO el trayecto (plano general), no centrado en la salida
        MapPanel _map;
        Label _status;

        // Mapa GENERAL de la ruta (toda la red), sin un Path concreto.
        public PathMapDialog(string routeDir) : this(null, routeDir, "", "") { _routeOnly = true; }

        public PathMapDialog(string patPath, string routeDir, string start, string end, bool overview = false)
        {
            _patPath = patPath; _routeDir = routeDir; _start = start; _end = end; _overview = overview;
            Text = I18n.T("Mapa de la ruta");
            BackColor = Theme.Bg; ForeColor = Theme.Text;
            Font = Theme.Font(9.5f);
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(900, 720);
            MinimumSize = new Size(560, 480);
            try { Icon = Icon.ExtractAssociatedIcon((Environment.ProcessPath ?? AppContext.BaseDirectory)); } catch { }

            var stripe = new LiveryStripe { Dock = DockStyle.Top };
            var header = new Label { Text = "  " + I18n.T("Esquema de vías") + (string.IsNullOrEmpty(_end) ? "" : "  ·  " + _start + "  →  " + _end), Dock = DockStyle.Top, Height = 42, Font = Theme.Font(12f, FontStyle.Bold), ForeColor = Theme.Text, TextAlign = ContentAlignment.MiddleLeft, BackColor = Theme.Surface };
            _status = new Label { Dock = DockStyle.Bottom, Height = 26, ForeColor = Theme.Subtle, Padding = new Padding(14, 2, 0, 0), Text = I18n.T("Cargando esquema de vías…") };
            _map = new MapPanel { Dock = DockStyle.Fill, StartName = _start, EndName = _end, Overview = _overview };

            Controls.Add(_map);
            Controls.Add(_status);
            Controls.Add(header);
            Controls.Add(stripe);

            Load += (s, e) => LoadAsync();
        }

        void LoadAsync()
        {
            var patPath = _patPath; var routeDir = _routeDir;
            Task.Run(() =>
            {
                var network = new List<PointF[]>();
                var path = new List<PointF>();
                var junctions = new List<PointF>();
                var labels = new List<KeyValuePair<PointF, string>>();
                var edges = new List<(int a, int b, PointF[] pts)>();   // topología de vías para enrutar
                var nodePos = new Dictionary<int, PointF>();            // posición de empalmes/finales (vértices compartidos)
                try
                {
                    var tdb = Directory.GetFiles(routeDir, "*.tdb");
                    if (tdb.Length > 0)
                    {
                        var db = new TrackDatabaseFile(tdb[0]);
                        var tnodes = db.TrackDB.TrackNodes;
                        var tsec = TrackGeometry.Sections(routeDir);   // curvas como arcos (tsection.dat)
                        for (int ni = 0; ni < tnodes.Length; ni++)
                        {
                            var tn = tnodes[ni];
                            if (tn == null) continue;
                            // posición de empalmes/finales (vértices compartidos del grafo)
                            if (tn.UiD != null && (tn.TrJunctionNode != null || tn.TrEndNode))
                                nodePos[ni] = new PointF(tn.UiD.TileX * 2048f + tn.UiD.X, tn.UiD.TileZ * 2048f + tn.UiD.Z);
                            var vs = tn.TrVectorNode?.TrVectorSections;
                            if (vs == null || vs.Length < 1) continue;
                            var poly = TrackGeometry.NodePolyline(vs, tsec);
                            int aN = (tn.TrPins != null && tn.TrPins.Length >= 1) ? tn.TrPins[0].Link : -1;
                            int bN = (tn.TrPins != null && tn.TrPins.Length >= 2) ? tn.TrPins[1].Link : -1;
                            edges.Add((aN, bN, poly));
                        }
                        // Esquema de vías: extendemos cada tramo hasta la posición real de sus empalmes
                        // (los .tdb solo guardan el INICIO de cada sección, por eso quedaban cortes en los desvíos).
                        foreach (var (aN, bN, pts) in edges)
                        {
                            if (pts == null || pts.Length < 1) continue;
                            PointF pa = default, pb = default;
                            bool haveA = aN >= 0 && nodePos.TryGetValue(aN, out pa);
                            bool haveB = bN >= 0 && nodePos.TryGetValue(bN, out pb);
                            var joined = TrackGeometry.JoinEnds(pts, haveA, pa, haveB, pb);
                            if (joined.Length >= 2) network.Add(joined);
                        }
                        // nombres de estaciones (agrupando andenes por estación)
                        if (db.TrackDB.TrItemTable != null)
                        {
                            var byStation = new Dictionary<string, List<PointF>>(StringComparer.OrdinalIgnoreCase);
                            foreach (var it in db.TrackDB.TrItemTable)
                            {
                                if (it is PlatformItem pl && !string.IsNullOrWhiteSpace(pl.Station))
                                {
                                    var pos = new PointF(pl.TileX * 2048f + pl.X, pl.TileZ * 2048f + pl.Z);
                                    if (!byStation.TryGetValue(pl.Station, out var l)) byStation[pl.Station] = l = new List<PointF>();
                                    l.Add(pos);
                                }
                            }
                            foreach (var kv in byStation)
                            {
                                float ax = kv.Value.Average(p => p.X), az = kv.Value.Average(p => p.Y);
                                labels.Add(new KeyValuePair<PointF, string>(new PointF(ax, az), kv.Key.Trim()));
                            }
                        }
                    }
                }
                catch { }
                try
                {
                    if (_routeOnly || string.IsNullOrEmpty(patPath)) throw new Exception("route-only");
                    var pf = new PathFile(patPath);
                    var pdps = pf.TrackPDPs; var nodes = pf.TrPathNodes;
                    if (nodes != null && nodes.Count > 0)
                    {
                        uint idx = 0; int guard = 0;
                        while (idx != 0xffffffff && idx < nodes.Count && guard++ < 100000)
                        {
                            var n = nodes[(int)idx];
                            if (n.fromPDP < pdps.Count)
                            {
                                var p = pdps[(int)n.fromPDP];
                                path.Add(new PointF(p.TileX * 2048f + p.X, p.TileZ * 2048f + p.Z));
                                if (p.IsJunction) junctions.Add(path[path.Count - 1]);
                            }
                            idx = n.nextMainNode;
                        }
                    }
                    if (path.Count < 2) { path.Clear(); foreach (var p in pdps) path.Add(new PointF(p.TileX * 2048f + p.X, p.TileZ * 2048f + p.Z)); }
                }
                catch { }

                // Trazado siguiendo las vías reales (no línea recta): teje los PDP a través del
                // grafo de la red (vértices de las secciones) con Dijkstra. Si falla un tramo, recta.
                try
                {
                    if (edges.Count > 0 && path.Count >= 2)
                    {
                        var routed = RailRouter.Route(edges, nodePos, path);
                        if (routed != null && routed.Count >= 2) path = routed;
                    }
                }
                catch { }

                if (!IsHandleCreated) return;
                BeginInvoke((Action)(() =>
                {
                    _map.SetData(network, path, junctions, labels);
                    if (_routeOnly)
                        _status.Text = network.Count > 0
                            ? string.Format(I18n.T("{0} tramos · {1} estaciones · rueda: zoom al cursor · arrastra: mover"), network.Count, labels.Count)
                            : I18n.T("No se pudo leer el esquema de vías.");
                    else
                        _status.Text = network.Count > 0
                            ? string.Format(I18n.T("{0} tramos · {1} estaciones · recorrido de {2} puntos · rueda: zoom al cursor · arrastra: mover"), network.Count, labels.Count, path.Count)
                            : (path.Count >= 2 ? string.Format(I18n.T("Recorrido de {0} puntos (sin esquema de vías)"), path.Count) : I18n.T("No se pudo leer el trazado."));
                }));
            });
        }

        class MapPanel : Panel
        {
            List<PointF[]> _net; List<PointF> _path, _junc; List<KeyValuePair<PointF, string>> _labels;
            float _minX, _maxX, _minZ, _maxZ, _scale, _offX, _offY;
            bool _fitted; bool _drag; Point _last;
            public string StartName, EndName;
            public bool Overview;   // encuadra todo el trayecto en vez de centrar en la salida
            PointF _oPix, _dPix;

            public MapPanel()
            {
                DoubleBuffered = true;
                SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.Selectable, true);
                BackColor = Color.FromArgb(28, 30, 32);
                TabStop = true;
                MouseEnter += (s, e) => Focus();
                MouseWheel += OnWheel;
                MouseDown += (s, e) => { _drag = true; _last = e.Location; Cursor = Cursors.SizeAll; };
                MouseUp += (s, e) => { _drag = false; Cursor = Cursors.Default; };
                MouseMove += (s, e) => { if (_drag) { _offX += e.X - _last.X; _offY += e.Y - _last.Y; _last = e.Location; Invalidate(); } };
            }

            public void SetData(List<PointF[]> net, List<PointF> path, List<PointF> junc, List<KeyValuePair<PointF, string>> labels)
            { _net = net; _path = path; _junc = junc; _labels = labels; _fitted = false; Invalidate(); }

            void OnWheel(object s, MouseEventArgs e)
            {
                if (!_fitted) return;
                float f = e.Delta > 0 ? 1.2f : 1 / 1.2f;
                // mundo bajo el cursor antes
                float wx = _minX + (e.X - _offX) / _scale;
                float wz = _maxZ - (e.Y - _offY) / _scale;
                _scale = Math.Max(1e-4f, _scale * f);
                // recolocar para que ese mundo quede bajo el cursor
                _offX = e.X - (wx - _minX) * _scale;
                _offY = e.Y - (_maxZ - wz) * _scale;
                Invalidate();
            }

            void EnsureFit(Rectangle rect)
            {
                if (_fitted) return;
                _minX = float.MaxValue; _maxX = float.MinValue; _minZ = float.MaxValue; _maxZ = float.MinValue;
                Action<PointF> acc = p => { _minX = Math.Min(_minX, p.X); _maxX = Math.Max(_maxX, p.X); _minZ = Math.Min(_minZ, p.Y); _maxZ = Math.Max(_maxZ, p.Y); };
                if (_net != null && _net.Count > 0) foreach (var poly in _net) foreach (var p in poly) acc(p);
                else if (_path != null) foreach (var p in _path) acc(p);
                if (_minX > _maxX) { _minX = 0; _maxX = 1; _minZ = 0; _maxZ = 1; }
                float w = Math.Max(1, _maxX - _minX), h = Math.Max(1, _maxZ - _minZ);
                if (_path != null && _path.Count >= 1 && Overview)
                {
                    // PLANO GENERAL: encuadra TODO el trayecto del path (con margen).
                    float pMinX = float.MaxValue, pMaxX = float.MinValue, pMinZ = float.MaxValue, pMaxZ = float.MinValue;
                    foreach (var p in _path) { pMinX = Math.Min(pMinX, p.X); pMaxX = Math.Max(pMaxX, p.X); pMinZ = Math.Min(pMinZ, p.Y); pMaxZ = Math.Max(pMaxZ, p.Y); }
                    float pw = Math.Max(1, pMaxX - pMinX), ph = Math.Max(1, pMaxZ - pMinZ);
                    int pad = 48;
                    _scale = Math.Max(1e-4f, Math.Min((rect.Width - pad * 2) / pw, (rect.Height - pad * 2) / ph));
                    float cx = (pMinX + pMaxX) / 2f, cz = (pMinZ + pMaxZ) / 2f;
                    _offX = rect.X + rect.Width / 2f - (cx - _minX) * _scale;
                    _offY = rect.Y + rect.Height / 2f - (_maxZ - cz) * _scale;
                }
                else if (_path != null && _path.Count >= 1)
                {
                    // vista inicial centrada en la salida del tren, con zoom moderado
                    float diag = (float)Math.Sqrt(w * w + h * h);
                    float span = Math.Max(700f, Math.Min(3500f, diag));
                    _scale = Math.Min(rect.Width, rect.Height) / span;
                    var o = _path[0];
                    _offX = rect.X + rect.Width / 2f - (o.X - _minX) * _scale;
                    _offY = rect.Y + rect.Height / 2f - (_maxZ - o.Y) * _scale;
                }
                else
                {
                    int pad = 34;
                    _scale = Math.Min((rect.Width - pad * 2) / w, (rect.Height - pad * 2) / h);
                    _offX = rect.X + (rect.Width - w * _scale) / 2;
                    _offY = rect.Y + (rect.Height - h * _scale) / 2;
                }
                _fitted = true;
            }

            PointF Map(PointF p) => new PointF(_offX + (p.X - _minX) * _scale, _offY + (_maxZ - p.Y) * _scale);

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
                var rect = ClientRectangle;
                using (var b = new SolidBrush(BackColor)) g.FillRectangle(b, rect);
                if ((_net == null || _net.Count == 0) && (_path == null || _path.Count < 2)) return;
                EnsureFit(rect);

                if (_net != null)
                    using (var pen = new Pen(Color.FromArgb(110, 120, 128), 1f) { LineJoin = LineJoin.Round })
                        foreach (var poly in _net)
                        {
                            var sp = new PointF[poly.Length];
                            for (int i = 0; i < poly.Length; i++) sp[i] = Map(poly[i]);
                            g.DrawLines(pen, sp);
                        }

                if (_path != null && _path.Count >= 2)
                {
                    var sp = new PointF[_path.Count];
                    for (int i = 0; i < _path.Count; i++) sp[i] = Map(_path[i]);
                    using (var pen = new Pen(Theme.Accent, 3.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                        g.DrawLines(pen, sp);
                    var o = sp[0]; var d = sp[sp.Length - 1];
                    using (var db = new SolidBrush(Color.FromArgb(225, 95, 95))) g.FillEllipse(db, d.X - 6, d.Y - 6, 12, 12);
                    using (var glow = new GraphicsPath())
                    {
                        glow.AddEllipse(o.X - 16, o.Y - 16, 32, 32);
                        using (var pgb = new PathGradientBrush(glow) { CenterColor = Color.FromArgb(140, Theme.AccentHi), SurroundColors = new[] { Color.FromArgb(0, Theme.Accent) } })
                            g.FillPath(pgb, glow);
                    }
                    using (var ob = new SolidBrush(Theme.AccentHi)) g.FillEllipse(ob, o.X - 7, o.Y - 7, 14, 14);
                    using (var op = new Pen(Color.White, 2f)) g.DrawEllipse(op, o.X - 7, o.Y - 7, 14, 14);
                    _oPix = o; _dPix = d;
                }

                // nombres de estaciones (punto + texto). Se muestran siempre; el texto solo si hay zoom suficiente para no saturar.
                if (_labels != null)
                {
                    bool showText = _scale > 0.02f; // a poco zoom solo puntos
                    using (var f = Theme.Font(8f, FontStyle.Bold))
                    using (var dot = new SolidBrush(Color.FromArgb(255, 205, 90)))
                        foreach (var kv in _labels)
                        {
                            var m = Map(kv.Key);
                            if (m.X < rect.X - 40 || m.X > rect.Right + 40 || m.Y < rect.Y - 20 || m.Y > rect.Bottom + 20) continue;
                            g.FillEllipse(dot, m.X - 3, m.Y - 3, 6, 6);
                            if (showText)
                            {
                                var sz = TextRenderer.MeasureText(g, kv.Value, f);
                                var tr = new Rectangle((int)m.X + 6, (int)m.Y - sz.Height / 2, sz.Width + 6, sz.Height + 2);
                                Theme.FillRound(g, tr, 4, Color.FromArgb(180, 0, 0, 0));
                                TextRenderer.DrawText(g, kv.Value, f, tr, Color.FromArgb(255, 220, 130), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                            }
                        }
                }

                // etiquetas de ORIGEN (verde) y DESTINO (rojo) sobre sus puntos, siempre encima de todo
                if (_path != null && _path.Count >= 2)
                {
                    DrawMarkerLabel(g, _oPix, I18n.T("Origen"), StartName, Theme.AccentHi, true);
                    DrawMarkerLabel(g, _dPix, I18n.T("Destino"), EndName, Color.FromArgb(235, 105, 105), false);
                }
            }

            static void DrawMarkerLabel(Graphics g, PointF p, string tag, string name, Color accent, bool above)
            {
                string text = string.IsNullOrWhiteSpace(name) ? tag : tag + ": " + name.Trim();
                using (var f = Theme.Font(8.5f, FontStyle.Bold))
                {
                    var sz = TextRenderer.MeasureText(g, text, f);
                    int w = sz.Width + 16, h = sz.Height + 8;
                    int x = (int)p.X - w / 2;
                    int y = above ? (int)p.Y - 16 - h : (int)p.Y + 16;
                    var box = new Rectangle(x, y, w, h);
                    Theme.FillRound(g, box, 6, Color.FromArgb(225, 20, 22, 24));
                    Theme.DrawRoundBorder(g, box, 6, accent, 1.6f);
                    // punta triangular apuntando al punto
                    using (var b = new SolidBrush(Color.FromArgb(225, 20, 22, 24)))
                    {
                        int cx = (int)p.X, ty = above ? box.Bottom : box.Top;
                        var tri = above
                            ? new[] { new Point(cx - 5, ty), new Point(cx + 5, ty), new Point(cx, ty + 6) }
                            : new[] { new Point(cx - 5, ty), new Point(cx + 5, ty), new Point(cx, ty - 6) };
                        g.FillPolygon(b, tri);
                    }
                    using (var dotb = new SolidBrush(accent)) g.FillEllipse(dotb, box.X + 6, box.Y + h / 2 - 3, 6, 6);
                    var tr = new Rectangle(box.X + 14, box.Y, box.Width - 16, box.Height);
                    TextRenderer.DrawText(g, text, f, tr, Color.White, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                }
            }
        }
    }

    /// <summary>
    /// Enruta el recorrido por las vías reales: construye un grafo con todos los vértices de las
    /// secciones de vía (uniendo extremos coincidentes = desvíos/empalmes) y busca el camino más
    /// corto (Dijkstra) entre cada par de PDP consecutivos, de modo que la línea siga los raíles.
    /// </summary>
    static class RailRouter
    {
        const float Cell = 16f;
        static long Key(int cx, int cz) => ((long)cx << 32) ^ (uint)cz;
        static float D(PointF a, PointF b) { float dx = a.X - b.X, dz = a.Y - b.Y; return (float)Math.Sqrt(dx * dx + dz * dz); }

        static IEnumerable<int> Near(Dictionary<long, List<int>> grid, PointF p, float r)
        {
            int x0 = (int)Math.Floor((p.X - r) / Cell), x1 = (int)Math.Floor((p.X + r) / Cell);
            int z0 = (int)Math.Floor((p.Y - r) / Cell), z1 = (int)Math.Floor((p.Y + r) / Cell);
            for (int cx = x0; cx <= x1; cx++)
                for (int cz = z0; cz <= z1; cz++)
                    if (grid.TryGetValue(Key(cx, cz), out var l))
                        foreach (var i in l) yield return i;
        }

        public static List<PointF> Route(List<(int a, int b, PointF[] pts)> edges, Dictionary<int, PointF> nodePos, List<PointF> pdps)
        {
            var V = new List<PointF>();
            var adj = new List<List<int>>();
            var adjC = new List<List<float>>();
            var grid = new Dictionary<long, List<int>>();

            int Add(PointF p)
            {
                int i = V.Count; V.Add(p); adj.Add(new List<int>()); adjC.Add(new List<float>());
                var k = Key((int)Math.Floor(p.X / Cell), (int)Math.Floor(p.Y / Cell));
                if (!grid.TryGetValue(k, out var l)) grid[k] = l = new List<int>();
                l.Add(i); return i;
            }
            void Edge(int a, int b, float c) { adj[a].Add(b); adjC[a].Add(c); adj[b].Add(a); adjC[b].Add(c); }

            // vértices compartidos para empalmes/finales -> garantizan conectividad del grafo
            var nodeVertex = new Dictionary<int, int>();
            int NodeV(int idx)
            {
                if (idx < 0 || !nodePos.TryGetValue(idx, out var p)) return -1;
                if (!nodeVertex.TryGetValue(idx, out var v)) nodeVertex[idx] = v = Add(p);
                return v;
            }

            foreach (var (aN, bN, pts) in edges)
            {
                if (pts == null || pts.Length < 1) continue;
                int start = V.Count;
                for (int i = 0; i < pts.Length; i++) Add(pts[i]);
                for (int i = 0; i < pts.Length - 1; i++) Edge(start + i, start + i + 1, D(pts[i], pts[i + 1]));
                int first = start, last = start + pts.Length - 1;
                int va = NodeV(aN), vb = NodeV(bN);
                // conectar cada nodo compartido al extremo de la polilínea más cercano
                if (va >= 0 && vb >= 0)
                {
                    var pa = V[va];
                    bool aAtFirst = D(pa, pts[0]) <= D(pa, pts[pts.Length - 1]);
                    int ea = aAtFirst ? first : last, eb = aAtFirst ? last : first;
                    Edge(va, ea, D(V[va], V[ea]));
                    Edge(vb, eb, D(V[vb], V[eb]));
                }
                else if (va >= 0) { int e = D(V[va], pts[0]) <= D(V[va], pts[pts.Length - 1]) ? first : last; Edge(va, e, D(V[va], V[e])); }
                else if (vb >= 0) { int e = D(V[vb], pts[0]) <= D(V[vb], pts[pts.Length - 1]) ? first : last; Edge(vb, e, D(V[vb], V[e])); }
            }
            if (V.Count == 0) return pdps;

            int Nearest(PointF p)
            {
                for (float r = Cell; r <= Cell * 128; r *= 2f)
                {
                    int best = -1; float bd = float.MaxValue;
                    foreach (var nb in Near(grid, p, r)) { float d = D(V[nb], p); if (d < bd) { bd = d; best = nb; } }
                    if (best >= 0) return best;
                }
                return -1;
            }

            int n = V.Count;
            var dist = new float[n]; var prev = new int[n]; var gen = new int[n]; int curGen = 0;

            List<int> Dijkstra(int src, int dst)
            {
                curGen++;
                var heap = new MinHeap();
                dist[src] = 0; gen[src] = curGen; prev[src] = -1; heap.Push(src, 0);
                int guard = 0;
                while (heap.Count > 0 && guard++ < 4_000_000)
                {
                    heap.Pop(out int u, out float du);
                    if (gen[u] == curGen && du > dist[u]) continue;
                    if (u == dst) break;
                    var au = adj[u]; var ac = adjC[u];
                    for (int k = 0; k < au.Count; k++)
                    {
                        int v = au[k]; float nd = du + ac[k];
                        if (gen[v] != curGen || nd < dist[v]) { dist[v] = nd; gen[v] = curGen; prev[v] = u; heap.Push(v, nd); }
                    }
                }
                if (gen[dst] != curGen) return null;
                var outp = new List<int>();
                for (int v = dst; v != -1; v = prev[v]) outp.Add(v);
                outp.Reverse();
                return outp;
            }

            var result = new List<PointF>();
            void Emit(PointF p) { if (result.Count == 0 || D(result[result.Count - 1], p) > 0.05f) result.Add(p); }

            for (int s = 0; s < pdps.Count - 1; s++)
            {
                int a = Nearest(pdps[s]), b = Nearest(pdps[s + 1]);
                List<int> seg = (a >= 0 && b >= 0 && a != b) ? Dijkstra(a, b) : null;
                if (seg != null && seg.Count >= 2) foreach (var vi in seg) Emit(V[vi]);
                else { Emit(pdps[s]); Emit(pdps[s + 1]); }
            }
            return result.Count >= 2 ? result : pdps;
        }
    }

    /// <summary>Montículo binario mínimo (nodo, coste) para Dijkstra.</summary>
    class MinHeap
    {
        int[] _n = new int[64]; float[] _c = new float[64]; int _len;
        public int Count => _len;
        public void Push(int node, float cost)
        {
            if (_len == _n.Length) { Array.Resize(ref _n, _len * 2); Array.Resize(ref _c, _len * 2); }
            int i = _len++; _n[i] = node; _c[i] = cost;
            while (i > 0) { int p = (i - 1) / 2; if (_c[p] <= _c[i]) break; (_c[p], _c[i]) = (_c[i], _c[p]); (_n[p], _n[i]) = (_n[i], _n[p]); i = p; }
        }
        public void Pop(out int node, out float cost)
        {
            node = _n[0]; cost = _c[0]; _len--;
            _n[0] = _n[_len]; _c[0] = _c[_len]; int i = 0;
            while (true)
            {
                int l = 2 * i + 1, r = 2 * i + 2, m = i;
                if (l < _len && _c[l] < _c[m]) m = l;
                if (r < _len && _c[r] < _c[m]) m = r;
                if (m == i) break;
                (_c[m], _c[i]) = (_c[i], _c[m]); (_n[m], _n[i]) = (_n[i], _n[m]); i = m;
            }
        }
    }
}
