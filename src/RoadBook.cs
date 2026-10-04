// Hoja de ruta: en el mapa grande se marcan uno o varios puntos sobre la vía y SelectOR traza el
// itinerario por las vías reales (grafo del .tdb, respetando los desvíos: no se puede entrar por una
// rama y salir por la otra), con las estaciones por las que pasa y la hora estimada de paso.
// La hora se calcula con los límites de velocidad de la vía (señales del .tdb, sin pasar de la
// velocidad máxima del tren), el tiempo de parada en las estaciones donde se para y la hora del
// simulador. Se recalcula en vivo según avanza el tren.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Orts.Formats.Msts;

namespace SelectOR
{
    // Grafo de vías de una ruta. Distancias en metros reales (de mundo); la geometría (X, Y) va en una
    // proyección local en metros, solo para buscar el punto de vía más cercano.
    public sealed class RouteGraph
    {
        public sealed class Edge
        {
            public int A = -1, B = -1;              // nodo del .tdb en el inicio (A) y en el final (B)
            public float[] X, Y;                    // vértices (proyección local)
            public double[] Cum;                    // distancia desde el inicio del tramo hasta cada vértice
            public double Len;
            public readonly List<(double off, double kmh)> Limits = new();      // señales de límite de velocidad
            public readonly List<(double off, string name)> Platforms = new();  // andenes (nombre de estación)
        }
        sealed class Node { public readonly List<int> In = new(), Out = new(); }

        public Edge[] Edges = Array.Empty<Edge>();
        readonly Dictionary<int, Node> _nodes = new();
        public double Lat0, Lon0, Kx, Ky;
        const float Cell = 200f;
        Dictionary<long, List<(int e, int i)>> _grid;

        public (double x, double y) Proj(double lat, double lon) => ((lon - Lon0) * Kx, (Lat0 - lat) * Ky);
        public (double lat, double lon) Unproj(double x, double y) => (Lat0 - y / Ky, Lon0 + x / Kx);

        static double D((double x, double z) a, (double x, double z) b) => Math.Sqrt((a.x - b.x) * (a.x - b.x) + (a.z - b.z) * (a.z - b.z));

