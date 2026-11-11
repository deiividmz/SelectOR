// (2026-11-10, petición) El PK exacto de un punto de la vía (para los pasos a nivel): por la vía, el hito kilométrico más cercano
// a cada lado (los del .tdb, uno por kilómetro) y lo que hay entre ellos, repartido. Si los dos no son de la misma cuenta (otra
// línea, otro origen) o solo hay hitos a un lado, sigue la cuenta de los dos más cercanos de ese lado. NaN si no se puede.

using System;
using System.Collections.Generic;
using System.Globalization;

namespace SelectOR
{
    public static class PuntoKm
    {
        const double Max = 4000;   // (m) hasta dónde se buscan hitos por cada lado

        // los hitos de un lado del punto (hacia el nudo A del tramo o hacia el B): distancia por la vía y valor
        static List<(double d, double v)> Side(RouteGraph g, int e, double off, bool towardA)
        {
            var res = new List<(double d, double v)>();
            var E = g.Edges[e];
            foreach (var (o, v) in E.MilePosts) if (towardA ? o < off : o > off) res.Add((Math.Abs(o - off), v));
            int n0 = towardA ? E.A : E.B; if (n0 < 0) { res.Sort((p, q) => p.d.CompareTo(q.d)); return res; }
            var seen = new HashSet<int> { e };
            var pq = new PriorityQueue<int, double>(); var dist = new Dictionary<int, double> { [n0] = towardA ? off : E.Len - off };
            pq.Enqueue(n0, dist[n0]);
            while (pq.TryDequeue(out int n, out double d))
            {
                if (d > dist[n] + 1e-9) continue;
                foreach (int x in g.EdgesAt(n))
                {
                    if (!seen.Add(x)) continue;
                    var X = g.Edges[x]; bool fromA = X.A == n;
                    foreach (var (o, v) in X.MilePosts) { double dd = d + (fromA ? o : X.Len - o); if (dd <= Max) res.Add((dd, v)); }
                    int other = fromA ? X.B : X.A; double nd = d + X.Len;
                    if (other >= 0 && nd <= Max && (!dist.TryGetValue(other, out double od) || nd < od)) { dist[other] = nd; pq.Enqueue(other, nd); }
                }
            }
            res.Sort((p, q) => p.d.CompareTo(q.d));
            return res;
        }

        public static double At(RouteGraph g, int e, double off)
        {
            if (g == null || e < 0 || e >= g.Edges.Length) return double.NaN;
            try
            {
                var a = Side(g, e, off, true); var b = Side(g, e, off, false);
                // un hito a cada lado, de la misma cuenta (lo que va de uno a otro es lo que hay por la vía)
                if (a.Count > 0 && b.Count > 0)
                {
                    var (da, va) = a[0]; var (db, vb) = b[0];
                    double km = (da + db) / 1000;
                    if (va != vb && Math.Abs(Math.Abs(vb - va) - km) <= 0.25 * km + 0.15) return va + (vb - va) * da / (da + db);
                    if (va == vb && da + db < 30) return va;   // (justo en el hito)
                }
                // si no, la cuenta de los dos más cercanos de un lado (el lado con el hito más cerca)
                foreach (var s in a.Count > 0 && (b.Count == 0 || a[0].d <= b[0].d) ? new[] { a, b } : new[] { b, a })
                {
                    if (s.Count < 2) continue;
                    for (int k = 1; k < s.Count; k++)
                    {
                        if (s[k].v == s[0].v || s[k].d - s[0].d < 100) continue;
                        double slope = (s[k].v - s[0].v) / (s[k].d - s[0].d);   // km por metro, alejándose
                        if (Math.Abs(Math.Abs(slope) - 0.001) > 0.0004) break;   // (no es la misma línea)
                        return s[0].v - slope * s[0].d;
                    }
                }
            }
            catch { }
            return double.NaN;
        }

        // «12,345» (km con tres decimales: los metros), como en los esquemas de ADIF
        public static string Text(double pk) => double.IsNaN(pk) ? null : Math.Max(0, pk).ToString("0.000", I18n.Culture ?? CultureInfo.CurrentCulture);
    }
}
