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
        public sealed class Head { public int Item, Obj = -1; public bool Normal; public HeadLook Look; }
        // (1.2.75) cómo es una cabeza (su tipo en el sigcfg.dat): sus luces, en su sitio y de su color, y qué enciende cada
        // estado de dibujo (el segundo byte de la foto de señales). Con esto se ven las figuras del RCF tal cual: anuncio de
        // precaución (verde y amarillo), amarillo a destellos, rebase autorizado, etc. Las pantallas de preanuncio (PAP)
        // son otra cabeza, en otro objeto junto a la señal: Sig.Paps.
        public sealed class HeadLook
        {
            public string Type;
            public Color[] Col = Array.Empty<Color>(); public float[] X = Array.Empty<float>(), Y = Array.Empty<float>();
            public (int lamp, bool flash)[][] States = Array.Empty<(int, bool)[]>();   // por número de estado de dibujo
            public string[] Names = Array.Empty<string>();
            public bool Pap; public int PapSpeed;   // pantalla de preanuncio; su velocidad si va en el tipo (pap50 → 50)
        }
        public readonly List<Head> Heads = new();          // en el orden en que el despachador manda su estado
        public sealed class Sig { public int Obj = -1; public double Lat, Lon, Hdg; public int[] Heads; public int Edge = -1; public double Off; public int[] Paps = Array.Empty<int>(); internal double Wx, Wz; internal int Node = -1, Dir; }   // (Edge/Off: su sitio en su propia vía; Paps: las cabezas de sus pantallas de preanuncio)
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

            // qué cabezas son principales (función NORMAL en el sigcfg.dat) y cómo es cada una
            var (types, looks) = LoadSignalTypes(routeDir);
            foreach (var h in geo.Heads)
            {
                string name = (items[h.Item] as SignalItem)?.SignalType;
                h.Normal = types == null || (name != null && types.TryGetValue(name, out var fn) && fn == "NORMAL");
                if (name != null && looks != null && looks.TryGetValue(name, out var lk)) h.Look = lk;
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
                // (sobre su propia vía, la del .tdb: con vías muy juntas, la más cercana puede ser la de al lado; 1.2.75)
                int sEdge = -1; double sOff = 0;
                if (nodeOf.TryGetValue(first.Item, out var np) && vecToEdge.TryGetValue(np.node, out int ei) && g.NearestOn(new[] { ei }, la, lo, 40, out _, out double off))
                {
                    var (x1, y1) = g.PointAt(ei, Math.Max(0, off - 3)); var (x2, y2) = g.PointAt(ei, Math.Min(g.Edges[ei].Len, off + 3));
                    hdg = Math.Atan2(y2 - y1, x2 - x1);
                    if (si.Direction != 0) hdg += Math.PI;
                    var (px, py) = g.PointAt(ei, off); (la, lo) = g.Unproj(px, py);   // sobre la vía
                    sEdge = ei; sOff = off;
                }
                geo.Signals.Add(new Sig { Obj = first.Obj, Lat = la, Lon = lo, Hdg = hdg, Heads = l.ToArray(), Edge = sEdge, Off = sOff,
                    Wx = si.TileX * 2048.0 + si.X, Wz = si.TileZ * 2048.0 + si.Z, Node = nodeOf.TryGetValue(first.Item, out var nq) ? nq.node : -1, Dir = (int)si.Direction });
            }

            // (1.2.75) pantallas de preanuncio: cada una, con la señal que tiene al lado (mismo sentido; en su misma vía si
            // puede ser) para pintar su número junto a ella
            for (int k = 0; k < geo.Heads.Count; k++)
            {
                var h = geo.Heads[k]; if (h.Look == null || !h.Look.Pap) continue;
                var si = items[h.Item] as SignalItem; if (si == null) continue;
                double wx = si.TileX * 2048.0 + si.X, wz = si.TileZ * 2048.0 + si.Z;
                int node = nodeOf.TryGetValue(h.Item, out var nq) ? nq.node : -1;
                Sig best = null; double bd = double.MaxValue;
                foreach (var sg in geo.Signals)
                {
                    if (sg.Dir != (int)si.Direction) continue;
                    double d = Math.Sqrt((sg.Wx - wx) * (sg.Wx - wx) + (sg.Wz - wz) * (sg.Wz - wz));
                    if (d > 25 || sg.Node != node && d > 12) continue;
                    if (sg.Node != node) d += 10;
                    if (d < bd) { bd = d; best = sg; }
                }
                if (best != null) best.Paps = best.Paps.Append(k).ToArray();   // (a veces dos: una con anuncio de parada y otra con el de precaución)
            }
        }

        // sigcfg.dat (el de OpenRails\ si lo hay): tipo de señal → función (NORMAL, DISTANCE, SPEED…) y su figura. null si no se lee.
        static (Dictionary<string, string> fn, Dictionary<string, HeadLook> looks) LoadSignalTypes(string routeDir)
        {
            try
            {
                string p = Path.Combine(routeDir, "OpenRails", "sigcfg.dat"); bool orts = File.Exists(p);
                if (!orts) p = Path.Combine(routeDir, "sigcfg.dat");
                if (!File.Exists(p)) return (null, null);
                var t = typeof(SignalConfigurationFile);
                object cfg = t.GetConstructor(new[] { typeof(string), typeof(bool) })?.Invoke(new object[] { p, orts }) ?? Activator.CreateInstance(t, p);
                if (Prop(cfg, "SignalTypes") is not IDictionary d) return (null, null);
                var r = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (DictionaryEntry de in d)
                {
                    object fn = Prop(de.Value, "Function") ?? Prop(de.Value, "FnType");
                    r[de.Key.ToString()] = (Prop(fn, "MstsFunction") ?? fn)?.ToString() ?? "";
                }
                Dictionary<string, HeadLook> looks = null;
                try { looks = LoadLooks(cfg, d); } catch { }
                return (r, looks);
            }
            catch { return (null, null); }
        }

        // la figura de cada tipo: luces (color del LightsTab y sitio en la cabeza) y lo que enciende cada estado de dibujo
        static Dictionary<string, HeadLook> LoadLooks(object cfg, IDictionary types)
        {
            var colors = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase);
            if (Prop(cfg, "LightsTable") is IDictionary lt)
                foreach (DictionaryEntry de in lt)
                    try { colors[de.Key.ToString()] = Color.FromArgb(Convert.ToInt32(Prop(de.Value, "r")), Convert.ToInt32(Prop(de.Value, "g")), Convert.ToInt32(Prop(de.Value, "b"))); } catch { }
            var res = new Dictionary<string, HeadLook>(StringComparer.OrdinalIgnoreCase);
            foreach (DictionaryEntry de in types)
            {
                try
                {
                    string name = de.Key.ToString(); var st = de.Value;
                    var look = new HeadLook { Type = name };
                    var lights = (Prop(st, "Lights") as IList)?.Cast<object>().ToList() ?? new List<object>();
                    look.Col = lights.Select(l => colors.TryGetValue(Prop(l, "Name")?.ToString() ?? "", out var c) ? c : Color.FromArgb(200, 200, 200)).ToArray();
                    look.X = lights.Select(l => Convert.ToSingle(Prop(l, "X") ?? 0f)).ToArray();
                    look.Y = lights.Select(l => Convert.ToSingle(Prop(l, "Y") ?? 0f)).ToArray();
                    if (Prop(st, "DrawStates") is IDictionary ds)
                    {
                        int n = 0; foreach (DictionaryEntry e in ds) n = Math.Max(n, Convert.ToInt32(Prop(e.Value, "Index")) + 1);
                        look.States = new (int, bool)[n][]; look.Names = new string[n];
                        foreach (DictionaryEntry e in ds)
                        {
                            int i = Convert.ToInt32(Prop(e.Value, "Index")); if (i < 0) continue;
                            look.Names[i] = (Prop(e.Value, "Name") ?? e.Key).ToString();
                            look.States[i] = ((Prop(e.Value, "DrawLights") as IList)?.Cast<object>() ?? Enumerable.Empty<object>())
                                .Select(x => (Convert.ToInt32(Prop(x, "LightIndex")), Prop(x, "Flashing") is bool b && b))
                                .Where(x => x.Item1 >= 0 && x.Item1 < lights.Count).ToArray();
                        }
                        for (int i = 0; i < n; i++) look.States[i] ??= Array.Empty<(int, bool)>();
                    }
                    object fn = Prop(st, "Function") ?? Prop(st, "FnType");
                    string fname = Prop(fn, "Name")?.ToString() ?? "";
                    var m = System.Text.RegularExpressions.Regex.Match(name, @"^pap(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    look.Pap = fname.Equals("PPREANUNCIO", StringComparison.OrdinalIgnoreCase) || name.StartsWith("pap", StringComparison.OrdinalIgnoreCase);
                    if (m.Success) look.PapSpeed = int.Parse(m.Groups[1].Value);
                    res[name] = look;
                }
                catch { }
            }
            return res;
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
        public Func<int, int> HeadDraw;                    // (1.2.75) su estado de dibujo (índice en su tipo del sigcfg), -1 = no se sabe
        public bool SignalsOk;                             // la foto de señales cuadra con la ruta
        public List<(string name, double lat, double lon, double kmh)> AiTrains = new();
        // (1.2.75) itinerarios de la sala: su camino (anulándose: ámbar) y, mientras se elige el destino de uno, los posibles
        public List<(List<(double lat, double lon)> pts, int kind)> Itins = new();   // kind: 0 establecido · 1 anulándose · 2 en cola
        public sealed class ItinPick { public int Origin; public readonly List<ItinPickOpt> Opts = new(); }
        public sealed class ItinPickOpt { public SchematicCircuits.ItinOption Opt; public List<List<(double lat, double lon)>> Parts = new(); public double Lat, Lon; }
        public ItinPick Pick;
        public static readonly object PickCancel = new object();   // (clic fuera mientras se elige)
        // El camino por delante de cada tren (como Ctrl+9), del color del tren; X donde lo corta una aguja en contra.
        public List<(Color col, List<(double lat, double lon)> pts, (double lat, double lon)? against)> Paths = new();
    }

    public static class RoomMapDraw
    {
        public const double MaxMPerPx = 6;   // más lejos, sin agujas ni señales (no cabrían)

        public static readonly Color ItinGreen = Color.FromArgb(120, 200, 80);   // (1.2.75) itinerarios (como el puesto de mando)
        static readonly Color SwOn = Color.FromArgb(245, 247, 248), SwOff = Color.FromArgb(90, 150, 158, 166), SwUnknown = Color.FromArgb(200, 150, 158, 166);
        public static readonly Color SwLocked = Color.FromArgb(0, 176, 255);    // aguja enclavada (azul vivo: los andenes son de otro azul)

        public static bool IsLocked(RoomMapView v, RouteGraph.SwitchGeo s) { char c = v?.SwitchLock?.Invoke(s) ?? '.'; return c == 'S' || c == 'T' || c == 'O' || c == 'I'; }   // (I: en un itinerario, 1.2.75)

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

        static PointF SigMarker(RoomMapGeo.Sig s, Func<double, double, PointF> pj, SizeF fig = default, float sc = 1)
        {
            var p = pj(s.Lat, s.Lon);
            if (double.IsNaN(s.Hdg)) return new PointF(p.X, p.Y - 8 * sc - fig.Height / 2);
            // a la derecha del sentido de la marcha (en la pantalla, y hacia abajo); con la figura, lo bastante lejos para
            // que no tape la vía
            float dx = (float)Math.Cos(s.Hdg), dy = (float)Math.Sin(s.Hdg);
            float d = fig.IsEmpty ? 8 * sc : 5 * sc + Math.Abs(dy) * fig.Width / 2 + Math.Abs(dx) * fig.Height / 2;
            return new PointF(p.X - dy * d, p.Y + dx * d);
        }

        // ── (1.2.75) figura de la señal (las del RCF): la cabeza con sus luces en su sitio, encendidas de su color y apagadas
        // en gris; las de destellos se encienden y se apagan cada medio segundo. Y el número de su pantalla de preanuncio.
        public static bool BlinkOn => DateTime.Now.Millisecond < 500;
        // tamaño de las señales en el mapa grande según la escala: igual de lejos; de cerca (menos de 1,2 m por píxel), más
        // grandes, hasta 1,7 veces (más, de muy cerca, tapaban la estación)
        public static float SignalScale(double mPerPx) => mPerPx <= 0 ? 1f : (float)Math.Max(1.0, Math.Min(1.7, Math.Pow(1.2 / mPerPx, 0.5)));
        public sealed class Figure { public RoomMapGeo.HeadLook Look; public int State; public string Pap; public Color PapCol; }

        // la cabeza principal que manda (la menos restrictiva, como SignalState) y su estado de dibujo; null si no se sabe
        public static Figure FigureOf(RoomMapGeo geo, RoomMapGeo.Sig s, Func<int, int> state, Func<int, int> draw)
        {
            if (geo == null || s == null || draw == null) return null;
            int best = -1, bk = -1;
            foreach (int k in s.Heads)
            {
                if (k < 0 || k >= geo.Heads.Count || !geo.Heads[k].Normal) continue;
                int st = state?.Invoke(k) ?? -1;
                if (bk < 0 || st > best) { best = st; bk = k; }
            }
            if (bk < 0) return null;
            var look = geo.Heads[bk].Look; int ds = draw(bk);
            if (look == null || look.Col.Length == 0 || ds < 0 || ds >= look.States.Length) return null;
            var f = new Figure { Look = look, State = ds };
            foreach (int pk in s.Paps)
            {
                if (pk < 0 || pk >= geo.Heads.Count || geo.Heads[pk].Look is not RoomMapGeo.HeadLook pl) continue;
                int pd = draw(pk);
                if (pd >= 0 && pd < pl.States.Length && pl.States[pd].Length > 0)
                {
                    var m = System.Text.RegularExpressions.Regex.Match(pl.Names[pd] ?? "", @"(\d+)");
                    f.Pap = m.Success ? m.Groups[1].Value : pl.PapSpeed > 0 ? pl.PapSpeed.ToString(System.Globalization.CultureInfo.InvariantCulture) : "P";
                    int li = pl.States[pd][0].lamp;
                    f.PapCol = li < pl.Col.Length ? pl.Col[li] : Color.FromArgb(255, 155, 0);
                    break;
                }
            }
            return f;
        }

        // ¿alguna luz encendida a destellos? (para repintar a menudo)
        public static bool Flashing(Figure f) => f != null && f.Look.States[f.State].Any(x => x.flash);

        static readonly Color LampOff = Color.FromArgb(50, 54, 60), HeadBg = Color.FromArgb(235, 14, 16, 18), HeadEdge = Color.FromArgb(110, 120, 130);

        // las luces en una rejilla: filas por altura (la de arriba, la primera) y columnas por su X. compacta: solo las
        // encendidas, una debajo de otra (de lejos)
        static List<(int row, int column, Color color, bool lit, bool flash)> Cells(Figure f, bool compact, out int nr, out int nc)
        {
            var look = f.Look;
            var ys = look.Y.Select(y => Math.Round(y, 1)).Distinct().OrderByDescending(y => y).ToList();
            var xs = look.X.Select(x => Math.Round(x, 1)).Distinct().OrderBy(x => x).ToList();
            var lit = new Dictionary<int, bool>(); foreach (var (lamp, flash) in look.States[f.State]) lit[lamp] = lit.TryGetValue(lamp, out bool o) ? o && flash : flash;
            var cells = new Dictionary<(int, int), (Color col, bool lit, bool flash)>();
            for (int i = 0; i < look.Col.Length; i++)
            {
                var key = (ys.IndexOf(Math.Round(look.Y[i], 1)), xs.IndexOf(Math.Round(look.X[i], 1)));
                bool on = lit.TryGetValue(i, out bool fl);
                if (cells.TryGetValue(key, out var c) && (c.lit || !on)) continue;
                cells[key] = (look.Col[i], on, on && fl);
            }
            var l = cells.OrderBy(kv => kv.Key.Item1).ThenBy(kv => kv.Key.Item2).Select(kv => (kv.Key.Item1, kv.Key.Item2, kv.Value.col, kv.Value.lit, kv.Value.flash)).ToList();
            nr = ys.Count; nc = Math.Max(1, xs.Count);
            if (compact)
            {
                var on = l.Where(q => q.lit).ToList();
                if (on.Count == 0) on = new() { (0, 0, LampOff, false, false) };
                l = on.Select((q, i) => (i, 0, q.Item3, q.lit, q.flash)).ToList(); nr = l.Count; nc = 1;
            }
            return l;
        }

        // lo que ocupa la figura (vertical, como en el mapa)
        public static SizeF FigureSize(Figure f, float r, bool compact)
        {
            Cells(f, compact, out int nr, out int nc);
            float step = r * 2.3f, pad = r * 0.6f;
            return new SizeF((nc - 1) * step + 2 * r + 2 * pad, (nr - 1) * step + 2 * r + 2 * pad);
        }

        // la dibuja: vertical centrada en c (mapa) o tumbada desde el mástil en c hacia face (+1 derecha / −1 izquierda;
        // la luz de abajo, junto al mástil: puesto de mando). Devuelve su recuadro.
        public static RectangleF DrawFigure(Graphics g, Figure f, PointF c, float r, bool compact, int face = 0)
        {
            var cells = Cells(f, compact, out int nr, out int nc);
            float step = r * 2.3f, pad = r * 0.6f;
            PointF At(int row, int col) => face == 0
                ? new PointF(c.X + (col - (nc - 1) / 2f) * step, c.Y + (row - (nr - 1) / 2f) * step)
                : new PointF(c.X + face * (r + pad + (nr - 1 - row) * step), c.Y + (col - (nc - 1) / 2f) * step);
            var pts = cells.Select(q => At(q.row, q.column)).ToList();
            var box = RectangleF.FromLTRB(pts.Min(q => q.X) - r - pad, pts.Min(q => q.Y) - r - pad, pts.Max(q => q.X) + r + pad, pts.Max(q => q.Y) + r + pad);
            var sm = g.SmoothingMode; g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = RoundRect(box, Math.Min(box.Width, box.Height) / 2))
            {
                using (var bb = new SolidBrush(HeadBg)) g.FillPath(bb, path);
                using (var bp = new Pen(HeadEdge, 1f)) g.DrawPath(bp, path);
            }
            bool blink = BlinkOn;
            for (int i = 0; i < cells.Count; i++)
            {
                var q = cells[i]; var p = pts[i];
                bool on = q.lit && (!q.flash || blink);
                if (on)
                    using (var glow = new SolidBrush(Color.FromArgb(70, q.color))) g.FillEllipse(glow, p.X - r - 1.2f, p.Y - r - 1.2f, 2 * r + 2.4f, 2 * r + 2.4f);
                using (var lb = new SolidBrush(on ? q.color : LampOff)) g.FillEllipse(lb, p.X - r, p.Y - r, 2 * r, 2 * r);
            }
            g.SmoothingMode = sm;
            return box;
        }

        // el número de la pantalla de preanuncio, en su recuadro (arriba a la izquierda en x, y); devuelve su tamaño
        public static SizeF DrawPap(Graphics g, Figure f, float x, float y, float size = 7f)
        {
            if (f?.Pap == null) return SizeF.Empty;
            using var fo = Theme.Font(size, FontStyle.Bold);
            var sz = g.MeasureString(f.Pap, fo);
            var box = new RectangleF(x, y, sz.Width + 1, sz.Height);
            using (var bb = new SolidBrush(Color.FromArgb(240, 8, 9, 10))) g.FillRectangle(bb, box);
            using (var bp = new Pen(Color.FromArgb(120, 128, 138), 1f)) g.DrawRectangle(bp, box.X, box.Y, box.Width, box.Height);
            using (var tb = new SolidBrush(f.PapCol)) g.DrawString(f.Pap, fo, tb, x + 1, y);
            return box.Size;
        }

        static GraphicsPath RoundRect(RectangleF r, float rad)
        {
            var gp = new GraphicsPath(); float d = Math.Max(0.5f, Math.Min(rad, Math.Min(r.Width, r.Height) / 2)) * 2;
            gp.AddArc(r.X, r.Y, d, d, 180, 90); gp.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            gp.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); gp.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            gp.CloseFigure(); return gp;
        }

        // Número de una señal (el de Open Rails), en pequeño y con borde oscuro (1.2.75; también lo usa el puesto de mando).
        public const double SignalIdMPerPx = 1.6;
        public static void SignalId(Graphics g, int obj, float x, float y, float size = 6.8f)
        {
            string t = obj.ToString(System.Globalization.CultureInfo.InvariantCulture);
            using var f = Theme.Font(size, FontStyle.Bold);
            var sz = g.MeasureString(t, f);
            using (var bb = new SolidBrush(Color.FromArgb(200, 15, 17, 19))) g.FillRectangle(bb, x - 1, y - 1, sz.Width - 2, sz.Height - 1);
            using var tb = new SolidBrush(Color.FromArgb(225, 230, 235));
            g.DrawString(t, f, tb, x - 2, y - 1);
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
            // (1.2.75) los itinerarios establecidos: verdes (anulándose, ámbar), anchos, con borde oscuro
            foreach (var (pts, kind) in v.Itins.OrderBy(x => x.kind == 2 ? 1 : 0))
            {
                var sp = pts.Select(q => pj(q.lat, q.lon)).ToArray();
                if (sp.Length < 2) continue;
                if (kind == 2)   // en cola: discontinuo azul
                {
                    using (var pen = new Pen(Color.FromArgb(100, 181, 246), 3.5f) { LineJoin = LineJoin.Round, DashPattern = new[] { 2.2f, 1.4f } }) g.DrawLines(pen, sp);
                    continue;
                }
                using (var halo = new Pen(Color.FromArgb(210, 15, 17, 19), 9f) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawLines(halo, sp);
                using (var pen = new Pen(kind == 1 ? Color.FromArgb(255, 183, 77) : ItinGreen, 5f) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawLines(pen, sp);
            }
            if (mPerPx <= MaxMPerPx)
            {
                // agujas: la rama puesta, marcada; la otra, tenue
                foreach (var s in v.Geo.Switches)
                {
                    var c = pj(s.Lat, s.Lon);
                    if (!view.Contains(c)) continue;
                    int pos = v.SwitchPos?.Invoke(s) ?? -1;   // (también los de una travesía: la foto de agujas los trae todos)
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
                    var dot = locked ? SwLocked : pos < 0 ? SwUnknown : SwOn;
                    using (var b = new SolidBrush(dot)) g.FillEllipse(b, c.X - r, c.Y - r, 2 * r, 2 * r);
                    using (var o = new Pen(Color.FromArgb(200, 20, 22, 24), 1.2f)) g.DrawEllipse(o, c.X - r, c.Y - r, 2 * r, 2 * r);
                    if (locked) Padlock(g, c.X - 14, c.Y - 14);   // (a la izquierda: los nombres de estación van a la derecha)
                }
                // señales
                if (v.SignalsOk)
                    foreach (var s in v.Geo.Signals)
                    {
                        var p = pj(s.Lat, s.Lon); if (!view.Contains(p)) continue;
                        // (1.2.75) la figura de la señal, si se sabe su estado de dibujo; si no, el punto del color del aspecto
                        var fig = FigureOf(v.Geo, s, v.HeadState, v.HeadDraw);
                        // (1.2.75) de cerca, la señal crece con el mapa (hasta 1,7 veces)
                        float sc = SignalScale(mPerPx);
                        bool compact = mPerPx > 2.5; float lr = (compact ? 3.4f : 2.5f) * sc;
                        var fsz = fig != null ? FigureSize(fig, lr, compact) : SizeF.Empty;
                        var m = SigMarker(s, pj, fsz, sc);
                        using (var lp = new Pen(Color.FromArgb(150, 200, 204, 208), Math.Min(2.5f, sc))) g.DrawLine(lp, p, m);
                        float r = 5f * sc;
                        int asp = SignalState(v, s);
                        bool callOn = IsCallOn(asp);   // rebase autorizado: rojo arriba y blanco abajo
                        var col = callOn ? AspectColor(0) : AspectColor(asp);
                        // flecha: hacia donde da paso (el sentido de la marcha de los trenes a los que manda)
                        if (!double.IsNaN(s.Hdg))
                        {
                            float dx = (float)Math.Cos(s.Hdg), dy = (float)Math.Sin(s.Hdg), w = 5.5f * sc;
                            float ext = fig != null ? Math.Abs(dx) * fsz.Width / 2 + Math.Abs(dy) * fsz.Height / 2 : r;
                            var tri = new[]
                            {
                                new PointF(m.X + dx * (ext + 10 * sc), m.Y + dy * (ext + 10 * sc)),
                                new PointF(m.X + dx * (ext - 1) - dy * w, m.Y + dy * (ext - 1) + dx * w),
                                new PointF(m.X + dx * (ext - 1) + dy * w, m.Y + dy * (ext - 1) - dx * w)
                            };
                            using (var tb = new SolidBrush(col)) g.FillPolygon(tb, tri);
                            using (var to = new Pen(Color.FromArgb(220, 15, 17, 19), 1.3f) { LineJoin = LineJoin.Round }) g.DrawPolygon(to, tri);
                        }
                        if (fig != null)
                        {
                            var fb = DrawFigure(g, fig, m, lr, compact);
                            float px = fb.Right + 2 * sc, fs = 7f * Math.Min(2.2f, sc);
                            DrawPap(g, fig, px, fb.Bottom - fs * 1.6f, fs);
                            if (s.Obj >= 0 && mPerPx <= SignalIdMPerPx) SignalId(g, s.Obj, px, fb.Top - 1, 6.8f * Math.Min(1.8f, sc));
                            continue;
                        }
                        if (callOn)
                        {
                            using (var top = new SolidBrush(AspectColor(0))) g.FillPie(top, m.X - r, m.Y - r, 2 * r, 2 * r, 180, 180);
                            using (var bottom = new SolidBrush(CallOnWhite)) g.FillPie(bottom, m.X - r, m.Y - r, 2 * r, 2 * r, 0, 180);
                        }
                        else using (var b = new SolidBrush(col)) g.FillEllipse(b, m.X - r, m.Y - r, 2 * r, 2 * r);
                        using (var o = new Pen(Color.FromArgb(220, 15, 17, 19), 1.4f)) g.DrawEllipse(o, m.X - r, m.Y - r, 2 * r, 2 * r);
                        // su número (el de Open Rails: el de los avisos y el registro), de cerca
                        if (s.Obj >= 0 && mPerPx <= SignalIdMPerPx) SignalId(g, s.Obj, m.X + r + 2, m.Y - r - 1, 6.8f * Math.Min(1.8f, sc));
                    }
            }
            // (1.2.75) eligiendo el destino de un itinerario: los caminos posibles y un círculo en cada destino
            if (v.Pick != null)
            {
                foreach (var o in v.Pick.Opts)
                {
                    using (var pen = new Pen(o.Opt.Mine ? ItinGreen : Color.FromArgb(225, 236, 238, 240), o.Opt.Mine ? 4f : 3f) { DashPattern = new[] { 2.5f, 1.6f }, LineJoin = LineJoin.Round })
                        foreach (var part in o.Parts)
                        {
                            var sp = part.Select(q => pj(q.lat, q.lon)).ToArray();
                            if (sp.Length >= 2) g.DrawLines(pen, sp);
                        }
                    var c = pj(o.Lat, o.Lon); float r = 9f;
                    using (var fb = new SolidBrush(Color.FromArgb(230, 20, 60, 30))) g.FillEllipse(fb, c.X - r, c.Y - r, 2 * r, 2 * r);
                    using (var rp = new Pen(o.Opt.Mine ? ItinGreen : Color.White, 2.2f)) g.DrawEllipse(rp, c.X - r, c.Y - r, 2 * r, 2 * r);
                    string lab = (o.Opt.Dest >= 0 ? o.Opt.Dest.ToString() : I18n.T("topera")) + (o.Opt.Mine ? "  ★ " + I18n.T("tu itinerario") : "");
                    using var lf = Theme.Font(8.2f, FontStyle.Bold);
                    var sz = g.MeasureString(lab, lf);
                    using (var bb = new SolidBrush(Color.FromArgb(220, 15, 17, 19))) g.FillRectangle(bb, c.X + r + 2, c.Y - sz.Height / 2, sz.Width, sz.Height);
                    using (var tb = new SolidBrush(o.Opt.Mine ? ItinGreen : Color.White)) g.DrawString(lab, lf, tb, c.X + r + 2, c.Y - sz.Height / 2);
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

        // Lo que hay bajo el ratón: una aguja o una señal (null si nada). Si caen varias agujas bajo el ratón (los cuatro
        // desvíos de una travesía están a 2 o 3 m), la lista de todas, de izquierda a derecha en la pantalla, para elegir.
        public static object Hit(RoomMapView v, Func<double, double, PointF> pj, double mPerPx, Point at)
        {
            if (v?.Geo == null || pj == null) return null;
            // (1.2.75) eligiendo el destino de un itinerario: sus círculos
            if (v.Pick != null)
            {
                RoomMapView.ItinPickOpt bo = null; double bdo = 16 * 16;
                foreach (var o in v.Pick.Opts)
                {
                    var c = pj(o.Lat, o.Lon); double d = (c.X - at.X) * (c.X - at.X) + (c.Y - at.Y) * (c.Y - at.Y);
                    if (d < bdo) { bdo = d; bo = o; }
                }
                return (object)bo ?? RoomMapView.PickCancel;
            }
            if (mPerPx > MaxMPerPx) return null;
            object best = null; double bd = 10 * 10;
            if (v.SignalsOk)
            {
                // (1.2.75) donde se ve la señal: su figura (tamaño y sitio como al dibujarla) o, sin ella, el punto
                float sc = SignalScale(mPerPx); bool compact = mPerPx > 2.5; float lr = (compact ? 3.4f : 2.5f) * sc;
                foreach (var s in v.Geo.Signals)
                {
                    var fig = FigureOf(v.Geo, s, v.HeadState, v.HeadDraw);
                    var fsz = fig != null ? FigureSize(fig, lr, compact) : SizeF.Empty;
                    var m = SigMarker(s, pj, fsz, sc);
                    double rx = Math.Max(10, fsz.Width / 2 + 4), ry = Math.Max(10, fsz.Height / 2 + 4);
                    double dx = (m.X - at.X) / rx, dy = (m.Y - at.Y) / ry, d = dx * dx + dy * dy;   // (dentro de la figura: < 1)
                    if (d < 1 && d * 100 < bd) { bd = d * 100; best = s; }
                }
            }
            var under = new List<(RouteGraph.SwitchGeo s, float x)>();
            foreach (var s in v.Geo.Switches)
            {
                var c = pj(s.Lat, s.Lon); double d = (c.X - at.X) * (c.X - at.X) + (c.Y - at.Y) * (c.Y - at.Y);
                if (d < 10 * 10) under.Add((s, c.X));
                if (d < bd) { bd = d; best = s; }
            }
            if (best is RouteGraph.SwitchGeo && under.Count > 1) return under.OrderBy(u => u.x).Select(u => u.s).ToList();
            return best;
        }
    }
}