        // null si la ruta no tiene .tdb o esta versión de Open Rails no trae la conversión a lat/lon.
        public static RouteGraph Build(string routeDir, Func<string, string> cleanName)
        {
            if (string.IsNullOrEmpty(routeDir) || !Directory.Exists(routeDir) || !OrGeo.Available) return null;
            var tdbs = Directory.GetFiles(routeDir, "*.tdb");
            if (tdbs.Length == 0) return null;
            var db = new TrackDatabaseFile(tdbs[0]);
            var tn = db.TrackDB.TrackNodes;
            var items = db.TrackDB.TrItemTable ?? Array.Empty<TrItem>();
            var byId = new Dictionary<uint, TrItem>();
            foreach (var it in items) if (it != null) byId[it.TrItemId] = it;
            var tsec = TrackGeometry.Sections(routeDir);

            var nodePos = new Dictionary<int, (double x, double z)>();
            for (int ni = 0; ni < tn.Length; ni++)
            {
                var n = tn[ni];
                if (n?.UiD != null && (n.TrJunctionNode != null || n.TrEndNode))
                    nodePos[ni] = (n.UiD.TileX * 2048.0 + n.UiD.X, n.UiD.TileZ * 2048.0 + n.UiD.Z);
            }

            var g = new RouteGraph();
            var edges = new List<Edge>();
            var world = new List<List<(double x, double z)>>();
            var vecToEdge = new Dictionary<int, int>();
            for (int ni = 0; ni < tn.Length; ni++)
            {
                var vn = tn[ni]?.TrVectorNode;
                if (vn?.TrVectorSections == null || vn.TrVectorSections.Length < 1) continue;
                var pts = TrackGeometry.NodePolylineD(vn.TrVectorSections, tsec);
                if (pts.Count == 0) continue;
                var pins = tn[ni].TrPins ?? Array.Empty<TrPin>();
                int p0 = pins.Length >= 1 ? pins[0].Link : -1, p1 = pins.Length >= 2 ? pins[1].Link : -1;
                // El primer pin del tramo es el nodo de su inicio (el de la primera sección) y el segundo, el del final.
                int a = p0, b = p1;
                double pre = 0;
                if (nodePos.TryGetValue(a, out var qa)) { double d = D(qa, pts[0]); if (d > 0.05) { pts.Insert(0, qa); pre = d; } }
                if (nodePos.TryGetValue(b, out var qb) && D(qb, pts[^1]) > 0.05) pts.Add(qb);

                var cum = new double[pts.Count];
                for (int i = 1; i < pts.Count; i++) cum[i] = cum[i - 1] + D(pts[i - 1], pts[i]);
                var keep = RdpKeep(pts, 0.75);
                var kp = new List<(double x, double z)>(); var kc = new List<double>();
                for (int i = 0; i < pts.Count; i++) if (keep[i]) { kp.Add(pts[i]); kc.Add(cum[i]); }
                var e = new Edge { A = a, B = b, Cum = kc.ToArray(), Len = cum[^1] };

                if (vn.TrItemRefs != null)
                    foreach (int r in vn.TrItemRefs)
                    {
                        TrItem it = r >= 0 && r < items.Length && items[r] != null && items[r].TrItemId == r ? items[r]
                                  : (r >= 0 && byId.TryGetValue((uint)r, out var x) ? x : null);
                        if (it == null) continue;
                        double off = Math.Max(0, Math.Min(e.Len, it.SData1 + pre));
                        if (it is SpeedPostItem sp)
                        {
                            if (!sp.IsLimit || sp.IsMilePost || sp.IsWarning || sp.IsResume) continue;
                            if (sp.IsFreight && !sp.IsPassenger) continue;   // límite solo de mercancías
                            double kmh = sp.SpeedInd * (sp.IsMPH ? 1.609344 : 1.0);
                            if (kmh > 0 && kmh < 500) e.Limits.Add((off, kmh));
                        }
                        else if (it is PlatformItem pl && !string.IsNullOrWhiteSpace(pl.Station))
                        {
                            string name = cleanName?.Invoke(pl.Station.Trim()) ?? pl.Station.Trim();
                            if (!string.IsNullOrWhiteSpace(name)) e.Platforms.Add((off, name));
                        }
                    }
                e.Limits.Sort((u, v) => u.off.CompareTo(v.off));
                e.Platforms.Sort((u, v) => u.off.CompareTo(v.off));
                vecToEdge[ni] = edges.Count;
                edges.Add(e); world.Add(kp);
            }
            if (edges.Count == 0) return null;

            // a lat/lon (la misma conversión que da la posición del tren) y a la proyección local
            var lat = new double[edges.Count][]; var lon = new double[edges.Count][];
            double minLa = 90, maxLa = -90, minLo = 180, maxLo = -180;
            for (int k = 0; k < edges.Count; k++)
            {
                var w = world[k];
                lat[k] = new double[w.Count]; lon[k] = new double[w.Count];
                for (int i = 0; i < w.Count; i++)
                {
                    if (!OrGeo.TryLatLon(w[i].x, w[i].z, out lat[k][i], out lon[k][i])) return null;
                    minLa = Math.Min(minLa, lat[k][i]); maxLa = Math.Max(maxLa, lat[k][i]);
                    minLo = Math.Min(minLo, lon[k][i]); maxLo = Math.Max(maxLo, lon[k][i]);
                }
            }
            g.Lat0 = (minLa + maxLa) / 2; g.Lon0 = (minLo + maxLo) / 2;
            g.Ky = 111320.0; g.Kx = 111320.0 * Math.Cos(g.Lat0 * Math.PI / 180.0);
            for (int k = 0; k < edges.Count; k++)
            {
                var e = edges[k]; int n = lat[k].Length;
                e.X = new float[n]; e.Y = new float[n];
                for (int i = 0; i < n; i++) { var (x, y) = g.Proj(lat[k][i], lon[k][i]); e.X[i] = (float)x; e.Y[i] = (float)y; }
            }
            g.Edges = edges.ToArray();

            // Desvíos y finales: qué tramos llegan por cada lado. Se decide por la geometría (no por el orden
            // de los pines, que no siempre es fiable): las dos ramas de un desvío salen casi en la misma
            // dirección y el tronco, en la contraria. Un tren que llega por una rama solo puede seguir por el
            // tronco, y viceversa.
            for (int ni = 0; ni < tn.Length; ni++)
            {
                var n = tn[ni];
                if (n == null || (n.TrJunctionNode == null && !n.TrEndNode) || n.TrPins == null) continue;
                var node = new Node();
                var att = new List<(int e, double ang)>();
                foreach (var pin in n.TrPins)
                {
                    if (!vecToEdge.TryGetValue(pin.Link, out int ei)) continue;
                    var E = g.Edges[ei];
                    if (E.A == ni) att.Add((ei, g.AwayAngle(ei, true)));
                    if (E.B == ni) att.Add((ei, g.AwayAngle(ei, false)));
                }
                if (att.Count == 3)
                {
                    // el par que forma el ángulo más cerrado son las ramas; el otro, el tronco
                    int trunk = 0; double bestPair = double.MaxValue;
                    for (int i = 0; i < 3; i++)
                    {
                        int j = (i + 1) % 3, k = (i + 2) % 3;
                        double d = Math.Abs(((att[j].ang - att[k].ang) % 360 + 540) % 360 - 180);
                        if (d < bestPair) { bestPair = d; trunk = i; }
                    }
                    for (int i = 0; i < 3; i++) (i == trunk ? node.In : node.Out).Add(att[i].e);
                }
                else if (att.Count == 2) { node.In.Add(att[0].e); node.Out.Add(att[1].e); }
                else
                {
                    int nin = (int)n.Inpins;
                    for (int k = 0; k < n.TrPins.Length; k++)
                        if (vecToEdge.TryGetValue(n.TrPins[k].Link, out int ei)) (k < nin ? node.In : node.Out).Add(ei);
                }
                g._nodes[ni] = node;
            }
            g.BuildGrid();
            return g;
        }

