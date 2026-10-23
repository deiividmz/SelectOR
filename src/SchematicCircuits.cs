// PUESTO DE MANDO, videográfico (1.2.75): los cantones (cada vía partida en sus señales), cuáles están ocupados por un
// tren y por dónde va el itinerario de cada señal abierta (de la señal a la siguiente en el mismo sentido, siguiendo las
// agujas como están). Todo en metros de cada tramo del .tdb.

using System;
using System.Collections.Generic;
using System.Linq;

namespace SelectOR
{
    public static class SchematicCircuits
    {
        // las señales de cada tramo: metro, sentido en el que rigen (+1 de A a B, −1 de B a A) y número
        public static Dictionary<int, List<(double off, int dir, int obj)>> SignalsByEdge(RouteGraph g, RoomMapGeo geo)
        {
            var r = new Dictionary<int, List<(double, int, int)>>();
            foreach (var sg in geo.Signals)
            {
                if (sg.Edge < 0 || double.IsNaN(sg.Hdg)) continue;
                double L = g.Edges[sg.Edge].Len;
                var (x1, y1) = g.PointAt(sg.Edge, Math.Max(0, sg.Off - 3)); var (x2, y2) = g.PointAt(sg.Edge, Math.Min(L, sg.Off + 3));
                int dir = Math.Cos(sg.Hdg) * (x2 - x1) + Math.Sin(sg.Hdg) * (y2 - y1) >= 0 ? 1 : -1;
                if (!r.TryGetValue(sg.Edge, out var l)) r[sg.Edge] = l = new();
                l.Add((sg.Off, dir, sg.Obj));
            }
            return r;
        }

        // los cantones de un esquema: cada tramo dibujado, partido en sus señales
        public static List<(int e, double a, double b)> Circuits(Schematic s)
        {
            var cuts = new Dictionary<int, List<double>>();
            foreach (var sg in s.Signals) if (sg.Sig != null && sg.Sig.Edge >= 0) { if (!cuts.TryGetValue(sg.Sig.Edge, out var l)) cuts[sg.Sig.Edge] = l = new(); l.Add(sg.Sig.Off); }
            var res = new List<(int, double, double)>();
            foreach (int e in s.DrawnEdges)
            {
                if (!s.DrawnRange(e, out double a, out double b)) continue;
                var pts = new List<double> { a, b };
                if (cuts.TryGetValue(e, out var c)) pts.AddRange(c.Where(o => o > a + 1 && o < b - 1));
                pts.Sort();
                for (int i = 1; i < pts.Count; i++) if (pts[i] - pts[i - 1] > 0.5) res.Add((e, pts[i - 1], pts[i]));
            }
            return res;
        }

        // ── (1.2.75) Itinerarios: los destinos posibles desde una señal. Por la vía en su sentido, en cada aguja de punta las dos
        // ramas y en cada una de talón la posición que hace falta, hasta la siguiente señal en el mismo sentido (su destino) o
        // una topera (destino −1). Cada uno con sus agujas (nodo del .tdb, posición: la rama Out[pos], como THROWNODE), los
        // trozos de vía (para dibujarlo) y dónde acaba.
        public sealed class ItinOption
        {
            public int Dest = -1;                                          // señal (−1: topera)
            public List<(int node, int pos)> Switches = new();
            public List<(int e, double a, double b)> Pieces = new();
            public int EndEdge; public double EndOff; public double Len;
            public bool Mine;                                              // va por el itinerario de tu hoja de ruta
        }

        public static List<ItinOption> Options(RouteGraph g, Dictionary<int, List<(double off, int dir, int obj)>> sbe, int e, double off, int dir, double maxM = 3000)
        {
            var res = new List<ItinOption>();
            void Go(int ce, double cur, int cd, double acc, List<(int, int)> sw, List<(int, double, double)> pcs, int depth)
            {
                if (res.Count >= 24 || depth > 60) return;
                var E = g.Edges[ce]; double end = cd > 0 ? E.Len : 0;
                double? stop = null; int stopObj = -1;
                if (sbe.TryGetValue(ce, out var sl))
                    foreach (var q in sl)
                        if (q.dir == cd && (cd > 0 ? q.off > cur + 0.5 : q.off < cur - 0.5) && (stop == null || (cd > 0 ? q.off < stop : q.off > stop))) { stop = q.off; stopObj = q.obj; }
                double to = stop ?? end;
                var p2 = new List<(int, double, double)>(pcs) { (ce, Math.Min(cur, to), Math.Max(cur, to)) };
                acc += Math.Abs(to - cur);
                if (stop != null) { res.Add(new ItinOption { Dest = stopObj, Switches = new(sw), Pieces = p2, EndEdge = ce, EndOff = to, Len = acc }); return; }
                if (acc > maxM) return;
                int n = cd > 0 ? E.B : E.A;
                if (n < 0 || g.IsDeadEnd(n) || g.EdgesAt(n).Count <= 1) { res.Add(new ItinOption { Dest = -1, Switches = new(sw), Pieces = p2, EndEdge = ce, EndOff = to, Len = acc }); return; }
                void Next(int f, List<(int, int)> s2)
                {
                    int nd = g.Edges[f].A == n ? 1 : -1;
                    Go(f, nd > 0 ? 0 : g.Edges[f].Len, nd, acc, s2, p2, depth + 1);
                }
                if (g.JunctionOf(n, out _, out _, out _, out var outs) && outs != null && outs.Length >= 2)
                {
                    int k = Array.IndexOf(outs, ce);
                    if (k >= 0)
                    {
                        // de talón: la aguja hacia la rama por la que se llega, y sigue por el tronco
                        foreach (int f in g.EdgesAt(n)) if (Array.IndexOf(outs, f) < 0) { Next(f, new(sw) { (n, k) }); break; }
                    }
                    else
                        // de punta: las dos ramas
                        for (int b = 0; b < outs.Length && b < 2; b++) if (outs[b] >= 0) Next(outs[b], new(sw) { (n, b) });
                }
                else foreach (int f in g.EdgesAt(n)) if (f != ce) { Next(f, sw); break; }
            }
            try { Go(e, off, dir, 0, new(), new(), 0); } catch { }
            // (cada destino, una vez: el camino más corto)
            return res.GroupBy(o => o.Dest >= 0 ? "s" + o.Dest : "b" + o.EndEdge).Select(gr => gr.OrderBy(o => o.Len).First()).OrderBy(o => o.Len).ToList();
        }

