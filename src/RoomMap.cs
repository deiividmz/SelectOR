// Mapa grande en una sala de Servidores SelectOR, como el mapa de Open Rails (Ctrl+9): las agujas con su posición
// real (y un clic para cambiarlas), las señales con su aspecto (y su menú, si el despachador te ha nombrado ayudante)
// y los trenes de la IA. Lo de la ruta (RoomMapGeo) se prepara una vez; lo que cambia llega por la conexión aparte
// con la sala (SwitchPilot, servidor con «READY 4»; ver «SelectOR Servidor MP\RoomMap.cs»).
//  · Agujas: la foto del despachador (un byte por desvío, en el orden del .tdb: JunctionOrder) y cada cambio al momento.
//    La posición 0/1 es la rama Out[0]/Out[1]. Las que comparten número con otra (escapes) no se pueden pedir.
//  · Señales: un punto del color de su aspecto con una flecha hacia donde da paso. El estado de cada cabeza en el orden del multijugador (OrSignalMap: el de Open Rails); si el número de
//    cabezas no coincide con el de la ruta, no se pintan. El número de cada señal, para pedir un cambio, sale del
//    propio código de Open Rails; sin él (u otra versión rara), se ven pero no se pueden pedir.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Reflection;
using Orts.Formats.Msts;

namespace SelectOR
{
    public sealed class RoomMapGeo
    {
        public List<RouteGraph.SwitchGeo> Switches = new();
        public readonly Dictionary<(int tx, int tz, int id), RouteGraph.SwitchGeo> ById = new();   // el primero del .tdb (el que cambia Open Rails)
        public sealed class Head { public int Item, Obj = -1; public bool Normal; }
        public readonly List<Head> Heads = new();          // en el orden en que el despachador manda su estado
        public sealed class Sig { public int Obj = -1; public double Lat, Lon, Hdg; public int[] Heads; }
        public readonly List<Sig> Signals = new();         // las que tienen alguna cabeza principal (NORMAL)
        public bool CanControl;                            // numeración de Open Rails: se pueden pedir cambios
        public RouteGraph Graph;                           // (para el camino por delante de cada tren)
        public readonly Dictionary<int, int> OrderOf = new();   // nodo del desvío → su posición en la foto de agujas
        public string SignalNote;                          // por qué no (para el registro)

        public static RoomMapGeo Build(RouteGraph g, string routeDir)
        {
            var geo = new RoomMapGeo();
            if (g == null) return geo;
            geo.Switches = g.SwitchGeometry();
            geo.Graph = g;
            for (int i = 0; i < g.JunctionOrder.Length; i++) geo.OrderOf[g.JunctionOrder[i]] = i;
            foreach (var s in geo.Switches.OrderBy(s => s.Node)) if (!s.Hidden) geo.ById.TryAdd((s.Tx, s.Tz, s.Id), s);
            try { BuildSignals(geo, g, routeDir); } catch (Exception ex) { geo.SignalNote = ex.Message; geo.Heads.Clear(); geo.Signals.Clear(); }
            return geo;
        }

