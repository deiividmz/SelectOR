// La vía que el tren tiene POR DELANTE, para la megafonía. Se sitúa el tren sobre el trazado real de la
// ruta (.tdb, con sus curvas), se sabe hacia dónde va por su rastro y se recorre la vía hacia delante
// —atravesando los desvíos— apuntando las marcas de andén que aparecen y a cuántos metros de vía están.
// Así solo cuentan las estaciones a las que el tren puede llegar por SU vía (una de una línea paralela o
// de otro ramal que no conecta no aparece nunca), y la distancia es la de la vía, también en curva.
//  · En un desvío que se toma de punta (el tren puede ir por las dos ramas) se siguen las dos; cada
//    estación lleva anotadas las ramas por las que se llega a ella (Route), para que quien lo use pueda
//    esperar a pasar el desvío si dos estaciones distintas dependen de por dónde vaya.
//  · Qué pata de cada desvío es la «de entrada» (la que va a las otras dos) se saca de la geometría: es la
//    que sale en sentido contrario a las otras dos. No depende del orden de los datos del .tdb.
//  · La posición de Open Rails (lat/lon, a saltos de ~1–2 m) se pasa a metros con conversiones locales
//    calibradas (Andenes.Frame), una por cada casilla de 500 m, calculadas cuando hacen falta.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SelectOR
{
    public sealed class ViaAdelante
    {
        public const double OnTrackM = 3.0;    // a más de esto de cualquier vía: el tren no se sitúa
        const double CellM = 200, FrameCellM = 500;

        public sealed class Hit { public string Key; public double Dist; public string Route; }

        Orts.Formats.Msts.TrackNode[] _tn;
        List<(double x, double z)>[] _pts;     // vía de cada nodo, del extremo del pin 0 al del pin 1
        double[][] _acc;                        // metros acumulados en cada punto
        List<(double s, string key)>[] _marks;  // marcas de andén de cada nodo (s = metros desde el pin 0)
        readonly Dictionary<long, List<(int node, int seg)>> _grid = new();
        readonly Dictionary<int, int> _trunk = new();   // desvío → nodo de su pata de entrada
        readonly List<(double lat, double lon, double x, double z)> _anchors = new();
        readonly Dictionary<long, GeoFrame> _frames = new();
        int _lastNode = -1;

        public HashSet<string> Stations { get; } = new(StringComparer.OrdinalIgnoreCase);

        // null si no se puede (sin .tdb, sin la conversión de Open Rails o sin andenes).
        public static ViaAdelante Load(string routeDir, Func<string, string> norm)
        {
            try
            {
                if (string.IsNullOrEmpty(routeDir) || !OrGeo.Available) return null;
                var tdb = Directory.GetFiles(routeDir, "*.tdb");
                if (tdb.Length == 0) return null;
                var db = new Orts.Formats.Msts.TrackDatabaseFile(tdb[0]);
                var tn = db.TrackDB?.TrackNodes;
                var items = db.TrackDB?.TrItemTable;
                if (tn == null || items == null) return null;
                var tsec = TrackGeometry.Sections(routeDir);
                var v = new ViaAdelante { _tn = tn };
                int n = tn.Length;
                v._pts = new List<(double x, double z)>[n]; v._acc = new double[n][]; v._marks = new List<(double s, string key)>[n];

                for (int i = 0; i < n; i++)
                {
                    var node = tn[i];
                    if (node?.TrVectorNode?.TrVectorSections == null || node.TrVectorNode.TrVectorSections.Length == 0) continue;
                    var pts = Andenes.NodePath(tn, i, tsec);
                    if (pts.Count < 2) continue;
                    // Orientación: el principio de la lista tiene que ser el extremo del pin 0.
                    if (node.TrPins != null && node.TrPins.Length >= 2)
                    {
                        var e0 = EndPos(tn, node.TrPins[0].Link); var e1 = EndPos(tn, node.TrPins[1].Link);
                        double a0 = e0 == null ? double.NaN : Dist(e0.Value, pts[0]), b0 = e0 == null ? double.NaN : Dist(e0.Value, pts[^1]);
                        double a1 = e1 == null ? double.NaN : Dist(e1.Value, pts[0]), b1 = e1 == null ? double.NaN : Dist(e1.Value, pts[^1]);
                        bool reverse = (!double.IsNaN(a0) && a0 > b0) || (double.IsNaN(a0) && !double.IsNaN(a1) && a1 < b1);
                        if (reverse) pts.Reverse();
                    }
                    var acc = new double[pts.Count];
                    for (int k = 1; k < pts.Count; k++) acc[k] = acc[k - 1] + Dist(pts[k - 1], pts[k]);
                    v._pts[i] = pts; v._acc[i] = acc;
                    for (int k = 0; k + 1 < pts.Count; k++) v.AddToGrid(i, k);
                }

                // Pata de entrada de cada desvío, por geometría
                for (int j = 0; j < n; j++)
                {
                    var J = tn[j];
                    if (J?.TrJunctionNode == null || J.TrPins == null || J.TrPins.Length < 3) continue;
                    var legs = new List<(int node, double dx, double dz)>();
                    foreach (var pin in J.TrPins.Take(3))
                    {
                        int w = pin.Link;
                        if (w < 0 || w >= n || v._pts[w] == null) continue;
                        var (dx, dz) = v.LegDir(w, j);
                        legs.Add((w, dx, dz));
                    }
                    if (legs.Count != 3) continue;
                    int best = -1; double bs = double.MaxValue;
                    for (int a = 0; a < 3; a++)
                    {
                        double sc = 0;
                        for (int b = 0; b < 3; b++) if (b != a) sc += legs[a].dx * legs[b].dx + legs[a].dz * legs[b].dz;
                        if (sc < bs) { bs = sc; best = legs[a].node; }
                    }
                    v._trunk[j] = best;
                }

                // Marcas de andén sobre su nodo
                var nodeOf = new Dictionary<long, int>();
                for (int i = 0; i < n; i++)
                {
                    var refs = tn[i]?.TrVectorNode?.TrItemRefs;
                    if (refs != null) foreach (var r in refs) nodeOf[(long)r] = i;
                }
                foreach (var it in items)
                {
                    if (!(it is Orts.Formats.Msts.PlatformItem p) || string.IsNullOrWhiteSpace(p.Station)) continue;
                    string key = norm(p.Station);
                    if (string.IsNullOrEmpty(key) || !nodeOf.TryGetValue(p.TrItemId, out int ni) || v._pts[ni] == null) continue;
                    double x = p.TileX * 2048.0 + p.X, z = p.TileZ * 2048.0 + p.Z;
                    double s = Andenes.Along(v._pts[ni], x, z, out double d);
                    if (d > 5) continue;
                    (v._marks[ni] ??= new List<(double s, string key)>()).Add((s, key));
                    v.Stations.Add(key);
                    if (OrGeo.TryLatLon(x, z, out double la, out double lo)) v._anchors.Add((la, lo, x, z));
                }
                if (v.Stations.Count == 0 || v._anchors.Count == 0) return null;
                foreach (var m in v._marks) m?.Sort((a, b) => a.s.CompareTo(b.s));
                return v;
            }
            catch { return null; }
        }

        static double Dist((double x, double z) a, (double x, double z) b) => Math.Sqrt((a.x - b.x) * (a.x - b.x) + (a.z - b.z) * (a.z - b.z));

        static (double x, double z)? EndPos(Orts.Formats.Msts.TrackNode[] tn, int j)
        {
            if (j < 0 || j >= tn.Length) return null;
            var u = tn[j]?.UiD;
            if (u == null) return null;
            return (u.TileX * 2048.0 + u.X, u.TileZ * 2048.0 + u.Z);
        }

        // Rumbo con el que el nodo w sale del desvío j (unos 15 m hacia dentro del nodo).
        (double dx, double dz) LegDir(int w, int j)
        {
            var pts = _pts[w]; var acc = _acc[w];
            bool atStart = _tn[w].TrPins != null && _tn[w].TrPins.Length >= 1 && _tn[w].TrPins[0].Link == j;
            if (_tn[w].TrPins != null && _tn[w].TrPins.Length >= 2 && _tn[w].TrPins[0].Link == j && _tn[w].TrPins[1].Link == j) atStart = true;
            double len = acc[^1], d = Math.Min(15, len);
            var a = atStart ? PointAt(w, 0) : PointAt(w, len);
            var b = atStart ? PointAt(w, d) : PointAt(w, len - d);
            double dx = b.x - a.x, dz = b.z - a.z, L = Math.Sqrt(dx * dx + dz * dz);
            return L > 0 ? (dx / L, dz / L) : (0, 0);
        }

        (double x, double z) PointAt(int w, double s)
        {
            var pts = _pts[w]; var acc = _acc[w];
            for (int k = 0; k + 1 < pts.Count; k++)
                if (s <= acc[k + 1] || k + 2 == pts.Count)
                {
                    double L = acc[k + 1] - acc[k], t = L > 0 ? Math.Max(0, Math.Min(1, (s - acc[k]) / L)) : 0;
                    return (pts[k].x + (pts[k + 1].x - pts[k].x) * t, pts[k].z + (pts[k + 1].z - pts[k].z) * t);
                }
            return pts[^1];
        }

        static long CellKey(long cx, long cz) => (cx << 32) ^ (cz & 0xffffffffL);

        void AddToGrid(int node, int seg)
        {
            var a = _pts[node][seg]; var b = _pts[node][seg + 1];
            long x0 = (long)Math.Floor((Math.Min(a.x, b.x) - OnTrackM) / CellM), x1 = (long)Math.Floor((Math.Max(a.x, b.x) + OnTrackM) / CellM);
            long z0 = (long)Math.Floor((Math.Min(a.z, b.z) - OnTrackM) / CellM), z1 = (long)Math.Floor((Math.Max(a.z, b.z) + OnTrackM) / CellM);
            if ((x1 - x0 + 1) * (z1 - z0 + 1) > 400) return;   // tramo absurdo (datos rotos)
            for (long cx = x0; cx <= x1; cx++)
                for (long cz = z0; cz <= z1; cz++)
                {
                    long k = CellKey(cx, cz);
                    if (!_grid.TryGetValue(k, out var l)) _grid[k] = l = new List<(int, int)>();
                    l.Add((node, seg));
                }
        }

        // ---------------- lat/lon de Open Rails → metros ----------------

        GeoFrame FrameAt(double x, double z)
        {
            long cx = (long)Math.Floor(x / FrameCellM), cz = (long)Math.Floor(z / FrameCellM);
            long k = CellKey(cx, cz);
            if (_frames.TryGetValue(k, out var f)) return f;
            f = Andenes.Frame((cx + 0.5) * FrameCellM, (cz + 0.5) * FrameCellM);
            _frames[k] = f;
            return f;
        }

        static (double x, double z) Apply(GeoFrame f, double lat, double lon)
        {
            double dl = lat - f.OLat, dn = lon - f.OLon;
            return (f.OX + f.Jxl * dl + f.Jxn * dn, f.OZ + f.Jzl * dl + f.Jzn * dn);
        }

        public bool ToWorld(double lat, double lon, out double x, out double z)
        {
            x = z = double.NaN;
            if (_anchors.Count == 0) return false;
            double cs = Math.Cos(lat * Math.PI / 180.0), bd = double.MaxValue; int bi = 0;
            for (int i = 0; i < _anchors.Count; i++)
            {
                double a = _anchors[i].lat - lat, b = (_anchors[i].lon - lon) * cs, d = a * a + b * b;
                if (d < bd) { bd = d; bi = i; }
            }
            var f = FrameAt(_anchors[bi].x, _anchors[bi].z);
            if (f == null) return false;
            (x, z) = Apply(f, lat, lon);
            // Afinar con la conversión de la casilla donde cae de verdad (la proyección cambia con la distancia)
            for (int it = 0; it < 2; it++)
            {
                var g = FrameAt(x, z);
                if (g == null || ReferenceEquals(g, f)) break;
                f = g; (x, z) = Apply(f, lat, lon);
            }
            return !double.IsNaN(x);
        }

        // ---------------- situar el tren y mirar hacia delante ----------------

        // Nodo y punto de la vía más cercanos (a OnTrackM o menos). Se prefiere el nodo de la vez anterior
        // si sigue valiendo: junto a un desvío dos vías se tocan y así no salta de una a otra.
        bool Locate(double x, double z, out int node, out int seg, out double s)
        {
            node = -1; seg = -1; s = 0;
            long cx = (long)Math.Floor(x / CellM), cz = (long)Math.Floor(z / CellM);
            if (!_grid.TryGetValue(CellKey(cx, cz), out var l)) return false;
            double best = double.MaxValue, bestLast = double.MaxValue; int ls = -1; double lsS = 0;
            foreach (var (w, k) in l)
            {
                var a = _pts[w][k]; var b = _pts[w][k + 1];
                double dx = b.x - a.x, dz = b.z - a.z, L2 = dx * dx + dz * dz;
                double t = L2 > 0 ? Math.Max(0, Math.Min(1, ((x - a.x) * dx + (z - a.z) * dz) / L2)) : 0;
                double px = a.x + t * dx - x, pz = a.z + t * dz - z, d = Math.Sqrt(px * px + pz * pz);
                double sAt = _acc[w][k] + t * Math.Sqrt(L2);
                if (d < best) { best = d; node = w; seg = k; s = sAt; }
                if (w == _lastNode && d < bestLast) { bestLast = d; ls = k; lsS = sAt; }
            }
            if (bestLast <= OnTrackM) { node = _lastNode; seg = ls; s = lsS; best = bestLast; }
            if (best > OnTrackM) { node = -1; return false; }
            _lastNode = node;
            return true;
        }

        // Estaciones por delante del tren, por su vía, hasta maxM metros. (x, z): el tren; (mx, mz): hacia
        // dónde se ha movido (de un punto de su rastro a él). located = false si no se sitúa sobre la vía.
        public List<Hit> Scan(double x, double z, double mx, double mz, double maxM, out bool located)
        {
            var res = new List<Hit>();
            located = false;
            if (!Locate(x, z, out int node, out int seg, out double s0)) return res;
            var a = _pts[node][seg]; var b = _pts[node][seg + 1];
            double tx = b.x - a.x, tz = b.z - a.z;
            if (mx * mx + mz * mz < 1e-6 || tx * tx + tz * tz < 1e-9) return res;
            located = true;
            int dir0 = mx * tx + mz * tz >= 0 ? 1 : -1;

            // Por orden de distancia: cada tramo se recorre una vez, por el camino más corto.
            var queue = new PriorityQueue<(int node, double s, int dir, double base_, string route), double>();
            var seen = new HashSet<(int, int)>();
            queue.Enqueue((node, s0, dir0, 0, ""), 0);
            int guard = 0;
            while (queue.Count > 0 && guard++ < 2000)
            {
                var (w, s, dir, base_, route) = queue.Dequeue();
                if (!seen.Add((w, dir))) continue;
                var marks = _marks[w];
                if (marks != null)
                    foreach (var (ms, key) in marks)
                    {
                        double d = dir > 0 ? ms - s : s - ms;
                        if (d <= 0.5) continue;                     // por detrás (o justo encima)
                        if (base_ + d <= maxM) res.Add(new Hit { Key = key, Dist = base_ + d, Route = route });
                    }
                double len = _acc[w][^1];
                double nb = base_ + (dir > 0 ? len - s : s);
                if (nb > maxM) continue;
                var pins = _tn[w].TrPins;
                if (pins == null || pins.Length < 2) continue;
                int j = dir > 0 ? pins[1].Link : pins[0].Link;
                if (j < 0 || j >= _tn.Length) continue;
                var J = _tn[j];
                if (J == null) continue;
                var next = new List<(int w, string route)>();
                if (J.TrJunctionNode != null && _trunk.TryGetValue(j, out int trunk))
                {
                    if (trunk == w)
                    {
                        // De punta: el tren puede ir por cualquiera de las dos ramas.
                        int leg = 0;
                        foreach (var p in J.TrPins.Take(3))
                            if (p.Link != trunk) next.Add((p.Link, route + j + ":" + (leg++) + "/"));
                    }
                    else next.Add((trunk, route));   // de talón: solo hacia la pata de entrada
                }
                else if (J.TrPins != null)
                    foreach (var p in J.TrPins) if (p.Link != w) next.Add((p.Link, route));   // otros enlaces (raros)
                foreach (var (nw, nr) in next)
                {
                    if (nw < 0 || nw >= _tn.Length || _pts[nw] == null) continue;
                    var np = _tn[nw].TrPins;
                    bool atStart = np != null && np.Length >= 1 && np[0].Link == j;
                    queue.Enqueue(atStart ? (nw, 0.0, 1, nb, nr) : (nw, _acc[nw][^1], -1, nb, nr), nb);
                }
            }
            return res;
        }
    }
}