        static bool[] RdpKeep(List<(double x, double z)> p, double eps)
        {
            int n = p.Count; var keep = new bool[n];
            if (n == 0) return keep;
            keep[0] = keep[n - 1] = true;
            var st = new Stack<(int, int)>(); if (n > 2) st.Push((0, n - 1));
            while (st.Count > 0)
            {
                var (a, b) = st.Pop();
                double best = 0; int bi = -1;
                double ax = p[a].x, az = p[a].z, dx = p[b].x - ax, dz = p[b].z - az, l2 = dx * dx + dz * dz;
                for (int i = a + 1; i < b; i++)
                {
                    double px = p[i].x - ax, pz = p[i].z - az;
                    double t = l2 > 0 ? Math.Max(0, Math.Min(1, (px * dx + pz * dz) / l2)) : 0;
                    double ex = px - t * dx, ez = pz - t * dz, d = ex * ex + ez * ez;
                    if (d > best) { best = d; bi = i; }
                }
                if (bi >= 0 && best > eps * eps) { keep[bi] = true; st.Push((a, bi)); st.Push((bi, b)); }
            }
            return keep;
        }

        static long Key(int cx, int cy) => ((long)cx << 32) ^ (uint)cy;

        void BuildGrid()
        {
            _grid = new Dictionary<long, List<(int, int)>>();
            for (int e = 0; e < Edges.Length; e++)
            {
                var X = Edges[e].X; var Y = Edges[e].Y;
                for (int i = 0; i + 1 < X.Length; i++)
                {
                    int x0 = (int)Math.Floor(Math.Min(X[i], X[i + 1]) / Cell), x1 = (int)Math.Floor(Math.Max(X[i], X[i + 1]) / Cell);
                    int y0 = (int)Math.Floor(Math.Min(Y[i], Y[i + 1]) / Cell), y1 = (int)Math.Floor(Math.Max(Y[i], Y[i + 1]) / Cell);
                    if ((x1 - x0 + 1) * (y1 - y0 + 1) > 900) continue;
                    for (int cx = x0; cx <= x1; cx++)
                        for (int cy = y0; cy <= y1; cy++)
                        {
                            long k = Key(cx, cy);
                            if (!_grid.TryGetValue(k, out var l)) _grid[k] = l = new List<(int, int)>();
                            l.Add((e, i));
                        }
                }
            }
        }

        // Punto de vía más cercano (a menos de maxM metros): tramo y distancia desde su inicio.
        public bool Nearest(double lat, double lon, double maxM, out int edge, out double off, out double distM)
        {
            edge = -1; off = 0; distM = double.MaxValue;
            if (_grid == null) return false;
            var (x, y) = Proj(lat, lon);
            int r = (int)Math.Ceiling(maxM / Cell);
            int gx = (int)Math.Floor(x / Cell), gy = (int)Math.Floor(y / Cell);
            double best = maxM * maxM;
            for (int cx = gx - r; cx <= gx + r; cx++)
                for (int cy = gy - r; cy <= gy + r; cy++)
                {
                    if (!_grid.TryGetValue(Key(cx, cy), out var l)) continue;
                    foreach (var (e, i) in l)
                    {
                        var E = Edges[e];
                        double ax = E.X[i], ay = E.Y[i], dx = E.X[i + 1] - ax, dy = E.Y[i + 1] - ay, l2 = dx * dx + dy * dy;
                        double t = l2 > 0 ? Math.Max(0, Math.Min(1, ((x - ax) * dx + (y - ay) * dy) / l2)) : 0;
                        double px = ax + t * dx - x, py = ay + t * dy - y, d2 = px * px + py * py;
                        if (d2 < best) { best = d2; edge = e; off = E.Cum[i] + t * (E.Cum[i + 1] - E.Cum[i]); }
                    }
                }
            if (edge < 0) return false;
            distM = Math.Sqrt(best);
            return true;
        }

        // Punto (proyección local) a una distancia del inicio del tramo.
        public (double x, double y) PointAt(int e, double off)
        {
            var E = Edges[e];
            if (E.X.Length == 1 || off <= 0) return (E.X[0], E.Y[0]);
            if (off >= E.Len) return (E.X[^1], E.Y[^1]);
            int i = Array.BinarySearch(E.Cum, off); if (i < 0) i = ~i - 1;
            i = Math.Max(0, Math.Min(E.X.Length - 2, i));
            double span = E.Cum[i + 1] - E.Cum[i], t = span > 0 ? (off - E.Cum[i]) / span : 0;
            return (E.X[i] + (E.X[i + 1] - E.X[i]) * t, E.Y[i] + (E.Y[i + 1] - E.Y[i]) * t);
        }

        // Rumbo con el que el tramo SALE de uno de sus extremos (medido en sus primeros ~25 m).
        double AwayAngle(int e, bool fromA)
        {
            var E = Edges[e];
            double off = fromA ? Math.Min(25, E.Len) : Math.Max(0, E.Len - 25);
            var (x0, y0) = PointAt(e, fromA ? 0 : E.Len);
            var (x1, y1) = PointAt(e, off);
            return (Math.Atan2(x1 - x0, -(y1 - y0)) * 180 / Math.PI + 360) % 360;
        }