        static void BuildSignals(RoomMapGeo geo, RouteGraph g, string routeDir)
        {
            var tdbPath = Directory.GetFiles(routeDir, "*.tdb").FirstOrDefault();
            if (tdbPath == null) { geo.SignalNote = "sin .tdb"; return; }
            var tdb = new TrackDatabaseFile(tdbPath);
            var tsec = TrackGeometry.Sections(routeDir);
            var items = tdb.TrackDB.TrItemTable; var nodes = tdb.TrackDB.TrackNodes;
            if (items == null || nodes == null) return;

            // cada elemento de señal, en qué tramo está (el primero que lo nombra, como Open Rails) y su posición en él
            var nodeOf = new Dictionary<int, (int node, int pos)>();
            for (int ni = 1; ni < nodes.Length; ni++)
            {
                var refs = nodes[ni]?.TrVectorNode?.TrItemRefs;
                if (refs == null || nodes[ni].TrEndNode) continue;
                for (int i = 0; i < refs.Length; i++)
                    if (refs[i] >= 0 && refs[i] < items.Length && items[refs[i]] is SignalItem && !nodeOf.ContainsKey(refs[i])) nodeOf[refs[i]] = (ni, i);
            }
            // el orden y el número de señal: con el código de Open Rails; si no, el orden por la cuenta del .tdb (sin números)
            string orDir = Path.GetDirectoryName(typeof(TrackDatabaseFile).Assembly.Location);
            var (res, err) = tsec == null ? (null, "sin tsection.dat") : OrSignalMap.Build(orDir, routeDir, tdbPath, tdb, tsec);
            if (res != null && res.Heads.Count > 0) { geo.CanControl = true; foreach (var (item, obj) in res.Heads) geo.Heads.Add(new Head { Item = item, Obj = obj }); }
            else
            {
                geo.SignalNote = err ?? "sin señales";
                foreach (var kv in nodeOf.OrderBy(kv => (long)kv.Key * 100000 + kv.Value.pos)) geo.Heads.Add(new Head { Item = kv.Key });
            }

            // qué cabezas son principales (función NORMAL en el sigcfg.dat)
            var types = LoadSignalTypes(routeDir);
            foreach (var h in geo.Heads)
            {
                string name = (items[h.Item] as SignalItem)?.SignalType;
                h.Normal = types == null || (name != null && types.TryGetValue(name, out var fn) && fn == "NORMAL");
            }

            // las señales: por su número (o, sin él, por dónde están y hacia dónde miran)
            var vecToEdge = new Dictionary<int, int>();
            for (int i = 0; i < g.Edges.Length; i++) vecToEdge[g.Edges[i].Vec] = i;
            var groups = new Dictionary<string, List<int>>();
            for (int k = 0; k < geo.Heads.Count; k++)
            {
                var h = geo.Heads[k];
                var si = items[h.Item] as SignalItem;
                string key = h.Obj >= 0 ? "o" + h.Obj : $"p{Math.Round(si.TileX * 2048.0 + si.X)}_{Math.Round(si.TileZ * 2048.0 + si.Z)}_{si.Direction}";
                if (!groups.TryGetValue(key, out var l)) groups[key] = l = new List<int>();
                l.Add(k);
            }
            foreach (var l in groups.Values)
            {
                if (!l.Any(k => geo.Heads[k].Normal)) continue;   // avanzadas, repetidoras, de velocidad…: no se pintan
                var first = geo.Heads[l[0]];
                var si = items[first.Item] as SignalItem;
                if (!OrGeo.TryLatLon(si.TileX * 2048.0 + si.X, si.TileZ * 2048.0 + si.Z, out double la, out double lo)) continue;
                double hdg = double.NaN;
                // sentido de la marcha al que da paso: el de su tramo (A→B) si Direction = 0; el contrario si no
                if (nodeOf.TryGetValue(first.Item, out var np) && vecToEdge.TryGetValue(np.node, out int ei) && g.Nearest(la, lo, 40, out int e2, out double off, out _) && e2 == ei)
                {
                    var (x1, y1) = g.PointAt(ei, Math.Max(0, off - 3)); var (x2, y2) = g.PointAt(ei, Math.Min(g.Edges[ei].Len, off + 3));
                    hdg = Math.Atan2(y2 - y1, x2 - x1);
                    if (si.Direction != 0) hdg += Math.PI;
                    var (px, py) = g.PointAt(ei, off); (la, lo) = g.Unproj(px, py);   // sobre la vía
                }
                geo.Signals.Add(new Sig { Obj = first.Obj, Lat = la, Lon = lo, Hdg = hdg, Heads = l.ToArray() });
            }
        }

        // sigcfg.dat (el de OpenRails\ si lo hay): tipo de señal → función (NORMAL, DISTANCE, SPEED…). null si no se lee.
        static Dictionary<string, string> LoadSignalTypes(string routeDir)
        {
            try
            {
                string p = Path.Combine(routeDir, "OpenRails", "sigcfg.dat"); bool orts = File.Exists(p);
                if (!orts) p = Path.Combine(routeDir, "sigcfg.dat");
                if (!File.Exists(p)) return null;
                var t = typeof(SignalConfigurationFile);
                object cfg = t.GetConstructor(new[] { typeof(string), typeof(bool) })?.Invoke(new object[] { p, orts }) ?? Activator.CreateInstance(t, p);
                if (Prop(cfg, "SignalTypes") is not IDictionary d) return null;
                var r = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (DictionaryEntry de in d)
                {
                    object fn = Prop(de.Value, "Function") ?? Prop(de.Value, "FnType");
                    r[de.Key.ToString()] = (Prop(fn, "MstsFunction") ?? fn)?.ToString() ?? "";
                }
                return r;
            }
            catch { return null; }
        }