        // un trozo de vía (tramo, de a b metros) en latitud y longitud, para el mapa grande
        public static List<(double lat, double lon)> PieceLatLon(RouteGraph g, int e, double a, double b)
        {
            var l = new List<(double, double)>(); var E = g.Edges[e];
            if (b < a) (a, b) = (b, a);
            void Add(double o) { var (x, y) = g.PointAt(e, o); l.Add(g.Unproj(x, y)); }
            Add(a);
            for (int i = 0; i < E.Cum.Length; i++) if (E.Cum[i] > a + 0.5 && E.Cum[i] < b - 0.5) Add(E.Cum[i]);
            Add(b);
            return l;
        }

        // Dónde rige una señal: su tramo, metro y sentido (+1 de A a B); false si no se sabe
        public static bool SignalPlace(Dictionary<int, List<(double off, int dir, int obj)>> sbe, RoomMapGeo.Sig sg, out int e, out double off, out int dir)
        {
            e = sg?.Edge ?? -1; off = sg?.Off ?? 0; dir = 0;
            if (sg == null || e < 0 || !sbe.TryGetValue(e, out var l)) return false;
            foreach (var q in l) if (q.obj == sg.Obj) { dir = q.dir; return true; }
            return false;
        }

        // El itinerario desde una señal (tramo e, metro off, sentido dir): hasta la siguiente señal en el mismo sentido, o
        // hasta una aguja que no está hacia él, o 4 km. mark(tramo, de, a). true = pasa por alguna aguja (un itinerario de
        // estación; los cantones de plena vía con la señal abierta van en gris, como en los videográficos).
        public static bool Walk(RouteGraph g, Dictionary<int, List<(double off, int dir, int obj)>> sbe, int e, double off, int dir,
                                Func<int, int> posOfNode, Action<int, double, double> mark, double maxM = 4000)
        {
            double cur = off, acc = 0; int ce = e, cd = dir; bool sw = false;
            for (int guard = 0; guard < 300; guard++)
            {
                var E = g.Edges[ce]; double end = cd > 0 ? E.Len : 0;
                double? stop = null;
                if (sbe.TryGetValue(ce, out var sl))
                    foreach (var q in sl)
                        if (q.dir == cd && (cd > 0 ? q.off > cur + 0.5 : q.off < cur - 0.5) && (stop == null || (cd > 0 ? q.off < stop : q.off > stop))) stop = q.off;
                double to = stop ?? end;
                mark(ce, Math.Min(cur, to), Math.Max(cur, to));
                acc += Math.Abs(to - cur);
                if (stop != null || acc > maxM) return sw;
                int n = cd > 0 ? E.B : E.A; if (n < 0) return sw;
                int next = -1;
                if (g.JunctionOf(n, out _, out _, out _, out var outs) && outs != null && outs.Length >= 2)
                {
                    int pos = posOfNode(n); if (pos < 0 || pos >= outs.Length) return sw;
                    sw = true;
                    if (outs.Contains(ce))
                    {
                        if (outs[pos] != ce) return sw;   // (la aguja no está hacia aquí: el itinerario no sigue)
                        foreach (int f in g.EdgesAt(n)) if (!outs.Contains(f)) { next = f; break; }
                    }
                    else next = outs[pos];
                }
                else foreach (int f in g.EdgesAt(n)) if (f != ce) { next = f; break; }
                if (next < 0) return sw;
                cd = g.Edges[next].A == n ? 1 : -1; cur = cd > 0 ? 0 : g.Edges[next].Len; ce = next;
            }
            return sw;
        }
    }
}