        // Rumbo (0 = norte) del tramo en ese punto, en el sentido de A hacia B.
        public double BearingAt(int e, double off)
        {
            var E = Edges[e];
            if (E.X.Length < 2) return 0;
            int i = Array.BinarySearch(E.Cum, off); if (i < 0) i = ~i - 1;
            i = Math.Max(0, Math.Min(E.X.Length - 2, i));
            double dx = E.X[i + 1] - E.X[i], dy = E.Y[i + 1] - E.Y[i];
            return (Math.Atan2(dx, -dy) * 180 / Math.PI + 360) % 360;
        }

        // Tramos a los que se puede seguir al salir del tramo e en el sentido d (0: hacia B, 1: hacia A).
        // Estado = tramo·2 + sentido con el que se recorre.
        IEnumerable<int> Exits(int e, int d)
        {
            var E = Edges[e]; int nId = d == 0 ? E.B : E.A;
            if (nId < 0 || !_nodes.TryGetValue(nId, out var node)) yield break;
            bool inIn = node.In.Contains(e), inOut = node.Out.Contains(e);
            IEnumerable<int> next = inIn && inOut ? node.In.Concat(node.Out) : inIn ? node.Out : inOut ? node.In : Enumerable.Empty<int>();
            foreach (int f in next)
            {
                var F = Edges[f];
                if (F.A == nId) yield return f * 2;
                if (F.B == nId) yield return f * 2 + 1;
            }
        }

        public sealed class Leg { public readonly List<(int e, double from, double to)> Parts = new(); public int EndDir; public double Len; }

        // Final de vía (topera): ahí el tren puede invertir la marcha, como en una estación término.
        bool DeadEnd(int nodeId) => nodeId >= 0 && _nodes.TryGetValue(nodeId, out var n) && n.In.Count + n.Out.Count == 1;
        const double ReversePenaltyM = 5000;   // solo se invierte si no hay otro camino razonable

        // Camino más corto por las vías desde (e0, off0) en el sentido dir0 hasta (eg, offg). null si no hay.
        public Leg Route(int e0, double off0, int dir0, int eg, double offg)
        {
            var E0 = Edges[e0];
            if (e0 == eg && (dir0 == 0 ? offg >= off0 - 0.5 : offg <= off0 + 0.5))
            {
                var direct = new Leg { EndDir = dir0, Len = Math.Abs(offg - off0) };
                direct.Parts.Add((e0, off0, offg));
                return direct;
            }
            int n = Edges.Length * 2;
            var dist = new double[n]; var prev = new int[n];
            Array.Fill(dist, double.PositiveInfinity); Array.Fill(prev, -2);
            var pq = new PriorityQueue<int, double>();
            double first = dir0 == 0 ? E0.Len - off0 : off0;
            foreach (int s in Exits(e0, dir0))
                if (first < dist[s]) { dist[s] = first; prev[s] = -1; pq.Enqueue(s, first); }
            if (DeadEnd(dir0 == 0 ? E0.B : E0.A))   // el tren está en una vía término: sale invirtiendo
            {
                int s = e0 * 2 + (1 - dir0); double c = first + ReversePenaltyM;
                if (c < dist[s]) { dist[s] = c; prev[s] = -1; pq.Enqueue(s, c); }
            }
            double best = double.PositiveInfinity; int bestState = -1;
            while (pq.TryDequeue(out int s, out double c))
            {
                if (c > dist[s]) continue;
                if (c >= best) break;
                int e = s >> 1, d = s & 1; var E = Edges[e];
                if (e == eg)
                {
                    double tot = c + (d == 0 ? offg : E.Len - offg);
                    if (tot < best) { best = tot; bestState = s; }
                }
                double c2 = c + E.Len;
                foreach (int t in Exits(e, d))
                    if (c2 < dist[t]) { dist[t] = c2; prev[t] = s; pq.Enqueue(t, c2); }
                if (DeadEnd(d == 0 ? E.B : E.A))
                {
                    int t = e * 2 + (1 - d); double c3 = c2 + ReversePenaltyM;
                    if (c3 < dist[t]) { dist[t] = c3; prev[t] = s; pq.Enqueue(t, c3); }
                }
            }
            if (bestState < 0) return null;
            var states = new List<int>();
            for (int s = bestState; s >= 0; s = prev[s]) states.Add(s);
            states.Reverse();
            var leg = new Leg { EndDir = bestState & 1 };
            leg.Parts.Add((e0, off0, dir0 == 0 ? E0.Len : 0));
            for (int i = 0; i < states.Count - 1; i++)
            {
                int e = states[i] >> 1, d = states[i] & 1;
                leg.Parts.Add((e, d == 0 ? 0 : Edges[e].Len, d == 0 ? Edges[e].Len : 0));
            }
            leg.Parts.Add((eg, (bestState & 1) == 0 ? 0 : Edges[eg].Len, offg));
            foreach (var (_, f, t) in leg.Parts) leg.Len += Math.Abs(t - f);   // metros reales (sin la penalización)
            return leg;
        }
    }