        static object Prop(object o, string n)
        {
            if (o == null) return null;
            const BindingFlags bf = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var t = o.GetType();
            return t.GetProperty(n, bf)?.GetValue(o) ?? t.GetField(n, bf)?.GetValue(o);
        }
    }

    // Lo que hay que dibujar ahora (lo prepara la ventana principal en cada repintado).
    public sealed class RoomMapView
    {
        public RoomMapGeo Geo;
        public Func<RouteGraph.SwitchGeo, int> SwitchPos;   // -1 = no se sabe
        public Func<RouteGraph.SwitchGeo, char> SwitchLock; // enclavada en Open Rails: S señal abierta · T tren · O tren encima; '.' libre
        public bool LocksKnown;                            // se sabe cuáles están enclavadas (agente del despachador)
        public Func<int, int> HeadState;                   // estado de una cabeza (MstsSignalAspect), -1 = no se sabe
        public bool SignalsOk;                             // la foto de señales cuadra con la ruta
        public List<(string name, double lat, double lon, double kmh)> AiTrains = new();
        // El camino por delante de cada tren (como Ctrl+9), del color del tren; X donde lo corta una aguja en contra.
        public List<(Color col, List<(double lat, double lon)> pts, (double lat, double lon)? against)> Paths = new();
    }

    public static class RoomMapDraw
    {
        public const double MaxMPerPx = 6;   // más lejos, sin agujas ni señales (no cabrían)

        static readonly Color SwOn = Color.FromArgb(245, 247, 248), SwOff = Color.FromArgb(90, 150, 158, 166), SwHidden = Color.FromArgb(120, 150, 158, 166);
        public static readonly Color SwLocked = Color.FromArgb(0, 176, 255);    // aguja enclavada (azul vivo: los andenes son de otro azul)

        public static bool IsLocked(RoomMapView v, RouteGraph.SwitchGeo s) { char c = v?.SwitchLock?.Invoke(s) ?? '.'; return c == 'S' || c == 'T' || c == 'O'; }

        // Rebase autorizado (parada y siga, rebase: aspectos 1 y 2): en el mapa, la señal media roja (arriba) y media blanca (abajo).
        public static bool IsCallOn(int st) => st == 1 || st == 2;
        public static readonly Color CallOnWhite = Color.FromArgb(240, 242, 244);

        public static Color AspectColor(int st) => st switch
        {
            0 => Color.FromArgb(239, 83, 80),                       // parada
            1 or 2 => Color.FromArgb(255, 138, 101),                 // parada y siga · rebase
            3 or 4 or 5 => Color.FromArgb(255, 213, 79),             // anuncio
            6 or 7 => Color.FromArgb(102, 187, 106),                 // vía libre
            _ => Color.FromArgb(150, 158, 166)
        };

        // El aspecto de una señal: el menos restrictivo de sus cabezas principales (como Open Rails, this_sig_lr).
        public static int SignalState(RoomMapView v, RoomMapGeo.Sig s)
        {
            int best = -1;
            foreach (int k in s.Heads)
            {
                if (!v.Geo.Heads[k].Normal) continue;
                int st = v.HeadState?.Invoke(k) ?? -1;
                if (st >= 0 && st < 8 && st > best) best = st;
            }
            return best;
        }

        static PointF SigMarker(RoomMapGeo.Sig s, Func<double, double, PointF> pj)
        {
            var p = pj(s.Lat, s.Lon);
            if (double.IsNaN(s.Hdg)) return new PointF(p.X, p.Y - 8);
            // a la derecha del sentido de la marcha (en la pantalla, y hacia abajo)
            float dx = (float)Math.Cos(s.Hdg), dy = (float)Math.Sin(s.Hdg);
            return new PointF(p.X - dy * 8, p.Y + dx * 8);
        }

