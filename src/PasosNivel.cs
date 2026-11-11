// (1.2.84, petición) Pasos a nivel en el puesto de mando, con su estado.
//  · Dónde están: los elementos de paso a nivel del .tdb (trXING), cada uno en su vía (como las señales: RoomMapGeo).
//  · Cómo se agrupan y cuándo cierran: el objeto «LevelCr» de los ficheros del mundo (WORLD\*.w), con sus elementos de
//    vía y sus parámetros (tiempo de aviso y distancia mínima). Se leen en segundo plano: hasta entonces, cada elemento
//    suelto y con los valores de Open Rails más comunes en CGL.
//  · Su estado: Open Rails solo lo calcula en quien tiene cargado el 3D de esa zona (el despachador fijo no tiene 3D),
//    con las posiciones de los trenes. Aquí, lo mismo (LevelCrossings.UpdateCrossings): baja las barreras si la cabeza o
//    la cola de un tren está a menos de «aviso × velocidad + distancia mínima + 1 m» (en línea recta), y todo el grupo
//    (las dos vías de un mismo paso) a la vez. Un tren suelto (sin maquinista), solo si está encima (distancia mínima).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Orts.Formats.Msts;

namespace SelectOR
{
    public static class PasosNivel
    {
        public sealed class Cr
        {   // (2026-11-10) con su PK exacto en el rótulo del puesto: «PN · PK 12,345»
            public int Item;                 // el elemento del .tdb
            public int Group = -1;           // el objeto del mundo al que pertenece; −1: no se sabe todavía
            public int Cluster;              // el cruce: los elementos a menos de 40 m entre sí (un cruce puede venir en dos objetos)
            public double Wx, Wz;            // en el mundo (metros)
            public double Lat, Lon;
            public int Edge = -1; public double Off;   // su sitio en su vía
            public float Warn = 30, MinDist = 20;      // (los de Open Rails más comunes en CGL, hasta leer el mundo)
            public double Pk = double.NaN; public bool PkDone;   // (2026-11-10) su PK exacto (PuntoKm), calculado una vez
            public string PkText(RouteGraph g) { if (!PkDone) { Pk = PuntoKm.At(g, Edge, Off); PkDone = true; } return PuntoKm.Text(Pk); }
        }

        // (RoomMapGeo.Build) los de la ruta; el mundo, en segundo plano
        internal static List<Cr> Load(RouteGraph g, string routeDir, TrackDatabaseFile tdb, Action<Dictionary<int, (double x, double z)>> onPosts = null)
        {
            var res = new List<Cr>();
            var items = tdb?.TrackDB?.TrItemTable; var nodes = tdb?.TrackDB?.TrackNodes;
            if (items == null || nodes == null || g == null) return res;
            var vecToEdge = new Dictionary<int, int>();
            for (int i = 0; i < g.Edges.Length; i++) vecToEdge[g.Edges[i].Vec] = i;
            var seen = new HashSet<int>();
            for (int ni = 1; ni < nodes.Length; ni++)
            {
                var refs = nodes[ni]?.TrVectorNode?.TrItemRefs;
                if (refs == null || nodes[ni].TrEndNode) continue;
                foreach (int r in refs)
                {
                    if (r < 0 || r >= items.Length || items[r] == null || items[r].ItemType != TrItem.trItemType.trXING || !seen.Add(r)) continue;
                    var it = items[r];
                    double wx = it.TileX * 2048.0 + it.X, wz = it.TileZ * 2048.0 + it.Z;
                    if (!OrGeo.TryLatLon(wx, wz, out double la, out double lo)) continue;
                    var c = new Cr { Item = r, Wx = wx, Wz = wz, Lat = la, Lon = lo };
                    if (vecToEdge.TryGetValue(ni, out int e) && g.NearestOn(new[] { e }, la, lo, 40, out _, out double off))
                    {
                        c.Edge = e; c.Off = off;
                        var (px, py) = g.PointAt(e, off); (c.Lat, c.Lon) = g.Unproj(px, py);   // sobre la vía
                    }
                    res.Add(c);
                }
            }
            // los cruces: juntos los que están a menos de 40 m (las dos vías de un paso, o sus dos mitades en el mundo)
            for (int i = 0; i < res.Count; i++) res[i].Cluster = i;
            int Find(int i) { while (res[i].Cluster != i) i = res[i].Cluster = res[res[i].Cluster].Cluster; return i; }
            for (int i = 0; i < res.Count; i++)
                for (int j = i + 1; j < res.Count; j++)
                {
                    double dx = res[i].Wx - res[j].Wx, dz = res[i].Wz - res[j].Wz;
                    if (dx * dx + dz * dz < 40 * 40) { int a = Find(i), b = Find(j); if (a != b) res[b].Cluster = a; }
                }
            var roots = Enumerable.Range(0, res.Count).Select(Find).ToArray();
            for (int i = 0; i < res.Count; i++) res[i].Cluster = res[roots[i]].Item;
            // (2026-11-06) en la misma pasada por el mundo, el poste de cada señal (para dibujarla de su lado de la vía)
            if (res.Count > 0 || onPosts != null)
            {
                var byItem = res.ToDictionary(c => c.Item);
                Task.Run(() =>
                {
                    var posts = new Dictionary<int, (double x, double z)>();
                    try { ReadWorld(routeDir, byItem, onPosts != null ? posts : null); } catch { }
                    try { onPosts?.Invoke(posts); } catch { }
                });
            }
            return res;
        }