    // El itinerario elegido y su hoja de ruta (estado vivo; se usa desde el hilo de la interfaz).
    public sealed class RoadBook
    {
        public sealed class Waypoint { public double Lat, Lon; public int Edge; public double Off; }
        public sealed class Stop
        {
            public string Name; public double Dist;      // distancia desde el origen del itinerario
            public bool Halt;                           // se para (true) o se pasa sin parar
            public bool Passed; public double PassedAt = double.NaN;   // hora del juego al pasar
            public DateTime PassedAtPc = DateTime.MinValue;            // hora del PC al pasar
            public double EtaS = double.NaN;            // segundos desde ahora hasta llegar
            public bool IsEnd;                          // fila final («Destino»), no una estación
            public bool IsReverse;                      // cambio de sentido (el tren invierte la marcha)
            public double Lat, Lon;                     // dónde está (para marcarla en el mapa)
            public double SchedArr = double.NaN, SchedDep = double.NaN;   // horario (s desde medianoche; NaN = sin horario)
            public bool HasSched => !double.IsNaN(SchedArr) || !double.IsNaN(SchedDep);
        }

        // Horario del tren (modo Horarios): estación → llegada y salida programadas. Con horario, las estaciones
        // que figuran en él son paradas y el resto se pasan sin parar (el maquinista puede cambiarlo).
        public List<(string name, double arr, double dep)> Schedule;
        public bool FromPlan;   // itinerario de un horario o una actividad: horas siempre del simulador (sin SIM/PC)

        public static string NormName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new System.Text.StringBuilder();
            foreach (char ch in s.Normalize(System.Text.NormalizationForm.FormD))
            {
                var cat = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch);
                if (cat == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
                if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
                else if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
            }
            return sb.ToString().Trim();
        }

        // Estación del itinerario (nombre del andén) ↔ estación del horario: igual, una contiene a la otra o
        // misma primera palabra (de 4 letras o más).
        bool MatchSchedule(string platform, out double arr, out double dep)
        {
            arr = dep = double.NaN;
            if (Schedule == null) return false;
            string a = NormName(platform);
            if (a.Length == 0) return false;
            int best = -1, score = 0;
            for (int i = 0; i < Schedule.Count; i++)
            {
                string b = NormName(Schedule[i].name);
                if (b.Length == 0) continue;
                int sc = a == b ? 3 : (a.Length >= 4 && b.Length >= 4 && (a.Contains(b) || b.Contains(a))) ? 2
                       : (a.Split(' ')[0] is string fa && fa.Length >= 4 && fa == b.Split(' ')[0]) ? 1 : 0;
                if (sc > score) { score = sc; best = i; }
            }
            if (best < 0) return false;
            arr = Schedule[best].arr; dep = Schedule[best].dep;
            return true;
        }
        public const double ReverseS = 120;             // tiempo estimado para invertir la marcha

        public string RouteDir;
        public RouteGraph Graph;
        public bool Building, Editing;
        public string Problem;                          // por qué no hay itinerario (o null)
        public readonly List<Waypoint> Points = new();
        public readonly List<Stop> Stops = new();
        public readonly List<(double d, double kmh)> Limits = new();
        readonly Dictionary<string, bool> _haltChoice = new(StringComparer.OrdinalIgnoreCase);
        public bool DefaultHalt;                        // por defecto se pasa sin parar: las paradas las marca el maquinista
        public double[] PathLat = Array.Empty<double>(), PathLon = Array.Empty<double>();
        float[] _px = Array.Empty<float>(), _py = Array.Empty<float>();
        double[] _pc = Array.Empty<double>();           // distancia acumulada en cada vértice del trazado
        public double TotalLen, Progress;
        public bool OffRoute, Reversal;
        public double[] PointDist = Array.Empty<double>();
        public int ReachedPoints;                       // puntos ya alcanzados (no se vuelven a planificar)
        public int Version;                             // cambia con cada itinerario nuevo (para repintar)
        public DateTime OffSinceUtc = DateTime.MaxValue;

        public bool HasPlan => _px.Length >= 2;
        // Vértice del trazado hasta el que ya ha llegado el tren (para pintar lo recorrido más apagado).
        public int PassedIdx { get { if (_pc.Length == 0) return 0; int i = Array.BinarySearch(_pc, Progress); return Math.Max(0, i < 0 ? ~i - 1 : i); } }

        public void Reset(string routeDir)
        {
            if (!string.Equals(routeDir, RouteDir, StringComparison.OrdinalIgnoreCase)) { Graph = null; Building = false; _haltChoice.Clear(); }
            RouteDir = routeDir;
            Points.Clear(); ClearPlan(); Editing = false; Problem = null; ReachedPoints = 0;
        }

        void ClearPlan()
        {
            Stops.Clear(); Limits.Clear();
            PathLat = PathLon = Array.Empty<double>(); _px = _py = Array.Empty<float>(); _pc = Array.Empty<double>();
            TotalLen = Progress = 0; OffRoute = false; Reversal = false; PointDist = Array.Empty<double>();
            OffSinceUtc = DateTime.MaxValue; Version++;
        }