        public static void Draw(Graphics g, Rectangle area, RoomMapView v, Func<double, double, PointF> pj, double mPerPx)
        {
            if (v?.Geo == null || pj == null) return;
            var clip = g.Clip; g.SetClip(area);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var view = RectangleF.Inflate(area, 30, 30);
            // el camino por delante de cada tren (discontinuo, de su color), debajo de agujas y señales
            foreach (var (col, pts, against) in v.Paths)
            {
                var sp = pts.Select(q => pj(q.lat, q.lon)).ToArray();
                if (sp.Length < 2) continue;
                using (var halo = new Pen(Color.FromArgb(200, 15, 17, 19), 7.5f) { LineJoin = LineJoin.Round, DashPattern = new[] { 2.2f, 0.95f } }) g.DrawLines(halo, sp);
                using (var pen = new Pen(Color.FromArgb(245, col), 4.4f) { DashPattern = new[] { 3.2f, 2.2f }, LineJoin = LineJoin.Round }) g.DrawLines(pen, sp);
                if (against is { } ag)
                {
                    var c = pj(ag.lat, ag.lon); float k = 7f;
                    using var xp = new Pen(col, 3f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                    g.DrawLine(xp, c.X - k, c.Y - k, c.X + k, c.Y + k); g.DrawLine(xp, c.X - k, c.Y + k, c.X + k, c.Y - k);
                }
            }
            if (mPerPx <= MaxMPerPx)
            {
                // agujas: la rama puesta, marcada; la otra, tenue
                foreach (var s in v.Geo.Switches)
                {
                    var c = pj(s.Lat, s.Lon);
                    if (!view.Contains(c)) continue;
                    int pos = s.Hidden ? -1 : v.SwitchPos?.Invoke(s) ?? -1;
                    bool locked = IsLocked(v, s);   // enclavada: la rama puesta y el punto, en azul, con un candado
                    for (int k = 0; k < 2; k++)
                    {
                        var br = s.Branch[k]; if (br == null || br.Count < 2) continue;
                        bool on = pos == k;
                        var pts = br.Select(q => pj(q.lat, q.lon)).ToArray();
                        // (enclavada: con borde oscuro, para que se lea también encima de un andén)
                        if (on && locked) using (var halo = new Pen(Color.FromArgb(220, 15, 17, 19), 6.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round }) g.DrawLines(halo, pts);
                        using var pen = new Pen(on ? (locked ? SwLocked : SwOn) : SwOff, on ? 3.2f : 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                        g.DrawLines(pen, pts);
                    }
                    float r = 4f;
                    var dot = locked ? SwLocked : s.Hidden ? SwHidden : pos < 0 ? Color.FromArgb(200, 150, 158, 166) : SwOn;
                    using (var b = new SolidBrush(dot)) g.FillEllipse(b, c.X - r, c.Y - r, 2 * r, 2 * r);
                    using (var o = new Pen(Color.FromArgb(200, 20, 22, 24), 1.2f)) g.DrawEllipse(o, c.X - r, c.Y - r, 2 * r, 2 * r);
                    if (locked) Padlock(g, c.X - 14, c.Y - 14);   // (a la izquierda: los nombres de estación van a la derecha)
                }
                // señales
                if (v.SignalsOk)
                    foreach (var s in v.Geo.Signals)
                    {
                        var p = pj(s.Lat, s.Lon); if (!view.Contains(p)) continue;
                        var m = SigMarker(s, pj);
                        using (var lp = new Pen(Color.FromArgb(150, 200, 204, 208), 1f)) g.DrawLine(lp, p, m);
                        float r = 5f;
                        int asp = SignalState(v, s);
                        bool callOn = IsCallOn(asp);   // rebase autorizado: rojo arriba y blanco abajo
                        var col = callOn ? AspectColor(0) : AspectColor(asp);
                        // flecha: hacia donde da paso (el sentido de la marcha de los trenes a los que manda)
                        if (!double.IsNaN(s.Hdg))
                        {
                            float dx = (float)Math.Cos(s.Hdg), dy = (float)Math.Sin(s.Hdg), w = r * 1.1f;
                            var tri = new[]
                            {
                                new PointF(m.X + dx * (r + 10), m.Y + dy * (r + 10)),
                                new PointF(m.X + dx * (r - 1) - dy * w, m.Y + dy * (r - 1) + dx * w),
                                new PointF(m.X + dx * (r - 1) + dy * w, m.Y + dy * (r - 1) - dx * w)
                            };
                            using (var tb = new SolidBrush(col)) g.FillPolygon(tb, tri);
                            using (var to = new Pen(Color.FromArgb(220, 15, 17, 19), 1.3f) { LineJoin = LineJoin.Round }) g.DrawPolygon(to, tri);
                        }
                        if (callOn)
                        {
                            using (var top = new SolidBrush(AspectColor(0))) g.FillPie(top, m.X - r, m.Y - r, 2 * r, 2 * r, 180, 180);
                            using (var bottom = new SolidBrush(CallOnWhite)) g.FillPie(bottom, m.X - r, m.Y - r, 2 * r, 2 * r, 0, 180);
                        }
                        else using (var b = new SolidBrush(col)) g.FillEllipse(b, m.X - r, m.Y - r, 2 * r, 2 * r);
                        using (var o = new Pen(Color.FromArgb(220, 15, 17, 19), 1.4f)) g.DrawEllipse(o, m.X - r, m.Y - r, 2 * r, 2 * r);
                    }
            }
            // trenes de la IA
            using (var f = Theme.Font(7.6f, FontStyle.Bold))
                foreach (var t in v.AiTrains)
                {
                    var p = pj(t.lat, t.lon); if (!view.Contains(p)) continue;
                    float r = 5f;
                    using (var b = new SolidBrush(Color.FromArgb(170, 176, 181))) g.FillEllipse(b, p.X - r, p.Y - r, 2 * r, 2 * r);
                    using (var o = new Pen(Color.FromArgb(40, 44, 48), 1.4f)) g.DrawEllipse(o, p.X - r, p.Y - r, 2 * r, 2 * r);
                    string lab = I18n.T("IA") + (t.kmh >= 1 ? $" · {t.kmh:0} km/h" : "");
                    System.Windows.Forms.TextRenderer.DrawText(g, lab, f, new Point((int)(p.X + 7), (int)(p.Y - 7)), Color.FromArgb(200, 205, 210), System.Windows.Forms.TextFormatFlags.NoPadding);
                }
            g.Clip = clip;
        }

        // Candado pequeño (aguja enclavada), con la esquina de arriba a la izquierda en (x, y).
        public static void Padlock(Graphics g, float x, float y)
        {
            using var dark = new Pen(Color.FromArgb(230, 15, 17, 19), 3.2f);
            using var arc = new Pen(SwLocked, 1.6f);
            var body = new RectangleF(x, y + 4.5f, 8, 6.5f);
            g.DrawArc(dark, x + 1.6f, y, 4.8f, 7, 180, 180);
            g.DrawArc(arc, x + 1.6f, y, 4.8f, 7, 180, 180);
            using (var o = new Pen(Color.FromArgb(230, 15, 17, 19), 1.2f)) using (var b = new SolidBrush(SwLocked))
            { g.FillRectangle(b, body); g.DrawRectangle(o, body.X, body.Y, body.Width, body.Height); }
        }

        // Lo que hay bajo el ratón: una aguja o una señal (null si nada).
        public static object Hit(RoomMapView v, Func<double, double, PointF> pj, double mPerPx, Point at)
        {
            if (v?.Geo == null || pj == null || mPerPx > MaxMPerPx) return null;
            object best = null; double bd = 10 * 10;
            if (v.SignalsOk)
                foreach (var s in v.Geo.Signals)
                {
                    var m = SigMarker(s, pj); double d = (m.X - at.X) * (m.X - at.X) + (m.Y - at.Y) * (m.Y - at.Y);
                    if (d < bd) { bd = d; best = s; }
                }
            foreach (var s in v.Geo.Switches)
            {
                var c = pj(s.Lat, s.Lon); double d = (c.X - at.X) * (c.X - at.X) + (c.Y - at.Y) * (c.Y - at.Y);
                if (d < bd) { bd = d; best = s; }
            }
            return best;
        }
    }
}
