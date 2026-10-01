// Pestaña «Ruta»: mapa de la ruta elegida (vías y estaciones del .tdb, pasadas a lat/lon con la misma
// conversión que usa Open Rails para la posición del tren) con los usuarios de la comunidad SelectOR que
// circulan ahora por ella (mismo RouteID). Las posiciones llegan a saltos: cada conductor envía la suya
// cada 5 s y aquí se consultan cada 2,5 s. LiveSmoother recorre esas lecturas con unos segundos de
// retraso y a su ritmo real, así que cada tren avanza de forma continua y pegado a la vía.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SelectOR
{
    // Trazado de una ruta en una proyección local en metros (x hacia el este, y hacia el sur).
    public sealed class RouteMapData
    {
        public double Lat0, Lon0, Kx, Ky;
        public float[][] X, Y;                     // vías
        public float MinX, MinY, MaxX, MaxY;
        public double[] StLat, StLon; public string[] StName;
        public float[] StX, StY;
        public int[] StOrder;                      // estaciones de más a menos andenes (las grandes, primero)
        public int Points;

        // Niveles de detalle: el mismo trazado simplificado a Eps metros (y sin los tramos más cortos que
        // eso). Con poco zoom se dibuja un nivel grueso: en rutas grandes son decenas de veces menos puntos.
        public sealed class Level { public float Eps; public float[][] X, Y; public float[] Bb; public int Points; }
        public Level[] Levels;

        public Level LevelFor(double pxPerM)
        {
            for (int i = Levels.Length - 1; i > 0; i--) if (Levels[i].Eps * pxPerM <= 0.75) return Levels[i];
            return Levels[0];
        }

        const float Cell = 250f;                   // rejilla para buscar la vía más cercana
        Dictionary<long, List<(int p, int i)>> _grid;

        public static RouteMapData Build(List<double[]> segLat, List<double[]> segLon, List<(string name, double lat, double lon, int weight)> stations)
        {
            var d = new RouteMapData();
            double minLa = 90, maxLa = -90, minLo = 180, maxLo = -180;
            foreach (var la in segLat) foreach (var v in la) { minLa = Math.Min(minLa, v); maxLa = Math.Max(maxLa, v); }
            foreach (var lo in segLon) foreach (var v in lo) { minLo = Math.Min(minLo, v); maxLo = Math.Max(maxLo, v); }
            d.Lat0 = (minLa + maxLa) / 2; d.Lon0 = (minLo + maxLo) / 2;
            d.Ky = 111320.0; d.Kx = 111320.0 * Math.Cos(d.Lat0 * Math.PI / 180.0);
            int n = segLat.Count;
            d.X = new float[n][]; d.Y = new float[n][];
            d.MinX = d.MinY = float.MaxValue; d.MaxX = d.MaxY = float.MinValue;
            for (int s = 0; s < n; s++)
            {
                var la = segLat[s]; var lo = segLon[s];
                var xs = new float[la.Length]; var ys = new float[la.Length];
                for (int i = 0; i < la.Length; i++)
                {
                    var (x, y) = d.Proj(la[i], lo[i]); xs[i] = (float)x; ys[i] = (float)y;
                    d.MinX = Math.Min(d.MinX, xs[i]); d.MaxX = Math.Max(d.MaxX, xs[i]);
                    d.MinY = Math.Min(d.MinY, ys[i]); d.MaxY = Math.Max(d.MaxY, ys[i]);
                }
                d.X[s] = xs; d.Y[s] = ys; d.Points += xs.Length;
            }
            d.StLat = new double[stations.Count]; d.StLon = new double[stations.Count]; d.StName = new string[stations.Count];
            d.StX = new float[stations.Count]; d.StY = new float[stations.Count];
            for (int i = 0; i < stations.Count; i++)
            {
                d.StName[i] = stations[i].name; d.StLat[i] = stations[i].lat; d.StLon[i] = stations[i].lon;
                var (x, y) = d.Proj(stations[i].lat, stations[i].lon); d.StX[i] = (float)x; d.StY[i] = (float)y;
            }
            var order = new int[stations.Count];
            for (int i = 0; i < order.Length; i++) order[i] = i;
            Array.Sort(order, (a, b) => stations[a].weight != stations[b].weight ? stations[b].weight.CompareTo(stations[a].weight)
                                                                              : string.CompareOrdinal(stations[a].name, stations[b].name));
            d.StOrder = order;
            d.BuildGrid();
            d.BuildLevels();
            return d;
        }

        void BuildLevels()
        {
            var levels = new List<Level> { MakeLevel(0, X, Y) };
            foreach (float eps in new[] { 2f, 8f, 32f, 128f, 512f })
            {
                var prev = levels[^1];
                var xs = new List<float[]>(); var ys = new List<float[]>();
                for (int p = 0; p < prev.X.Length; p++)
                {
                    int b = p * 4;
                    if (Math.Max(prev.Bb[b + 2] - prev.Bb[b], prev.Bb[b + 3] - prev.Bb[b + 1]) < eps) continue;   // tramo más corto que el detalle
                    var (sx, sy) = Rdp(prev.X[p], prev.Y[p], eps);
                    xs.Add(sx); ys.Add(sy);
                }
                var lv = MakeLevel(eps, xs.ToArray(), ys.ToArray());
                if (lv.Points > prev.Points * 0.85 && levels.Count > 1) { levels[^1] = lv; continue; }   // apenas cambia: no compensa otro nivel
                levels.Add(lv);
            }
            Levels = levels.ToArray();
        }

        static Level MakeLevel(float eps, float[][] xs, float[][] ys)
        {
            var lv = new Level { Eps = eps, X = xs, Y = ys, Bb = new float[xs.Length * 4] };
            for (int p = 0; p < xs.Length; p++)
            {
                float a = float.MaxValue, b = float.MaxValue, c = float.MinValue, e = float.MinValue;
                for (int i = 0; i < xs[p].Length; i++) { a = Math.Min(a, xs[p][i]); c = Math.Max(c, xs[p][i]); b = Math.Min(b, ys[p][i]); e = Math.Max(e, ys[p][i]); }
                lv.Bb[p * 4] = a; lv.Bb[p * 4 + 1] = b; lv.Bb[p * 4 + 2] = c; lv.Bb[p * 4 + 3] = e;
                lv.Points += xs[p].Length;
            }
            return lv;
        }

        static (float[] x, float[] y) Rdp(float[] xs, float[] ys, float eps)
        {
            int n = xs.Length;
            if (n <= 2) return (xs, ys);
            var keep = new bool[n]; keep[0] = keep[n - 1] = true;
            var stack = new Stack<(int, int)>(); stack.Push((0, n - 1));
            double e2 = (double)eps * eps;
            while (stack.Count > 0)
            {
                var (a, b) = stack.Pop();
                double best = 0; int bi = -1;
                double ax = xs[a], ay = ys[a], dx = xs[b] - ax, dy = ys[b] - ay, len2 = dx * dx + dy * dy;
                for (int i = a + 1; i < b; i++)
                {
                    double px = xs[i] - ax, py = ys[i] - ay;
                    double t = len2 > 0 ? Math.Max(0, Math.Min(1, (px * dx + py * dy) / len2)) : 0;
                    double ex = px - t * dx, ey = py - t * dy, d = ex * ex + ey * ey;
                    if (d > best) { best = d; bi = i; }
                }
                if (bi >= 0 && best > e2) { keep[bi] = true; stack.Push((a, bi)); stack.Push((bi, b)); }
            }
            int k = 0; for (int i = 0; i < n; i++) if (keep[i]) k++;
            var rx = new float[k]; var ry = new float[k]; k = 0;
            for (int i = 0; i < n; i++) if (keep[i]) { rx[k] = xs[i]; ry[k] = ys[i]; k++; }
            return (rx, ry);
        }

        public (double x, double y) Proj(double lat, double lon) => ((lon - Lon0) * Kx, (Lat0 - lat) * Ky);
        public (double lat, double lon) Unproj(double x, double y) => (Lat0 - y / Ky, Lon0 + x / Kx);

        static long Key(int cx, int cy) => ((long)cx << 32) ^ (uint)cy;

        void BuildGrid()
        {
            _grid = new Dictionary<long, List<(int, int)>>();
            for (int p = 0; p < X.Length; p++)
                for (int i = 0; i + 1 < X[p].Length; i++)
                {
                    int x0 = (int)Math.Floor(Math.Min(X[p][i], X[p][i + 1]) / Cell), x1 = (int)Math.Floor(Math.Max(X[p][i], X[p][i + 1]) / Cell);
                    int y0 = (int)Math.Floor(Math.Min(Y[p][i], Y[p][i + 1]) / Cell), y1 = (int)Math.Floor(Math.Max(Y[p][i], Y[p][i + 1]) / Cell);
                    if ((x1 - x0 + 1) * (y1 - y0 + 1) > 400) continue;   // tramo absurdo: no se indexa
                    for (int cx = x0; cx <= x1; cx++)
                        for (int cy = y0; cy <= y1; cy++)
                        {
                            long k = Key(cx, cy);
                            if (!_grid.TryGetValue(k, out var l)) _grid[k] = l = new List<(int, int)>();
                            l.Add((p, i));
                        }
                }
        }

        // Punto de vía más cercano (a menos de maxM metros). Para que la marca no corte las curvas.
        public bool Snap(double lat, double lon, double maxM, out double sLat, out double sLon)
        {
            sLat = lat; sLon = lon;
            if (_grid == null) return false;
            var (x, y) = Proj(lat, lon);
            int gx = (int)Math.Floor(x / Cell), gy = (int)Math.Floor(y / Cell);
            double best = maxM * maxM, bx = 0, by = 0; bool hit = false;
            for (int cx = gx - 1; cx <= gx + 1; cx++)
                for (int cy = gy - 1; cy <= gy + 1; cy++)
                {
                    if (!_grid.TryGetValue(Key(cx, cy), out var l)) continue;
                    foreach (var (p, i) in l)
                    {
                        double ax = X[p][i], ay = Y[p][i], dx = X[p][i + 1] - ax, dy = Y[p][i + 1] - ay;
                        double len2 = dx * dx + dy * dy;
                        double t = len2 > 0 ? Math.Max(0, Math.Min(1, ((x - ax) * dx + (y - ay) * dy) / len2)) : 0;
                        double px = ax + t * dx, py = ay + t * dy, d2 = (px - x) * (px - x) + (py - y) * (py - y);
                        if (d2 < best) { best = d2; bx = px; by = py; hit = true; }
                    }
                }
            if (hit) (sLat, sLon) = Unproj(bx, by);
            return hit;
        }
    }

    // Posiciones en vivo suavizadas. Cada lectura trae su antigüedad (age_s), así que se sabe CUÁNDO
    // estuvo el tren en cada punto: el mapa enseña el pasado reciente (Delay) recorriendo esas muestras
    // a su ritmo real. Si faltan datos se prolonga el movimiento unos segundos y, al final, un filtro
    // corto absorbe cualquier corrección para que la marca nunca salte.
    public sealed class LiveSmoother
    {
        public const double Delay = 7.5;          // s por detrás del tiempo real (envío cada 5 s + consulta cada 2,5 s)
        const double MaxExtrap = 3.0, Ease = 0.18;

        sealed class Sample { public double T, Lat, Lon, Hdg, Speed; public bool HasHdg; }
        sealed class Track
        {
            public string Id, Name, Train, Company; public bool Me; public Color Color;
            public readonly List<Sample> S = new();
            public double DLat, DLon, DHdg, LastFrame; public bool Shown, HasHdg;
            public DateTime SeenUtc;
        }

        readonly Dictionary<string, Track> _t = new();
        readonly Dictionary<string, Color> _colors = new();
        readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        int _next;

        public int Count => _t.Count;
        public string MeName = "Tú";
        public static readonly Color MeColor = Color.FromArgb(120, 210, 150);

        public void Clear() { _t.Clear(); }

        public void Update(List<LiveRow> rows)
        {
            double now = _clock.Elapsed.TotalSeconds;
            var seen = new HashSet<string>();
            foreach (var r in rows)
            {
                if (string.IsNullOrEmpty(r.Id) || seen.Contains(r.Id)) continue;
                seen.Add(r.Id);
                if (!_t.TryGetValue(r.Id, out var t))
                {
                    t = new Track { Id = r.Id };
                    if (!_colors.TryGetValue(r.Id, out var c)) { c = LiveMates.Palette[_next++ % LiveMates.Palette.Length]; _colors[r.Id] = c; }
                    t.Color = c;
                    _t[r.Id] = t;
                }
                t.Me = r.IsMe;
                if (t.Me) t.Color = MeColor;
                t.Name = t.Me ? MeName : (string.IsNullOrWhiteSpace(r.Name) ? "?" : r.Name.Trim());
                t.Train = r.Train ?? ""; t.Company = r.Company; t.SeenUtc = DateTime.UtcNow;

                double ts = now - Math.Max(0, r.AgeS ?? 0);
                var last = t.S.Count > 0 ? t.S[^1] : null;
                if (last != null && ts <= last.T + 1.5) continue;   // la misma lectura otra vez (o una más vieja)
                var s = new Sample { T = ts, Lat = r.Lat, Lon = r.Lon, Speed = r.SpeedKmh, Hdg = r.Heading ?? 0, HasHdg = r.Heading.HasValue };
                // Salto grande (cambio de tren, teletransporte): se empieza de nuevo, sin recorrer el hueco.
                if (last != null && LiveMates.Haversine(last.Lat, last.Lon, s.Lat, s.Lon) > 3000) { t.S.Clear(); t.Shown = false; }
                t.S.Add(s);
            }
            foreach (var k in new List<string>(_t.Keys)) if (!seen.Contains(k)) _t.Remove(k);
        }

        // Sin respuesta del servidor: se mantienen hasta 30 s sin noticias.
        public void Expire()
        {
            var lim = DateTime.UtcNow.AddSeconds(-30);
            foreach (var k in new List<string>(_t.Keys)) if (_t[k].SeenUtc < lim) _t.Remove(k);
        }

        static double Turn(double a, double b) => ((b - a) % 360 + 540) % 360 - 180;

        // Dónde estaba el tren en el instante r (con las muestras que hay).
        static (double lat, double lon, double hdg, bool hasHdg, double speed) At(List<Sample> S, double r)
        {
            var a = S[0];
            if (S.Count == 1 || r <= a.T) return (a.Lat, a.Lon, a.Hdg, a.HasHdg, a.Speed);
            for (int i = 0; i + 1 < S.Count; i++)
            {
                Sample p = S[i], q = S[i + 1];
                if (r > q.T) continue;
                double u = (r - p.T) / Math.Max(0.001, q.T - p.T);
                double h = p.HasHdg && q.HasHdg ? (p.Hdg + Turn(p.Hdg, q.Hdg) * u + 360) % 360 : (q.HasHdg ? q.Hdg : p.Hdg);
                return (p.Lat + (q.Lat - p.Lat) * u, p.Lon + (q.Lon - p.Lon) * u, h, p.HasHdg || q.HasHdg, p.Speed + (q.Speed - p.Speed) * u);
            }
            // Más allá de la última lectura: se sigue un poco con la velocidad del último tramo.
            Sample l = S[^1], k = S[^2];
            double dt = l.T - k.T, ex = Math.Min(r - l.T, MaxExtrap);
            if (dt <= 0.5 || l.Speed < 1) return (l.Lat, l.Lon, l.Hdg, l.HasHdg, l.Speed);
            double f = ex / dt;
            return (l.Lat + (l.Lat - k.Lat) * f, l.Lon + (l.Lon - k.Lon) * f, l.Hdg, l.HasHdg, l.Speed);
        }

        // Posiciones del momento (en cada fotograma). snap: pega la marca a la vía (opcional).
        public List<LiveMarker> Current(RouteMapData snapTo)
        {
            var list = new List<LiveMarker>(_t.Count);
            if (_t.Count == 0) return list;
            double now = _clock.Elapsed.TotalSeconds, r = now - Delay;
            foreach (var t in _t.Values)
            {
                if (t.S.Count == 0) continue;
                var (la, lo, h, hasH, v) = At(t.S, r);
                if (snapTo != null && snapTo.Snap(la, lo, 35, out var sla, out var slo)) { la = sla; lo = slo; }
                // rumbo: si no viene en los datos, el del movimiento
                if (!hasH && t.Shown && LiveMates.Haversine(t.DLat, t.DLon, la, lo) > 0.5) { h = LiveMates.Bearing(t.DLat, t.DLon, la, lo); hasH = true; }
                double dt = t.Shown ? Math.Max(0, now - t.LastFrame) : 0;
                if (!t.Shown || LiveMates.Haversine(t.DLat, t.DLon, la, lo) > 2000) { t.DLat = la; t.DLon = lo; t.DHdg = h; t.Shown = true; }
                else
                {
                    double e = 1 - Math.Exp(-dt / Ease);
                    t.DLat += (la - t.DLat) * e; t.DLon += (lo - t.DLon) * e;
                    t.DHdg = (t.DHdg + Turn(t.DHdg, h) * e + 360) % 360;
                }
                if (hasH) t.HasHdg = true;
                t.LastFrame = now;
                // quitar lecturas que ya quedaron atrás (se guardan dos para interpolar)
                while (t.S.Count > 2 && t.S[1].T < r - 2) t.S.RemoveAt(0);
                list.Add(new LiveMarker
                {
                    Id = t.Id, Name = t.Name, Short = t.Me ? t.Name : LiveMates.ShortName(t.Name), Train = t.Train, Company = t.Company,
                    Lat = t.DLat, Lon = t.DLon, Heading = t.DHdg, HasHeading = t.HasHdg, SpeedKmh = v, Color = t.Color, Me = t.Me
                });
            }
            list.Sort((a, b) => a.Me != b.Me ? (a.Me ? -1 : 1) : string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
            return list;
        }
    }

    // El mapa: vías y estaciones (capa fija, pintada aparte y reutilizada mientras no cambie la vista)
    // + los trenes en vivo encima. Rueda: zoom al cursor · arrastrar: mover · doble clic: toda la ruta ·
    // clic en un tren o en la lista: seguirlo.
    public sealed class RouteLiveMap : Control
    {
        RouteMapData _d;
        public readonly LiveSmoother Live = new();
        string _status = "", _empty = "";
        double _cx, _cy, _s, _fitS; bool _fitted;
        // Capa fija (vías y estaciones): se pinta en segundo plano con la vista del momento y, mientras llega
        // la nueva, la anterior se desplaza o se escala al instante. Así arrastrar y hacer zoom nunca esperan.
        Bitmap _layer; double _lcx, _lcy, _ls; int _lw, _lh;
        bool _rendering; int _gen;
        // Mientras se gira la rueda no se pinta la capa nítida (GDI+ no deja dibujar a dos hilos a la vez y la
        // ventana tendría que esperar): se escala la que hay y, al parar ~150 ms, se pinta la buena.
        long _lastWheel;
        readonly Timer _settle = new() { Interval = 150 };
        const int Margin2 = 320;                     // la capa es más grande que la vista: mover no la repinta
        readonly Timer _anim = new() { Interval = 33 };
        string _follow; bool _drag; Point _last, _down; int _moved;
        List<(Rectangle hit, LiveMarker m)> _hits = new();
        Rectangle _legendBox;
        List<LiveMarker> _marks = new();

        static readonly Color Bg = Color.FromArgb(28, 30, 32);

        public RouteLiveMap()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                     | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = Bg; TabStop = true;
            _anim.Tick += (s, e) =>
            {
                if (!Visible || _d == null || Live.Count == 0) return;
                var f = FindForm(); if (f == null || f.WindowState == FormWindowState.Minimized) return;
                Invalidate();
            };
            _anim.Start();
            _settle.Tick += (s, e) => { _settle.Stop(); Invalidate(); };
            MouseEnter += (s, e) => { if (FindForm()?.ContainsFocus == true) Focus(); };
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _anim.Stop(); _anim.Dispose(); _settle.Stop(); _settle.Dispose(); _gen++; _layer?.Dispose(); _layer = null; }
            base.Dispose(disposing);
        }

        public RouteMapData Data => _d;

        public void SetData(RouteMapData d)
        {
            _d = d; _fitted = false; _follow = null; Live.Clear(); _gen++; DropLayer(); Invalidate();
        }

        // Texto de la etiqueta de arriba a la izquierda (cuántos hay / por qué no se ve a nadie).
        public string Status { get => _status; set { if (_status != value) { _status = value ?? ""; Invalidate(); } } }
        // Texto en el centro cuando aún no hay trazado («Cargando…»).
        public string EmptyText { get => _empty; set { _empty = value ?? ""; if (_d == null) Invalidate(); } }

        void DropLayer() { _layer?.Dispose(); _layer = null; }

        void Fit()
        {
            if (_d == null || Width < 20 || Height < 20) return;
            float w = Math.Max(1, _d.MaxX - _d.MinX), h = Math.Max(1, _d.MaxY - _d.MinY);
            _fitS = Math.Min((Width - 60) / w, (Height - 60) / h);
            if (_fitS <= 0) _fitS = 0.01;
            _s = _fitS; _cx = (_d.MinX + _d.MaxX) / 2; _cy = (_d.MinY + _d.MaxY) / 2;
            _fitted = true;
        }

        public void FitAll() { _follow = null; Fit(); Invalidate(); }

        PointF Scr(double x, double y) => new PointF((float)((x - _cx) * _s + Width / 2.0), (float)((y - _cy) * _s + Height / 2.0));
        PointF ScrLL(double lat, double lon) { var (x, y) = _d.Proj(lat, lon); return Scr(x, y); }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (_d != null && _fitted && _fitS > 0 && Width > 20 && Height > 20)
            {
                // si estaba en «toda la ruta», se vuelve a encajar al nuevo tamaño
                bool wasFit = Math.Abs(_s - _fitS) < _fitS * 0.001;
                float w = Math.Max(1, _d.MaxX - _d.MinX), h = Math.Max(1, _d.MaxY - _d.MinY);
                _fitS = Math.Max(0.0001, Math.Min((Width - 60) / w, (Height - 60) / h));
                if (wasFit) _s = _fitS;
            }
        }

        // ---------------- capa fija: vías y estaciones ----------------
        bool NeedsLayer()
        {
            if (_layer == null) return true;
            if (Math.Abs(_ls - _s) > _s * 1e-9) return true;
            if (_lw != Width + Margin2 * 2 || _lh != Height + Margin2 * 2) return true;
            return Math.Abs((_lcx - _cx) * _s) > Margin2 / 2 || Math.Abs((_lcy - _cy) * _s) > Margin2 / 2;
        }

        // Pide la capa para la vista actual. Solo hay un pintado en marcha; al acabar, si la vista ya cambió,
        // el siguiente repintado del control pide otra con la vista de ese momento.
        void RequestLayer()
        {
            if (_rendering || _d == null || Width < 20 || Height < 20) return;
            _rendering = true;
            var d = _d; double cx = _cx, cy = _cy, s = _s, fitS = _fitS;
            int W = Width + Margin2 * 2, H = Height + Margin2 * 2, gen = _gen;
            string family; float emPx;
            using (var f = Theme.Font(8.5f)) { family = f.FontFamily.Name; emPx = f.SizeInPoints * DeviceDpi / 72f; }
            Task.Run(() =>
            {
                Bitmap b = null;
                try { b = RenderLayer(d, cx, cy, s, fitS, W, H, family, emPx); } catch { b?.Dispose(); b = null; }
                return b;
            }).ContinueWith(t =>
            {
                var b = t.Result;
                try
                {
                    if (IsDisposed || !IsHandleCreated) { b?.Dispose(); return; }
                    BeginInvoke((Action)(() =>
                    {
                        _rendering = false;
                        if (gen == _gen && b != null && !IsDisposed)
                        {
                            var old = _layer; _layer = b; _lcx = cx; _lcy = cy; _ls = s; _lw = W; _lh = H; old?.Dispose();
                        }
                        else b?.Dispose();
                        Invalidate();
                    }));
                }
                catch { b?.Dispose(); }
            });
        }

        static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SizeF> _textSize = new();

        // Se puede llamar desde cualquier hilo: todo lo que usa lo crea aquí. Los nombres van en un único
        // trazado de texto (contorno + relleno: dos llamadas), no uno a uno: con cientos de estaciones es lo
        // que más pesaba.
        public static Bitmap RenderLayer(RouteMapData d, double cx, double cy, double s, double fitS, int W, int H, string family, float emPx)
        {
            var bmp = new Bitmap(W, H, PixelFormat.Format32bppPArgb);
            using var g = Graphics.FromImage(bmp);
            g.Clear(Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float hx = W / 2f, hy = H / 2f;
            double vx0 = cx - (hx + 10) / s, vx1 = cx + (hx + 10) / s, vy0 = cy - (hy + 10) / s, vy1 = cy + (hy + 10) / s;
            double z = Math.Log(Math.Max(1e-9, s / fitS), 2);
            float lw = (float)Math.Max(1.3, Math.Min(4, 1.3 + z * 0.4));

            // vías: el nivel de detalle que toca, solo lo que se ve, en trazos de ~1500 puntos (GDI+ bloquea a
            // los demás hilos mientras dibuja: con trozos pequeños, la ventana puede repintar entre uno y otro)
            var lv = d.LevelFor(s);
            using (var pen = new Pen(Color.FromArgb(112, 122, 132), lw) { LineJoin = LineJoin.Round })
            {
                var path = new GraphicsPath();
                for (int p = 0; p < lv.X.Length; p++)
                {
                    int b = p * 4;
                    if (lv.Bb[b + 2] < vx0 || lv.Bb[b] > vx1 || lv.Bb[b + 3] < vy0 || lv.Bb[b + 1] > vy1) continue;
                    var xs = lv.X[p]; var ys = lv.Y[p];
                    if (xs.Length < 2) continue;
                    var pts = new PointF[xs.Length];
                    for (int i = 0; i < xs.Length; i++) pts[i] = new PointF((float)((xs[i] - cx) * s + hx), (float)((ys[i] - cy) * s + hy));
                    path.StartFigure(); path.AddLines(pts);
                    if (path.PointCount >= 1500) { g.DrawPath(pen, path); path.Dispose(); path = new GraphicsPath(); }
                }
                if (path.PointCount > 0) g.DrawPath(pen, path);
                path.Dispose();
            }

            // estaciones: las grandes primero; si dos puntos caen casi encima, solo se dibuja uno
            float dr = (float)Math.Max(2.5, Math.Min(4.5, 2.5 + z * 0.35));
            float cell = dr * 2.6f;
            var used = new HashSet<long>();
            var shown = new List<(int i, PointF m)>();
            using (var dot = new SolidBrush(Color.FromArgb(232, 235, 237)))
            using (var ring = new Pen(Bg, 1.5f))
                foreach (int i in d.StOrder)
                {
                    float mx = (float)((d.StX[i] - cx) * s + hx), my = (float)((d.StY[i] - cy) * s + hy);
                    if (mx < -10 || my < -10 || mx > W + 10 || my > H + 10) continue;
                    if (!used.Add(((long)(int)Math.Floor(mx / cell) << 32) ^ (uint)(int)Math.Floor(my / cell))) continue;
                    g.FillEllipse(dot, mx - dr, my - dr, dr * 2, dr * 2); g.DrawEllipse(ring, mx - dr, my - dr, dr * 2, dr * 2);
                    shown.Add((i, new PointF(mx, my)));
                }

            // nombres donde quepan sin pisar otro (rejilla para comprobarlo rápido)
            const int GC = 96;
            var grid = new Dictionary<long, List<RectangleF>>();
            static long K(int a, int b) => ((long)a << 32) ^ (uint)b;
            bool Free(RectangleF r)
            {
                var q = RectangleF.Inflate(r, 3, 1);
                for (int gx = (int)Math.Floor(q.Left / GC); gx <= (int)Math.Floor(q.Right / GC); gx++)
                    for (int gy = (int)Math.Floor(q.Top / GC); gy <= (int)Math.Floor(q.Bottom / GC); gy++)
                        if (grid.TryGetValue(K(gx, gy), out var l)) foreach (var o in l) if (o.IntersectsWith(q)) return false;
                return true;
            }
            void Put(RectangleF r)
            {
                for (int gx = (int)Math.Floor(r.Left / GC); gx <= (int)Math.Floor(r.Right / GC); gx++)
                    for (int gy = (int)Math.Floor(r.Top / GC); gy <= (int)Math.Floor(r.Bottom / GC); gy++)
                    {
                        if (!grid.TryGetValue(K(gx, gy), out var l)) grid[K(gx, gy)] = l = new List<RectangleF>();
                        l.Add(r);
                    }
            }
            using var ff = new FontFamily(family);
            using var font = new Font(ff, emPx, FontStyle.Regular, GraphicsUnit.Pixel);
            using var sf = (StringFormat)StringFormat.GenericTypographic.Clone();
            sf.FormatFlags |= StringFormatFlags.NoWrap;
            float lineH = font.GetHeight(g);
            var texts = new List<GraphicsPath> { new GraphicsPath() };
            int labels = 0;
            foreach (var (i, m) in shown)
            {
                if (labels >= 450) break;
                string name = d.StName[i];
                if (string.IsNullOrWhiteSpace(name)) continue;
                var sz = _textSize.GetOrAdd(name + "" + emPx.ToString("0.00"), _ => { var ms = g.MeasureString(name, font, PointF.Empty, sf); return new SizeF(ms.Width, lineH); });
                RectangleF? at = null;
                foreach (var c in new[]
                {
                    new RectangleF(m.X + dr + 4, m.Y - sz.Height / 2f, sz.Width, sz.Height),
                    new RectangleF(m.X - dr - 4 - sz.Width, m.Y - sz.Height / 2f, sz.Width, sz.Height),
                    new RectangleF(m.X - sz.Width / 2f, m.Y - dr - 3 - sz.Height, sz.Width, sz.Height),
                    new RectangleF(m.X - sz.Width / 2f, m.Y + dr + 3, sz.Width, sz.Height)
                })
                    if (Free(c)) { at = c; break; }
                if (at == null) continue;
                Put(at.Value); labels++;
                if (labels % 40 == 0) texts.Add(new GraphicsPath());
                texts[^1].AddString(name, ff, (int)FontStyle.Regular, emPx, at.Value.Location, sf);
            }
            // contorno oscuro para que se lea encima de las vías, y el texto encima
            using (var halo = new Pen(Bg, 3f) { LineJoin = LineJoin.Round })
                foreach (var t in texts) if (t.PointCount > 0) g.DrawPath(halo, t);
            using (var tb = new SolidBrush(Color.FromArgb(190, 197, 204)))
                foreach (var t in texts) { if (t.PointCount > 0) g.FillPath(tb, t); t.Dispose(); }
            return bmp;
        }

        // La capa que haya, llevada a la vista actual (desplazada y, si cambió el zoom, escalada).
        void DrawLayer(Graphics g)
        {
            if (_layer == null) return;
            double k = _s / _ls;
            double left = (_lcx - _cx) * _s + Width / 2.0 - _lw / 2.0 * k, top = (_lcy - _cy) * _s + Height / 2.0 - _lh / 2.0 * k;
            if (Math.Abs(k - 1) < 1e-9) { g.DrawImageUnscaled(_layer, (int)Math.Round(left), (int)Math.Round(top)); return; }
            var st = g.Save();
            g.InterpolationMode = k > 1 ? InterpolationMode.Bilinear : InterpolationMode.Low;
            g.PixelOffsetMode = PixelOffsetMode.HighSpeed;
            g.CompositingQuality = CompositingQuality.HighSpeed;
            g.DrawImage(_layer, new RectangleF((float)left, (float)top, (float)(_lw * k), (float)(_lh * k)));
            g.Restore(st);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var rect = ClientRectangle;
            g.Clear(Bg);
            if (_d == null)
            {
                if (_empty.Length > 0)
                {
                    using var fe = Theme.Font(10f);
                    TextRenderer.DrawText(g, _empty, fe, rect, Color.FromArgb(150, 156, 162),
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
                }
                return;
            }
            if (!_fitted) Fit();
            if (!_fitted) return;

            _marks = Live.Current(_d);
            if (_follow != null)
            {
                var fm = _marks.Find(m => m.Id == _follow);
                if (fm == null) _follow = null;
                else { var (x, y) = _d.Proj(fm.Lat, fm.Lon); _cx = x; _cy = y; }
            }

            DrawLayer(g);
            if (NeedsLayer())
            {
                bool zooming = _layer != null && Math.Abs(_ls - _s) > _s * 1e-9 && Environment.TickCount64 - _lastWheel < 150;
                if (zooming) { _settle.Stop(); _settle.Start(); }
                else RequestLayer();
            }

            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (_follow != null)
            {
                var fm = _marks.Find(m => m.Id == _follow);
                if (fm != null)
                {
                    var p = ScrLL(fm.Lat, fm.Lon);
                    using var pen = new Pen(Color.FromArgb(150, 255, 255, 255), 1.6f);
                    g.DrawEllipse(pen, p.X - 17, p.Y - 17, 34, 34);
                }
            }
            LiveMapDraw.Draw(g, rect, ScrLL, _marks, 1.15f, true, _d.StLat, _d.StLon, _d.StName);
            _hits = LiveMapDraw.Legend(g, rect, null, null, _marks, out _legendBox, includeMe: false);

            DrawChip(g);
            DrawScale(g, rect);
        }

        void DrawChip(Graphics g)
        {
            if (_status.Length == 0) return;
            using var f = Theme.Font(8.5f, FontStyle.Bold);
            var sz = TextRenderer.MeasureText(g, _status, f, Size.Empty, TextFormatFlags.NoPadding);
            var rc = new Rectangle(10, 10, sz.Width + 30, sz.Height + 10);
            var st = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Theme.Round(rc, rc.Height / 2))
            {
                using (var b = new SolidBrush(Color.FromArgb(235, 36, 39, 42))) g.FillPath(b, path);
                using (var pen = new Pen(_marks.Count > 0 ? Color.FromArgb(76, 175, 80) : Color.FromArgb(70, 76, 82))) g.DrawPath(pen, path);
            }
            using (var b = new SolidBrush(_marks.Count > 0 ? Color.FromArgb(102, 197, 106) : Color.FromArgb(120, 126, 132)))
                g.FillEllipse(b, rc.X + 9, rc.Y + rc.Height / 2 - 4, 8, 8);
            g.Restore(st);
            TextRenderer.DrawText(g, _status, f, new Point(rc.X + 22, rc.Y + 5), Color.FromArgb(225, 240, 226), TextFormatFlags.NoPadding);
        }

        void DrawScale(Graphics g, Rectangle rect)
        {
            double want = 110 / _s, len = 50;
            foreach (var k in new double[] { 50, 100, 200, 500, 1000, 2000, 5000, 10000, 20000, 50000 }) if (k <= want) len = k;
            float px = (float)(len * _s), x = 14, y = rect.Bottom - 16;
            using (var pen = new Pen(Color.FromArgb(185, 192, 199), 2f))
                g.DrawLines(pen, new[] { new PointF(x, y - 5), new PointF(x, y), new PointF(x + px, y), new PointF(x + px, y - 5) });
            using var f = Theme.Font(8f);
            string t = len >= 1000 ? (len / 1000).ToString("0") + " km" : len.ToString("0") + " m";
            var sz = TextRenderer.MeasureText(g, t, f, Size.Empty, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, t, f, new Point((int)x + 4, (int)y - 6 - sz.Height), Color.FromArgb(185, 192, 199), TextFormatFlags.NoPadding);
            string hint = I18n.T("Rueda: zoom · arrastrar: mover · doble clic: toda la ruta · clic en un tren: seguirlo");
            var hs = TextRenderer.MeasureText(g, hint, f, Size.Empty, TextFormatFlags.NoPadding);
            if (hs.Width + px + 60 < rect.Width)
                TextRenderer.DrawText(g, hint, f, new Point(rect.Right - hs.Width - 12, rect.Bottom - hs.Height - 10), Color.FromArgb(125, 131, 136), TextFormatFlags.NoPadding);
        }

        // ---------------- ratón ----------------
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (_d == null || !_fitted) return;
            double f = Math.Pow(1.2, e.Delta / 120.0);
            double ns = Math.Max(_fitS * 0.6, Math.Min(4.0, _s * f));
            if (ns == _s) return;
            _lastWheel = Environment.TickCount64;
            if (_follow == null)
            {
                // el punto bajo el cursor no se mueve
                double wx = _cx + (e.X - Width / 2.0) / _s, wy = _cy + (e.Y - Height / 2.0) / _s;
                _cx = wx - (e.X - Width / 2.0) / ns; _cy = wy - (e.Y - Height / 2.0) / ns;
            }
            _s = ns; Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            Focus();
            _drag = true; _last = _down = e.Location; _moved = 0;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_drag || _d == null || !_fitted)
            {
                bool overRow = false;
                foreach (var h in _hits) if (h.hit.Contains(e.Location)) { overRow = true; break; }
                Cursor = overRow ? Cursors.Hand : Cursors.Default;
                return;
            }
            int dx = e.X - _last.X, dy = e.Y - _last.Y;
            _moved += Math.Abs(dx) + Math.Abs(dy);
            if (_moved > 4)
            {
                _follow = null; Cursor = Cursors.SizeAll;
                _cx -= dx / _s; _cy -= dy / _s; Invalidate();
            }
            _last = e.Location;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (!_drag) return;
            _drag = false; Cursor = Cursors.Default;
            if (_moved > 4 || _d == null) return;
            // clic: una fila de la lista o un tren del mapa → seguirlo
            string id = null;
            foreach (var h in _hits) if (h.hit.Contains(e.Location)) { id = h.m.Id; break; }
            if (id == null && !_legendBox.Contains(e.Location))
            {
                double best = 18 * 18;
                foreach (var m in _marks)
                {
                    var p = ScrLL(m.Lat, m.Lon);
                    double d2 = (p.X - e.X) * (p.X - e.X) + (p.Y - e.Y) * (p.Y - e.Y);
                    if (d2 < best) { best = d2; id = m.Id; }
                }
            }
            if (id == null) return;
            _follow = id;
            if (_s < 0.12) _s = Math.Min(4.0, Math.Max(_s, 0.12));
            Invalidate();
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (_legendBox.Contains(e.Location)) return;
            FitAll();
        }
    }
}