        public bool AddPoint(double lat, double lon, double maxM)
        {
            if (Graph == null || !Graph.Nearest(lat, lon, maxM, out int e, out double off, out _)) return false;
            var (x, y) = Graph.PointAt(e, off); var (la, lo) = Graph.Unproj(x, y);
            Points.Add(new Waypoint { Lat = la, Lon = lo, Edge = e, Off = off });
            return true;
        }

        public void ClearHaltChoices() => _haltChoice.Clear();   // tren nuevo con horario: mandan sus paradas
        public void Undo() { if (Points.Count > ReachedPoints) Points.RemoveAt(Points.Count - 1); if (Points.Count == 0) ReachedPoints = 0; }
        public void ClearAll() { Points.Clear(); ReachedPoints = 0; ClearPlan(); Problem = null; }

        public void ToggleHalt(int stopIndex)
        {
            if (stopIndex < 0 || stopIndex >= Stops.Count || Stops[stopIndex].IsEnd || Stops[stopIndex].IsReverse) return;
            var s = Stops[stopIndex]; s.Halt = !s.Halt; _haltChoice[s.Name] = s.Halt;
        }

        // «Todas» / «Ninguna»: parar en todas las estaciones que faltan, o en ninguna.
        public void SetAllHalts(bool halt)
        {
            foreach (var s in Stops)
                if (!s.IsEnd && !s.IsReverse && !s.Passed) { s.Halt = halt; _haltChoice[s.Name] = halt; }
        }

        public int HaltCount => Stops.Count(s => s.Halt && !s.IsEnd && !s.IsReverse && !s.Passed);

        // Rehace el itinerario desde la posición del tren por los puntos que faltan.
        public void Replan(bool hasPos, double lat, double lon, bool hasHdg, double hdg)
        {
            var oldPassed = Stops.Where(s => s.Passed && (!double.IsNaN(s.PassedAt) || s.PassedAtPc != DateTime.MinValue))
                .ToDictionary(s => s.Name, s => (s.PassedAt, s.PassedAtPc), StringComparer.OrdinalIgnoreCase);
            ClearPlan(); Problem = null;
            if (Graph == null || Points.Count <= ReachedPoints) return;
            int startIdx = ReachedPoints, e; double off; var dirs = new List<int>();
            if (hasPos && Graph.Nearest(lat, lon, 150, out e, out off, out _))
            {
                if (hasHdg)
                {
                    double diff = Math.Abs(((hdg - Graph.BearingAt(e, off)) % 360 + 540) % 360 - 180);
                    dirs.Add(diff <= 90 ? 0 : 1);
                }
                else { dirs.Add(0); dirs.Add(1); }
            }
            else if (Points.Count - startIdx >= 2) { e = Points[startIdx].Edge; off = Points[startIdx].Off; startIdx++; dirs.Add(0); dirs.Add(1); }
            else { Problem = I18n.T("Esperando la posición del tren…"); return; }

            _firstPlanned = startIdx;
            var parts = new List<(int e, double from, double to)>();
            var pd = new List<double>();
            double total = 0;
            for (int i = startIdx; i < Points.Count; i++)
            {
                var p = Points[i];
                RouteGraph.Leg best = null;
                foreach (int d in dirs) { var l = Graph.Route(e, off, d, p.Edge, p.Off); if (l != null && (best == null || l.Len < best.Len)) best = l; }
                if (best == null)   // con cambio de sentido (p. ej., en una estación término)
                    foreach (int d in new[] { 0, 1 }.Where(x => !dirs.Contains(x)))
                    {
                        var l = Graph.Route(e, off, d, p.Edge, p.Off);
                        if (l != null && (best == null || l.Len < best.Len)) best = l;
                    }
                if (best == null) { Problem = string.Format(I18n.T("No hay camino por la vía hasta el punto {0}."), i + 1); ClearPlanKeepProblem(); return; }
                parts.AddRange(best.Parts); total += best.Len; pd.Add(total);
                e = p.Edge; off = p.Off; dirs = new List<int> { best.EndDir };
            }
            Build(parts, pd, oldPassed);
        }

        void ClearPlanKeepProblem() { var pr = Problem; ClearPlan(); Problem = pr; }