        static void ReadWorld(string routeDir, Dictionary<int, Cr> byItem, Dictionary<int, (double x, double z)> posts = null)
        {
            string world = Path.Combine(routeDir, "WORLD");
            if (!Directory.Exists(world)) return;
            int group = 0;
            foreach (var w in Directory.GetFiles(world, "*.w"))
            {
                WorldFile wf;
                try { wf = new WorldFile(w); } catch { continue; }
                foreach (var o in wf.Tr_Worldfile)
                {
                    if (posts != null && o is SignalObj so && so.SignalUnits?.Units != null && so.Position != null)
                    {
                        double px = wf.TileX * 2048.0 + so.Position.X, pz = wf.TileZ * 2048.0 + so.Position.Z;
                        foreach (var u in so.SignalUnits.Units) if (u != null) posts[(int)u.TrItem] = (px, pz);
                        continue;
                    }
                    if (o is not LevelCrossingObj lc || lc.trItemIDList == null) continue;
                    int gi = group++;
                    foreach (var id in lc.trItemIDList)
                    {
                        if (id.db != 0 || !byItem.TryGetValue(id.dbID, out var c)) continue;
                        c.Warn = lc.levelCrParameters.warningTime; c.MinDist = lc.levelCrParameters.minimumDistance; c.Group = gi;
                    }
                }
            }
        }

        // Estado de cada paso con los trenes de ahora: los extremos de cada tren (en metros del plano de RouteGraph), su
        // velocidad y si está suelto. Devuelve el conjunto de elementos con las barreras bajadas.
        public static HashSet<int> Closed(IReadOnlyList<Cr> crs, RouteGraph g, List<(double x, double y, double mps, bool loose)> ends)
        {
            var closed = new HashSet<int>();
            if (crs == null || crs.Count == 0 || ends == null || ends.Count == 0) return closed;
            var groupsClosed = new HashSet<int>();
            foreach (var c in crs)
            {
                var (cx, cy) = c.Edge >= 0 ? g.PointAt(c.Edge, c.Off) : g.Proj(c.Lat, c.Lon);
                foreach (var (x, y, mps, loose) in ends)
                {
                    double r = loose ? c.MinDist : c.Warn * Math.Abs(mps) + c.MinDist + 1;
                    double dx = x - cx, dy = y - cy;
                    if (dx * dx + dy * dy <= r * r) { closed.Add(c.Item); if (c.Group >= 0) groupsClosed.Add(c.Group); break; }
                }
            }
            if (groupsClosed.Count > 0) foreach (var c in crs) if (c.Group >= 0 && groupsClosed.Contains(c.Group)) closed.Add(c.Item);
            // (el dibujo junta cada cruce en un símbolo: cerrado si lo está alguno de sus elementos)
            var clusters = new HashSet<int>(crs.Where(c => closed.Contains(c.Item)).Select(c => c.Cluster));
            foreach (var c in crs) if (clusters.Contains(c.Cluster)) closed.Add(c.Item);
            return closed;
        }
    }
}