        void Build(List<(int e, double from, double to)> parts, List<double> pointDist, Dictionary<string, (double PassedAt, DateTime PassedAtPc)> oldPassed)
        {
            var xs = new List<float>(); var ys = new List<float>(); var cs = new List<double>();
            var plats = new List<(double d, string name)>();
            var revs = new List<double>();
            double at = 0; int prevE = -1; bool prevFwd = true; double prevTo = double.NaN;
            void Add(double x, double y, double c)
            {
                if (xs.Count > 0 && Math.Abs(xs[^1] - x) < 0.01 && Math.Abs(ys[^1] - y) < 0.01) return;
                xs.Add((float)x); ys.Add((float)y); cs.Add(c);
            }
            foreach (var (e, from, to) in parts)
            {
                var E = Graph.Edges[e];
                double len = Math.Abs(to - from);
                bool fwd = to >= from;
                // el mismo tramo, desde donde acabó el anterior y al revés: ahí se invierte la marcha
                if (e == prevE && Math.Abs(from - prevTo) < 0.5 && fwd != prevFwd && len > 0.5) revs.Add(at);
                if (len > 0.5) { prevE = e; prevFwd = fwd; prevTo = to; }
                var p0 = Graph.PointAt(e, from); Add(p0.x, p0.y, at);
                if (fwd) { for (int i = 0; i < E.Cum.Length; i++) if (E.Cum[i] > from && E.Cum[i] < to) Add(E.X[i], E.Y[i], at + E.Cum[i] - from); }
                else { for (int i = E.Cum.Length - 1; i >= 0; i--) if (E.Cum[i] < from && E.Cum[i] > to) Add(E.X[i], E.Y[i], at + from - E.Cum[i]); }
                var p1 = Graph.PointAt(e, to); Add(p1.x, p1.y, at + len);
                double lo = Math.Min(from, to), hi = Math.Max(from, to);
                foreach (var (o, kmh) in E.Limits) if (o >= lo - 0.1 && o <= hi + 0.1) Limits.Add((at + Math.Abs(o - from), kmh));
                foreach (var (o, name) in E.Platforms) if (o >= lo - 0.1 && o <= hi + 0.1) plats.Add((at + Math.Abs(o - from), name));
                at += len;
            }
            _px = xs.ToArray(); _py = ys.ToArray(); _pc = cs.ToArray();
            TotalLen = at;
            PathLat = new double[_px.Length]; PathLon = new double[_px.Length];
            for (int i = 0; i < _px.Length; i++) (PathLat[i], PathLon[i]) = Graph.Unproj(_px[i], _py[i]);
            Limits.Sort((a, b) => a.d.CompareTo(b.d));
            PointDist = pointDist.ToArray();

            // andenes seguidos de la misma estación → una sola parada (a mitad de sus andenes)
            plats.Sort((a, b) => a.d.CompareTo(b.d));
            int k = 0;
            while (k < plats.Count)
            {
                int j = k;
                while (j + 1 < plats.Count && string.Equals(plats[j + 1].name, plats[k].name, StringComparison.OrdinalIgnoreCase) && plats[j + 1].d - plats[j].d < 800) j++;
                string name = plats[k].name;
                bool sched = MatchSchedule(name, out double sArr, out double sDep);
                var s = new Stop
                {
                    Name = name, Dist = (plats[k].d + plats[j].d) / 2,
                    Halt = _haltChoice.TryGetValue(name, out bool h) ? h : Schedule != null ? sched : DefaultHalt,
                    SchedArr = sArr, SchedDep = sDep
                };
                if (oldPassed.TryGetValue(name, out var pa) && s.Dist < 30) { s.Passed = true; s.PassedAt = pa.PassedAt; s.PassedAtPc = pa.PassedAtPc; }
                Stops.Add(s);
                k = j + 1;
            }
            foreach (double r in revs)
                Stops.Add(new Stop { Name = I18n.T("Cambio de sentido"), Dist = r, Halt = true, IsReverse = true });
            Stops.Sort((x, y) => x.Dist.CompareTo(y.Dist));
            // estación término: «Estación · cambio de sentido · Estación» → una sola fila que invierte la marcha
            for (int i = Stops.Count - 3; i >= 0; i--)
                if (!Stops[i].IsReverse && Stops[i + 1].IsReverse && !Stops[i + 2].IsReverse
                    && string.Equals(Stops[i].Name, Stops[i + 2].Name, StringComparison.OrdinalIgnoreCase) && Stops[i + 2].Dist - Stops[i].Dist < 4000)
                {
                    Stops[i].IsReverse = true; Stops[i].Halt = true;
                    Stops.RemoveRange(i + 1, 2);
                }
            Reversal = revs.Count > 0;
            foreach (var st in Stops) (st.Lat, st.Lon) = LatLonAt(st.Dist);
            // el final del itinerario, si no es una estación
            if (Stops.Count == 0 || TotalLen - Stops[^1].Dist > 300)
            {
                var (la, lo) = LatLonAt(TotalLen);
                var dest = new Stop { Name = I18n.T("Destino"), Dist = TotalLen, Halt = true, IsEnd = true, Lat = la, Lon = lo };
                // Con horario: si su última estación no ha salido en el itinerario (el recorrido acaba justo antes de
                // su andén), el destino es ella, con su hora.
                if (Schedule != null && Schedule.Count > 0)
                {
                    var last = Schedule[^1];
                    string ln = NormName(last.name);
                    bool seen = Stops.Any(st => st.HasSched && NormName(st.Name) is string sn && (sn == ln || sn.Contains(ln) || ln.Contains(sn)));
                    if (!seen) { dest.Name = last.name; dest.SchedArr = last.arr; dest.SchedDep = last.dep; }
                }
                Stops.Add(dest);
            }
            else { Stops[^1].IsEnd = true; Stops[^1].Halt = true; }   // el destino es esa estación
            Version++;
        }

        // Punto del itinerario a esa distancia del origen.
        public (double lat, double lon) LatLonAt(double d)
        {
            if (_pc.Length == 0) return (0, 0);
            int i = Array.BinarySearch(_pc, d); if (i < 0) i = ~i - 1;
            i = Math.Max(0, Math.Min(_pc.Length - 2, i));
            double span = _pc[i + 1] - _pc[i], t = span > 0 ? Math.Max(0, Math.Min(1, (d - _pc[i]) / span)) : 0;
            return Graph.Unproj(_px[i] + (_px[i + 1] - _px[i]) * t, _py[i] + (_py[i + 1] - _py[i]) * t);
        }

        // Dónde va el tren sobre el itinerario (se busca cerca de donde iba, para no saltar a otra vía).
        public void Track(bool hasPos, double lat, double lon, double gameNow)
        {
            if (!HasPlan || !hasPos || Graph == null) return;
            var (x, y) = Graph.Proj(lat, lon);
            double lo = Progress - 300, hi = Progress + 6000, best = double.MaxValue, bestD = Progress;
            for (int i = 0; i + 1 < _px.Length; i++)
            {
                if (_pc[i + 1] < lo || _pc[i] > hi) continue;
                double ax = _px[i], ay = _py[i], dx = _px[i + 1] - ax, dy = _py[i + 1] - ay, l2 = dx * dx + dy * dy;
                double t = l2 > 0 ? Math.Max(0, Math.Min(1, ((x - ax) * dx + (y - ay) * dy) / l2)) : 0;
                double ex = ax + t * dx - x, ey = ay + t * dy - y, d2 = ex * ex + ey * ey;
                if (d2 < best) { best = d2; bestD = _pc[i] + t * (_pc[i + 1] - _pc[i]); }
            }
            bool off = Math.Sqrt(best) > 60;
            if (off && !OffRoute) OffSinceUtc = DateTime.UtcNow;
            OffRoute = off;
            if (off) return;
            OffSinceUtc = DateTime.MaxValue;
            Progress = Math.Max(Progress, bestD);   // el tren no retrocede en la hoja de ruta
            foreach (var s in Stops)
                if (!s.Passed && Progress > s.Dist + 30) { s.Passed = true; s.PassedAt = gameNow; s.PassedAtPc = DateTime.Now; }
            // puntos ya alcanzados: si hay que rehacer el itinerario, no se vuelve a ellos
            for (int i = 0; i < PointDist.Length; i++)
                if (Progress > PointDist[i] - 30 && ReachedPoints < _firstPlanned + i + 1) ReachedPoints = _firstPlanned + i + 1;
        }
        int _firstPlanned;   // índice (en Points) del punto al que corresponde PointDist[0]

        // Diferencia de horas del día en (−12 h, +12 h] (un horario que pasa de medianoche).
        public static double Wrap(double secs) { secs %= 86400; if (secs > 43200) secs -= 86400; if (secs <= -43200) secs += 86400; return secs; }

        // Hora estimada (segundos desde ahora) de cada parada, con los límites de la vía.
        //   curLimit: límite en vigor ahora (del monitor de Open Rails; NaN si no se sabe).
        //   vmax: velocidad máxima del tren (0 = sin dato). dwell: segundos de parada en cada estación.
        public void ComputeEtas(double curLimit, double vmax, double dwell, double gameNow = double.NaN)
        {
            if (!HasPlan) return;
            double cap = vmax > 0 ? vmax : 999;
            double lim = !double.IsNaN(curLimit) && curLimit > 0 ? curLimit : double.NaN;
            int li = 0;
            while (li < Limits.Count && Limits[li].d <= Progress) { if (double.IsNaN(curLimit) || curLimit <= 0) lim = Limits[li].kmh; li++; }
            if (double.IsNaN(lim)) lim = Limits.Count > 0 ? Limits[0].kmh : (vmax > 0 ? vmax : 100);
            double V(double l) => Math.Max(10, Math.Min(l, cap)) / 3.6;
            double t = 0, pos = Progress;
            for (int i = 0; i < Stops.Count; i++)
            {
                var s = Stops[i];
                if (s.Passed) { s.EtaS = double.NaN; continue; }
                while (li < Limits.Count && Limits[li].d < s.Dist)
                {
                    if (Limits[li].d > pos) { t += (Limits[li].d - pos) / V(lim); pos = Limits[li].d; }
                    lim = Limits[li].kmh; li++;
                }
                if (s.Dist > pos) { t += (s.Dist - pos) / V(lim); pos = s.Dist; }
                s.EtaS = t;
                // parada: tiempo parado + lo que se pierde al frenar y arrancar (±0,5 m/s²)
                if (s.IsReverse) { if (s.Dist > Progress + 30) t += Math.Max(ReverseS, dwell) + V(lim); }
                else if (s.Halt && !s.IsEnd && i < Stops.Count - 1 && s.Dist > Progress + 30)
                {
                    // con horario: la parada dura lo programado (mín. 20 s) y no se sale antes de la hora
                    double stay = !double.IsNaN(s.SchedArr) && !double.IsNaN(s.SchedDep) && s.SchedDep > s.SchedArr ? Math.Max(20, s.SchedDep - s.SchedArr) : dwell;
                    t += stay + V(lim);
                    if (!double.IsNaN(gameNow) && !double.IsNaN(s.SchedDep))
                    {
                        double untilDep = Wrap(s.SchedDep - gameNow);
                        if (untilDep > t) t = untilDep;
                    }
                }
            }
        }
    }
}
