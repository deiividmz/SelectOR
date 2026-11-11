using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace SelectOR
{
    // (2026-11-10, vista previa) El esquema de la ruta montado con el esquema de cada estación (el auditado, estilo ADIF) y los
    // tramos de línea entre ellas (Zones), unidos en orden desde la estación de cabecera. Las bandas solo dirían qué se ve.
    public sealed partial class Schematic
    {
        // un punto de unión de una pieza: una punta de vía, con su clave (nudo, o tramo recortado y su lado), el nudo de la ruta
        // desde el que se sigue buscando y los metros hasta él
        sealed class Port { public string Key; public int Node; public double D0; public PointF At; public int Edge = -1, Side; public double Off; }

        List<Port> Ports()
        {
            var l = new List<Port>();
            foreach (var t in Tracks)
            {
                if (t.Pts == null || t.Pts.Length < 2 || t.Edge < 0) continue;
                var E = _g.Edges[t.Edge];
                _offRange.TryGetValue(t.Edge, out var rg);
                bool hasR = _offRange.ContainsKey(t.Edge);
                for (int k = 0; k < 2; k++)
                {
                    if (t.EndKind[k] == 'e') continue;   // (topera)
                    var at = k == 0 ? t.Pts[0] : t.Pts[^1];
                    bool clipped = hasR && (k == 0 ? rg.a > 0.5 : rg.b < E.Len - 0.5);
                    int en = k == 0 ? E.A : E.B;
                    if (clipped)
                        l.Add(new Port { Key = $"e{t.Edge}:{k}", Node = en, D0 = k == 0 ? rg.a : E.Len - rg.b, At = at, Edge = t.Edge, Side = k, Off = k == 0 ? rg.a : rg.b });
                    else
                    {
                        int n = t.EndNode[k]; if (n < 0) continue;
                        if (n != en && t.EndKind[k] != 'g' && t.EndKind[k] != 'j') continue;
                        l.Add(new Port { Key = $"n{n}", Node = n, D0 = 0, At = at });
                    }
                }
            }
            return l;
        }

        public static Schematic RouteZones(RouteGraph g, RoomMapGeo geo, string label, IList<Zone> zones, Action<string> log = null, string start = null, bool merged = false)
        {
            var Z = new List<Zone>(); var S = new List<Schematic>();
            foreach (var z in zones)
            {
                Schematic s = null;
                try { s = Build(g, geo, z); } catch (Exception ex) { log?.Invoke($"{z.Name}: {ex.Message}"); }
                if (s != null && s.Tracks.Count > 0) { Z.Add(z); S.Add(s); }
            }
            int N = S.Count; if (N == 0) return null;
            // 0a) (2026-11-10, petición) la vía de una estación terminal (Lanuza: 4,4 km de vía única desde el Museo), entera en su
            // estación: pasaba en el plano junto al enlace de Filella y la línea de Santa Ana, y esas piezas se quedaban un trozo
            // cada una (cinco trozos, con diagonales de un lado a otro). Terminal = su pieza solo sale al resto por esa vía
            var preSkip = new Dictionary<int, HashSet<int>>();
            if (!merged)
            {
                var ranges = new Dictionary<int, List<(int i, double a, double b)>>();
                for (int i = 0; i < N; i++)
                    foreach (int e in S[i].DrawnEdges)
                        if (S[i].DrawnRange(e, out double a0, out double b0)) { if (!ranges.TryGetValue(e, out var l)) ranges[e] = l = new(); l.Add((i, a0, b0)); }
                foreach (var kv in ranges.Where(kv => kv.Value.Count >= 3 && g.Edges[kv.Key].Len > 1500))
                {
                    int e = kv.Key; double L = g.Edges[e].Len;
                    var atA = kv.Value.Where(t => t.a < 1).ToList(); var atB = kv.Value.Where(t => t.b > L - 1).ToList();
                    if (Environment.GetEnvironmentVariable("SELECTOR_ZONAS_TERM") != null) log?.Invoke($"TERM e{e} {L:0}: " + string.Join(" · ", kv.Value.Select(t => $"{Z[t.i].Name} {t.a:0}..{t.b:0} puertos {string.Join(",", S[t.i].Ports().Select(p => p.Key))}")));
                    if (atA.Count != 1 || atB.Count != 1) continue;
                    foreach (var (term, other, fromA) in new[] { (atB[0], atA[0], false), (atA[0], atB[0], true) })
                    {
                        if (Z[term.i].Line || Z[other.i].Line) continue;
                        var mid = kv.Value.Where(t => t.i != term.i && t.i != other.i).ToList();
                        if (mid.Any(t => !Z[t.i].Line)) continue;   // (solo por tramos de línea: Colomé, estación de paso en la vía del Aeropuerto, se quedaba sin pieza)
                        var de = S[term.i].DrawnEdges;
                        if (S[term.i].Ports().Any(p => p.Edge >= 0 ? p.Edge != e : g.EdgesAt(p.Node).Any(x => x != e && !de.Contains(x)))) continue;   // (solo sale por esa vía)
                        var nz = new Zone { Name = Z[term.i].Name, Line = false, Anchors = Z[term.i].Anchors, Extra = new HashSet<int> { e },
                            ExtraRange = new Dictionary<int, (double lo, double hi)> { [e] = fromA ? (0, other.a) : (other.b, L) } };
                        Schematic s2 = null; try { s2 = Build(g, geo, nz); } catch { }
                        if (s2 == null || !s2.DrawnRange(e, out double c0, out double c1) || c1 - c0 < (fromA ? other.a : L - other.b) - 60
                            || S[term.i].Signals.Any(q => q.Sig != null && !s2.Signals.Any(r => r.Sig == q.Sig))) continue;
                        log?.Invoke($"vía entera de la terminal {Z[term.i].Name}: tramo {e} de {c0:0} a {c1:0} m (la deja {string.Join(", ", kv.Value.Where(t => t.i != term.i && t.i != other.i).Select(t => Z[t.i].Name))})");
                        Z[term.i] = nz; S[term.i] = s2;
                        foreach (var t in mid) { if (!preSkip.TryGetValue(t.i, out var ps)) preSkip[t.i] = ps = new(); ps.Add(e); }
                        break;
                    }
                }
            }
            // 0) (2026-11-10) cada tramo, de una sola pieza: dos estaciones juntas (Museo y Princesa Susana) dibujaban las mismas vías,
            // cada una a su manera, y sus uniones se cruzaban. Lo dibuja la que más tramo saca de él; las demás lo dejan (si se pisan)
            var skip = Enumerable.Range(0, N).Select(i => preSkip.TryGetValue(i, out var ps) ? new HashSet<int>(ps) : new HashSet<int>()).ToList();
            var clip = Enumerable.Range(0, N).Select(_ => new Dictionary<int, (double lo, double hi)>()).ToList();
            {
                var by = new Dictionary<int, List<(int i, double a, double b)>>();
                for (int i = 0; i < N; i++)
                    foreach (int e in S[i].DrawnEdges)
                        if (!skip[i].Contains(e) && S[i].DrawnRange(e, out double a0, out double b0)) { if (!by.TryGetValue(e, out var l)) by[e] = l = new(); l.Add((i, a0, b0)); }
                foreach (var kv in by.Where(kv => kv.Value.Count > 1))
                {
                    var own = kv.Value.OrderByDescending(t => t.b - t.a).ThenBy(t => Z[t.i].Line ? 1 : 0).First();
                    foreach (var t in kv.Value)
                        if (t.i != own.i && t.a >= own.a - 30 && t.b <= own.b + 30) skip[t.i].Add(kv.Key);   // (solo si la otra lo dibuja entero: si no, se perdía lo de más allá, con sus señales)
                    // los que se pisan a medias: cada uno hasta la mitad de lo que comparten
                    var rest = kv.Value.Where(t => !skip[t.i].Contains(kv.Key)).OrderBy(t => t.a).ToList();
                    for (int k = 1; k < rest.Count; k++)
                    {
                        var c0 = rest[k - 1]; var c1 = rest[k];
                        if (c0.b - c1.a <= 5) continue;
                        double m = (c1.a + Math.Min(c0.b, c1.b)) / 2;
                        clip[c0.i][kv.Key] = (clip[c0.i].TryGetValue(kv.Key, out var o0) ? o0.lo : double.MinValue, m);
                        clip[c1.i][kv.Key] = (m, clip[c1.i].TryGetValue(kv.Key, out var o1) ? o1.hi : double.MaxValue);
                    }
                }
                int nre = 0;
                for (int i = 0; i < N; i++)
                {
                    if (skip[i].Count == 0 && clip[i].Count == 0) continue;
                    Schematic s2 = null; try { s2 = BuildWith(g, geo, Z[i], null, skip[i], clip[i]); } catch { }
                    if (s2 != null && s2.Tracks.Count > 0) { S[i] = s2; nre++; }
                }
                // (2026-11-10) sin huecos: si al recortar un tramo compartido ninguna pieza llega ya a un trozo que antes se
                // dibujaba (una pieza dibuja menos de lo que le toca), las de los dos lados lo dibujan sin recortar
                // (2026-11-10) antes, la pieza de al lado alarga lo suyo hasta taparlo: quitar el recorte a todas dejaba a Parque
                // Grande y al Museo dibujando 400 m de las mismas vías, con dos uniones de 2 km por debajo
                {
                    var redo = new HashSet<int>();
                    foreach (var kv in by.Where(kv => kv.Value.Count > 1))
                    {
                        int e = kv.Key;
                        var now = new List<(double a, double b)>();
                        for (int i = 0; i < N; i++) if (S[i].DrawnRange(e, out double a1, out double b1)) now.Add((a1, b1));
                        double lo0 = kv.Value.Min(t => t.a), hi0 = kv.Value.Max(t => t.b);
                        for (double off = lo0; off <= hi0; off += 20)
                        {
                            if (!kv.Value.Any(t => off >= t.a && off <= t.b) || now.Any(t => off >= t.a - 1 && off <= t.b + 1)) continue;
                            // la que lo dibujaba antes y tiene su trozo más cerca
                            // (de las que dibujan algo de él ahora: a Princesa Susana le tocaba un trozo que no saca)
                            var best = kv.Value.Where(t => clip[t.i].ContainsKey(e) && off >= t.a - 1 && off <= t.b + 1 && S[t.i].DrawnRange(e, out _, out _))
                                .Select(t => { S[t.i].DrawnRange(e, out double na, out double nb); return (t, d: Math.Max(0, Math.Max(na - off, off - nb))); })
                                .OrderBy(q => q.d).FirstOrDefault();
                            if (!clip[best.t.i].ContainsKey(e)) continue;   // (ninguna)
                            var c = clip[best.t.i][e]; clip[best.t.i][e] = (Math.Min(c.lo, off - 10), Math.Max(c.hi, off + 10)); redo.Add(best.t.i);
                            now.Add((off - 10, off + 10));
                        }
                    }
                    foreach (int i in redo) { Schematic s2 = null; try { s2 = BuildWith(g, geo, Z[i], null, skip[i], clip[i]); } catch { } if (s2 != null && s2.Tracks.Count > 0) S[i] = s2; }
                    if (redo.Count > 0) log?.Invoke($"huecos al recortar: {redo.Count} piezas alargan lo suyo");
                    if (Environment.GetEnvironmentVariable("SELECTOR_ZONAS_TRAMO") is string dt0 && int.TryParse(dt0, out int de0) && by.TryGetValue(de0, out var dl0))
                        foreach (var t in dl0) { S[t.i].DrawnRange(de0, out double a2, out double b2); log?.Invoke($"TRAMO1 e{de0} {Z[t.i].Name}: antes {t.a:0}..{t.b:0} ahora {a2:0}..{b2:0} clip {(clip[t.i].TryGetValue(de0, out var cq) ? $"{cq.lo:0}..{cq.hi:0}" : "-")}"); }
                    redo.Clear();
                    foreach (var kv in by.Where(kv => kv.Value.Count > 1))
                    {
                        int e = kv.Key;
                        var before = kv.Value.Select(t => (t.a, t.b)).OrderBy(t => t.a).ToList();
                        var now = new List<(double a, double b, int i)>();
                        for (int i = 0; i < N; i++) if (S[i].DrawnRange(e, out double a1, out double b1)) now.Add((a1, b1, i));
                        now = now.OrderBy(t => t.a).ToList();
                        // lo de antes que no cubre nada ahora (cada 20 m)
                        double lo0 = before.Min(t => t.a), hi0 = before.Max(t => t.b);
                        for (double off = lo0; off <= hi0; off += 20)
                        {
                            if (!before.Any(t => off >= t.a && off <= t.b) || now.Any(t => off >= t.a - 1 && off <= t.b + 1)) continue;
                            foreach (var t in kv.Value) if (clip[t.i].ContainsKey(e) && off >= t.a - 1 && off <= t.b + 1) { clip[t.i].Remove(e); redo.Add(t.i); }
                        }
                    }
                    foreach (int i in redo) { Schematic s2 = null; try { s2 = BuildWith(g, geo, Z[i], null, skip[i], clip[i]); } catch { } if (s2 != null && s2.Tracks.Count > 0) S[i] = s2; }
                    if (redo.Count > 0) log?.Invoke($"huecos al recortar: {redo.Count} piezas sin recortar en ese tramo");
                }
                if (Environment.GetEnvironmentVariable("SELECTOR_ZONAS_TRAMO") is string dt && int.TryParse(dt, out int de) && by.TryGetValue(de, out var dl))
                    foreach (var t in dl) { S[t.i].DrawnRange(de, out double a2, out double b2); log?.Invoke($"TRAMO e{de} {Z[t.i].Name}: antes {t.a:0}..{t.b:0} ahora {a2:0}..{b2:0} skip {skip[t.i].Contains(de)} clip {(clip[t.i].TryGetValue(de, out var cq) ? $"{cq.lo:0}..{cq.hi:0}" : "-")}"); }
                log?.Invoke($"tramos repartidos: {skip.Sum(x => x.Count)} quitados y {clip.Sum(x => x.Count)} recortados, en {nre} piezas");
                // (las piezas que se quedan sin nada suyo: fuera)
                var empty = Enumerable.Range(0, N).Where(i => S[i].DrawnEdges.All(e => skip[i].Contains(e))).ToList();
                if (empty.Count > 0)
                {
                    log?.Invoke("piezas sin nada suyo: " + string.Join(", ", empty.Select(i => Z[i].Name)));
                    foreach (int i in empty.OrderByDescending(x => x)) { Z.RemoveAt(i); S.RemoveAt(i); skip.RemoveAt(i); clip.RemoveAt(i); }
                    N = S.Count;
                }
            }

            // 1) las uniones entre piezas: el mismo nudo, el mismo tramo recortado por los dos lados, o por vía que no dibuja nadie (≤ 3 km)
            // conn: (i, clave de i, j, clave de j)
            var conn = new HashSet<(int i, string a, int j, string b)>();
            void Connect()
            {
                conn.Clear();
                var ports = S.Select(s => s.Ports()).ToList();
                var drawn = new HashSet<int>(S.SelectMany(s => s._byEdge.Keys));
                // tramo recortado por los dos lados: punta alta de uno con la baja del otro
                var byEdge = new Dictionary<int, List<(int i, Port p)>>();
                for (int i = 0; i < N; i++) foreach (var p in ports[i]) if (p.Edge >= 0) { if (!byEdge.TryGetValue(p.Edge, out var l)) byEdge[p.Edge] = l = new(); l.Add((i, p)); }
                var claimed = new HashSet<(int, string)>();
                foreach (var kv in byEdge)
                {
                    var hi = kv.Value.Where(q => q.p.Side == 1).ToList(); var lo = kv.Value.Where(q => q.p.Side == 0).ToList();
                    foreach (var h in hi)
                    {
                        var m = lo.Where(q => q.i != h.i && q.p.Off >= h.p.Off - 60).OrderBy(q => q.p.Off).FirstOrDefault();
                        if (m.p == null) continue;
                        if (h.i < m.i) conn.Add((h.i, h.p.Key, m.i, m.p.Key)); else conn.Add((m.i, m.p.Key, h.i, h.p.Key));
                        claimed.Add((h.i, h.p.Key)); claimed.Add((m.i, m.p.Key));
                    }
                }
                // por los nudos
                var owner = new Dictionary<int, List<(int i, Port p)>>();
                for (int i = 0; i < N; i++) foreach (var p in ports[i]) { if (claimed.Contains((i, p.Key))) continue; if (!owner.TryGetValue(p.Node, out var l)) owner[p.Node] = l = new(); l.Add((i, p)); }
                for (int i = 0; i < N; i++)
                    foreach (var p0 in ports[i])
                    {
                        if (claimed.Contains((i, p0.Key))) continue;
                        var dist = new Dictionary<int, double> { [p0.Node] = p0.D0 };
                        var pq = new PriorityQueue<int, double>(); pq.Enqueue(p0.Node, p0.D0);
                        while (pq.TryDequeue(out int n, out double d))
                        {
                            if (d > dist[n] + 1e-9) continue;
                            bool hit = false;
                            if (owner.TryGetValue(n, out var ow))
                                foreach (var (j, pj) in ow)
                                    if (j != i && d + pj.D0 <= 3000) { hit = true; if (i < j) conn.Add((i, p0.Key, j, pj.Key)); }
                            if (hit && n != p0.Node) continue;
                            foreach (int e in g.EdgesAt(n))
                            {
                                if (drawn.Contains(e)) continue;
                                var E = g.Edges[e]; int to = E.A == n ? E.B : E.A; if (to < 0) continue;
                                double nd = d + E.Len; if (nd > 3000) continue;
                                if (!dist.TryGetValue(to, out double od) || nd < od) { dist[to] = nd; pq.Enqueue(to, nd); }
                            }
                        }
                    }
            }
            Connect();
            if (log != null && Environment.GetEnvironmentVariable("SELECTOR_ZONAS_DENTRO") != null)
            {
                var pts0 = S.Select(q => q.Ports()).ToList();
                foreach (var (i, a, j, b) in conn)
                    foreach (var (x, kx, y, ky) in new[] { (i, a, j, b), (j, b, i, a) })
                    {
                        var bb = S[x].Bounds; var pp = pts0[x].Where(p => p.Key == kx).ToList(); if (pp.Count == 0) continue;
                        float f = (pp[0].At.X - bb.Left) / Math.Max(1, bb.Width);
                        log($"DENTRO {S[x].Station} [{(Z[x].Line ? "L" : "E")}] {kx} → {S[y].Station} [{(Z[y].Line ? "L" : "E")}] {ky}: {f:0.00}");
                    }
            }
            // (2026-11-10, v3) las piezas que empalman por dentro (un tramo «Junto a» que entra por la mitad de su estación, dos
            // estaciones entrelazadas): una sola pieza. Separadas, sus uniones tenían que cruzar vías o dar la vuelta. Solo dentro de
            // la misma banda del puesto (la pieza se queda con el nombre de una de ellas)
            if (!merged)
            {
                var pts0 = S.Select(q => q.Ports()).ToList();
                float Frac(int x, string k) { var bb = S[x].Bounds; var pp = pts0[x].Where(p => p.Key == k).ToList(); return pp.Count == 0 ? 0 : (pp[0].At.X - bb.Left) / Math.Max(1, bb.Width); }
                var pairStat = new Dictionary<(int, int), (int c1, int c2)>();
                foreach (var (i, a, j, b) in conn)
                {
                    float fa = Frac(i, a), fb = Frac(j, b);
                    bool In(float f, float lo) => f > lo && f < 1 - lo;
                    var key = (Math.Min(i, j), Math.Max(i, j)); pairStat.TryGetValue(key, out var st);
                    pairStat[key] = (st.c1 + (In(fa, 0.15f) || In(fb, 0.15f) ? 1 : 0), st.c2 + (In(fa, 0.3f) || In(fb, 0.3f) ? 1 : 0));
                }
                bool SameBand(int i, int j) => !Bandas.Of.TryGetValue(Z[i].Name, out int bi) || !Bandas.Of.TryGetValue(Z[j].Name, out int bj) || bi == bj;
                var grp = Enumerable.Range(0, N).ToArray(); int Find(int x) { while (grp[x] != x) x = grp[x] = grp[grp[x]]; return x; }
                foreach (var ((i, j), (c1, c2)) in pairStat)
                {
                    bool both = !Z[i].Line && !Z[j].Line;
                    // (y que vayan en la misma dirección en el plano: Parque Grande va casi en perpendicular al Museo, y juntas sus
                    // vías salían de pie, sin andenes)
                    double ang = Math.Abs(Math.Atan2(S[i]._ay, S[i]._ax) - Math.Atan2(S[j]._ay, S[j]._ax)) % Math.PI; ang = Math.Min(ang, Math.PI - ang) * 180 / Math.PI;
                    if (both && ang > 25) { log?.Invoke($"no se juntan (en ángulo, {ang:0}°): {Z[i].Name} + {Z[j].Name}"); continue; }
                    // (ni, a través de un tramo, dos estaciones en ángulo: Lanuza con Parque Grande se quedaba sin sus vías)
                    bool Angled(int x, int y) { double a2 = Math.Abs(Math.Atan2(S[x]._ay, S[x]._ax) - Math.Atan2(S[y]._ay, S[y]._ax)) % Math.PI; return Math.Min(a2, Math.PI - a2) * 180 / Math.PI > 25; }
                    var stI = Enumerable.Range(0, N).Where(k => !Z[k].Line && Find(k) == Find(i)).ToList(); var stJ = Enumerable.Range(0, N).Where(k => !Z[k].Line && Find(k) == Find(j)).ToList();
                    if (Find(i) != Find(j) && stI.Any(x => stJ.Any(y => Angled(x, y)))) { log?.Invoke($"no se juntan (estaciones en ángulo): {Z[i].Name} + {Z[j].Name}"); continue; }
                    if ((both ? c2 >= 2 : c1 >= 2 || c2 >= 1) && SameBand(i, j)) { grp[Find(i)] = Find(j); log?.Invoke($"juntas: {Z[i].Name} + {Z[j].Name} ({c1}/{c2})"); }
                }
                var groups = Enumerable.Range(0, N).GroupBy(Find).Where(q => q.Count() > 1).Select(q => q.ToList()).ToList();
                if (groups.Count > 0)
                {
                    var inG = new HashSet<int>(groups.SelectMany(q => q));
                    var nz = new List<Zone>();
                    for (int k = 0; k < N; k++) if (!inG.Contains(k)) nz.Add(Z[k]);   // (las de ahora: con la vía entera de las terminales y sin las vacías)
                    foreach (var gq in groups)
                    {
                        var sts = gq.Where(k => !Z[k].Line).OrderByDescending(k => S[k].Tracks.Count).ToList();
                        var extra = new HashSet<int>(gq.SelectMany(k => S[k].DrawnEdges));
                        var xrange = new Dictionary<int, (double lo, double hi)>();
                        foreach (int k in gq) foreach (int e in S[k].DrawnEdges) if (S[k].DrawnRange(e, out double a0, out double b0)) xrange[e] = xrange.TryGetValue(e, out var o) ? (Math.Min(o.lo, a0), Math.Max(o.hi, b0)) : (a0, b0);
                        Zone mz;
                        if (sts.Count > 0) mz = new Zone { Name = Z[sts[0]].Name, Line = false, Extra = extra, Also = sts.Count > 1 ? sts.Skip(1).Select(k => Z[k].Name).ToList() : null, ExtraRange = xrange };
                        else
                        {
                            int m0 = gq.OrderByDescending(k => S[k].Tracks.Count).First();
                            mz = new Zone { Name = Z[m0].Name, Line = true, Anchors = gq.SelectMany(k => Z[k].Anchors ?? new List<(double x, double y)>()).ToList(), Skip = Z[m0].Skip, Extra = extra, ExtraRange = xrange };
                        }
                        mz.Members = gq.SelectMany(k => Z[k].Members ?? new List<string> { Z[k].Name }).Distinct().ToList();
                        // (solo si la pieza unida dibuja todo lo de sus piezas: si no, cada una por su lado)
                        Schematic ms = null; try { ms = Build(g, geo, mz); } catch { }
                        var msSig = ms == null ? new HashSet<RoomMapGeo.Sig>() : new HashSet<RoomMapGeo.Sig>(ms.Signals.Select(q => q.Sig));
                        var lost = gq.SelectMany(k => S[k].Signals).Where(q => q.Sig != null && !msSig.Contains(q.Sig)).ToList();
                        var lostE = gq.SelectMany(k => S[k].DrawnEdges).Where(e => ms == null || !ms.DrawnEdges.Contains(e)).Distinct().ToList();
                        if (ms == null || lost.Count > 0 || lostE.Count > 0)
                        {
                            foreach (int k in gq) nz.Add(Z[k]);
                            log?.Invoke($"no se unen {string.Join(" + ", gq.Select(k => Z[k].Name))}: se perderían {lost.Count} señales y {lostE.Count} tramos");
                            continue;
                        }
                        nz.Add(mz);
                        log?.Invoke($"pieza unida «{mz.Name}»: {string.Join(" + ", gq.Select(k => Z[k].Name))}");
                    }
                    return RouteZones(g, geo, label, nz, log, start, true);
                }
            }
            bool Adj(int i, int j) => conn.Any(c => (c.i == i && c.j == j) || (c.i == j && c.j == i));
            List<(string mine, string other)> Joints(int i, int j) => conn.Where(c => c.i == i && c.j == j).Select(c => (c.a, c.b)).Concat(conn.Where(c => c.i == j && c.j == i).Select(c => (c.b, c.a))).ToList();
            double Len(int i) => Math.Max(50, S[i].Bounds.Width);

            // 2) la línea principal: de la cabecera, el camino más corto hasta la pieza más alejada
            int first = start == null ? 0 : Math.Max(0, S.FindIndex(s => s.Station == start));
            var dd = new Dictionary<int, double> { [first] = 0 }; var pv = new Dictionary<int, int>(); var pqb = new PriorityQueue<int, double>(); pqb.Enqueue(first, 0);
            while (pqb.TryDequeue(out int a0, out double d0))
            {
                if (d0 > dd[a0] + 1e-9) continue;
                for (int j = 0; j < N; j++) if (j != a0 && Adj(a0, j)) { double nd = d0 + Len(j); if (!dd.TryGetValue(j, out double od) || nd < od) { dd[j] = nd; pv[j] = a0; pqb.Enqueue(j, nd); } }
            }
            int far = dd.OrderByDescending(kv => kv.Value).First().Key;
            var chain = new List<int>(); for (int c = far; ; c = pv[c]) { chain.Insert(0, c); if (!pv.ContainsKey(c)) break; }
            // (por la vía de verdad) de la cabecera a la estación más alejada por la vía: las piezas que dibujan ese camino, en orden
            // (saltando de pieza en pieza se iba por los tramos de línea y se dejaba fuera las estaciones de en medio)
            List<(int e, int from)> pathE = null;
            if (start != null)
            {
                var owner = new Dictionary<int, int>();
                for (int i = 0; i < N; i++) foreach (int e in S[i]._byEdge.Keys) if (!owner.ContainsKey(e) || (!Z[i].Line && Z[owner[e]].Line)) owner[e] = i;
                var dist = new Dictionary<int, double>(); var prevE = new Dictionary<int, (int from, int e)>(); var pq = new PriorityQueue<int, double>();
                for (int e = 0; e < g.Edges.Length; e++)
                    if (g.Edges[e].Platforms.Any(q => q.name == start))
                        foreach (int n in new[] { g.Edges[e].A, g.Edges[e].B }) if (n >= 0 && !dist.ContainsKey(n)) { dist[n] = 0; pq.Enqueue(n, 0); }
                while (pq.TryDequeue(out int n, out double d))
                {
                    if (d > dist[n] + 1e-9) continue;
                    foreach (int e in g.EdgesAt(n))
                    {
                        var E = g.Edges[e]; int to = E.A == n ? E.B : E.A; if (to < 0) continue;
                        double nd = d + E.Len; if (!dist.TryGetValue(to, out double od) || nd < od) { dist[to] = nd; prevE[to] = (n, e); pq.Enqueue(to, nd); }
                    }
                }
                // la estación más alejada (por sus andenes)
                int farNode = -1; double farD = -1;
                foreach (var st in Stations(g))
                {
                    if (st.name == start) continue;
                    double best = double.MaxValue; int bn = -1;
                    for (int e = 0; e < g.Edges.Length; e++)
                        if (g.Edges[e].Platforms.Any(q => q.name == st.name))
                            foreach (int n in new[] { g.Edges[e].A, g.Edges[e].B }) if (n >= 0 && dist.TryGetValue(n, out double dn) && dn < best) { best = dn; bn = n; }
                    if (bn >= 0 && best > farD) { farD = best; farNode = bn; }
                }
                if (farNode >= 0)
                {
                    pathE = new List<(int e, int from)>(); for (int n = farNode; prevE.ContainsKey(n); n = prevE[n].from) pathE.Insert(0, (prevE[n].e, prevE[n].from));
                    // (cada pieza que dibuja algo del camino, por dónde empieza a lo largo de él: un tramo largo lo dibujan a trozos
                    // varias, la estación y los tramos de línea de cada lado)
                    var at = new Dictionary<int, double> { [first] = -1 };
                    double acc = 0;
                    foreach (var (e, from) in pathE)
                    {
                        var E = g.Edges[e]; bool fwd = E.A == from;
                        for (int i = 0; i < N; i++)
                            if (S[i].DrawnRange(e, out double a0, out double b0) && b0 - a0 > 20)
                            {
                                double pos = acc + (fwd ? a0 : E.Len - b0);
                                if (!at.TryGetValue(i, out double o) || pos < o) at[i] = pos;
                            }
                        acc += E.Len;
                    }
                    var ch = at.OrderBy(kv => kv.Value).Select(kv => kv.Key).ToList();
                    if (ch.Count > 1) chain = ch;
                }
            }
            // (2026-11-10, v3) fuera de la línea principal las piezas que no empalman con la anterior y la siguiente (un trozo que
            // dibuja un poco de la vía de paso pero cuelga de una sola: Junto a Estación Norte); van luego como ramal
            for (bool ch2 = true; ch2;)
            {
                ch2 = false;
                for (int k = 1; k + 1 < chain.Count; k++)
                    if ((!Adj(chain[k - 1], chain[k]) || !Adj(chain[k], chain[k + 1])) && Adj(chain[k - 1], chain[k + 1])) { chain.RemoveAt(k); ch2 = true; break; }
            }
            // (2026-11-10, petición) una estación con variante de más vías que su acceso (Aranda: vía única desde Genzor y doble vía
            // que la rodea): la variante es la línea y la estación, un lazo a un lado. Con la estación en la línea, la doble vía
            // pasaba por encima de ella, sus dos vías una sobre otra y cortando la vía única
            for (bool ch3 = true; ch3;)
            {
                ch3 = false;
                for (int k = 1; k + 1 < chain.Count; k++)
                {
                    int p0 = chain[k - 1], st = chain[k], n0 = chain[k + 1];
                    if (Z[st].Line || !Adj(p0, n0)) continue;
                    int jb = Joints(p0, n0).Count, ja = Math.Min(Joints(p0, st).Count, Joints(st, n0).Count);
                    if (jb >= 2 && jb > ja) { log?.Invoke($"variante: {S[p0].Station} → {S[n0].Station} ({jb} vías) en la línea; {S[st].Station} ({ja}) a un lado"); chain.RemoveAt(k); ch3 = true; break; }
                }
            }
            // ramales: en anchura desde la línea; cada uno cuelga de la pieza ya puesta con la que empalma (mejor un ramal que la línea)
            var order = new List<int>(chain); var parent = new Dictionary<int, int>();
            for (int k = 1; k < chain.Count; k++) parent[chain[k]] = chain[k - 1];
            for (int qi = 0; qi < order.Count; qi++)
                for (int j = 0; j < N; j++) if (!order.Contains(j) && Adj(order[qi], j)) { order.Add(j); parent[j] = order[qi]; }
            foreach (int j in Enumerable.Range(0, N).Where(j => !order.Contains(j)).ToList()) order.Add(j);
            log?.Invoke($"piezas: {N} · uniones: {conn.Count}");
            log?.Invoke("línea principal: " + string.Join(" → ", chain.Select(i => S[i].Station)));
            log?.Invoke("ramales: " + string.Join(", ", order.Skip(chain.Count).Select(i => S[i].Station + (parent.TryGetValue(i, out int pa) ? " (de " + S[pa].Station + ")" : ""))));

            // 3) el eje de la ruta: la vía de verdad de la línea principal. Cada pieza, mirando en su sentido (si no, girada)
            var gsp = new List<(double x, double y)>();
            if (pathE != null)
                foreach (var (e, from) in pathE)
                {
                    var E = g.Edges[e]; bool fwd = E.A == from;
                    for (int k = 0; k < E.X.Length; k++) { int kk = fwd ? k : E.X.Length - 1 - k; double qx = E.X[kk], qy = E.Y[kk]; if (gsp.Count == 0 || Math.Abs(gsp[^1].x - qx) + Math.Abs(gsp[^1].y - qy) > 0.5) gsp.Add((qx, qy)); }
                }
            if (gsp.Count < 2) gsp = new List<(double x, double y)> { (S[first]._cx, S[first]._cy), (S[first]._cx + S[first]._ax, S[first]._cy + S[first]._ay) };
            var (gcum, ggrid) = PolyIndex(gsp);
            (double tx, double ty) Tan(double u)
            {
                int lo = 0, hi = gcum.Length - 1;
                while (hi - lo > 1) { int m = (lo + hi) / 2; if (gcum[m] <= u) lo = m; else hi = m; }
                int a = Math.Max(0, lo - 3), b = Math.Min(gsp.Count - 1, lo + 4);
                double dx = gsp[b].x - gsp[a].x, dy = gsp[b].y - gsp[a].y, L = Math.Sqrt(dx * dx + dy * dy);
                return L < 1e-9 ? (1, 0) : (dx / L, dy / L);
            }
            var GU = new double[N]; var GV = new double[N];
            for (int i = 0; i < N; i++)
            {
                var (_, u, v) = PolyNearest(gsp, gcum, ggrid, S[i]._cx, S[i]._cy, true, true);
                GU[i] = u; GV[i] = v;
                var (tx, ty) = Tan(Math.Max(0, Math.Min(gcum[^1], u)));
                // (2026-11-10, v3) en la línea principal, de su empalme con la anterior al de la siguiente (en el plano): con la
                // dirección general de la línea, un trozo corto en una curva salía al revés y sus uniones daban la vuelta
                int ci = chain.IndexOf(i);
                if (ci >= 0)
                {
                    int pv0 = ci > 0 ? chain[ci - 1] : -1, nx0 = ci + 1 < chain.Count ? chain[ci + 1] : -1;
                    (double x, double y)? pin = pv0 >= 0 && Adj(i, pv0) ? RealAvg(i, pv0) : null, pout = nx0 >= 0 && Adj(i, nx0) ? RealAvg(i, nx0) : null;
                    (double x, double y) c0 = (S[i]._cx, S[i]._cy);
                    var a1 = pin ?? c0; var b1 = pout ?? c0;
                    double ex = b1.x - a1.x, ey = b1.y - a1.y, el = Math.Sqrt(ex * ex + ey * ey);
                    if (el > 60 && ex * tx + ey * ty > -0.5 * el) { tx = ex / el; ty = ey / el; }
                    else if (el > 60 && pin != null && pout != null) { tx = ex / el; ty = ey / el; }
                }
                // (mirando hacia donde va la línea en ese punto: girar con _flip no cambiaba el sentido, Make lo volvía a poner)
                Schematic s2 = null; try { s2 = BuildToward(g, geo, Z[i], tx, ty, skip[i], clip[i]); } catch { }
                if (s2 != null && s2.Tracks.Count > 0) S[i] = s2;
            }
            // hacia dónde crecen las filas con el lado del mapa (igual en todas: todas miran hacia el mismo sentido)
            double SY(Schematic s)
            {
                if (s.Tracks.Count < 2) return 0;
                double mv = s.Tracks.Average(t => t.V), my = s.Tracks.Average(t => t.Pts.Average(q => q.Y));
                return s.Tracks.Sum(t => (t.V - mv) * (t.Pts.Average(q => q.Y) - my));
            }
            double sy = Math.Sign(SY(S[first])); if (sy == 0) sy = 1;
            // (2026-11-10, petición) los triángulos de vías (Parque Grande), como el vértice de un triángulo (Schematic.Apex)
            for (int i = 0; i < N; i++) { string ap = null; try { ap = S[i].Apex(); } catch (Exception ex) { log?.Invoke("triángulo: " + ex.Message); } if (ap != null) log?.Invoke(ap); }
            Connect();
            if (log != null && Environment.GetEnvironmentVariable("SELECTOR_ZONAS_GRAFO") != null)
                for (int i = 0; i < N; i++)
                    log($"GRAFO {S[i].Station} [{(Z[i].Line ? "L" : "E")}, {S[i].Bounds.Width:0}×{S[i].Bounds.Height:0}, u {GU[i]:0} v {GV[i]:0}]: " + string.Join(" | ", Enumerable.Range(0, N).Where(j => j != i && Adj(i, j)).Select(j => $"{S[j].Station} ×{Joints(i, j).Count}")));

            // 4) colocar: la línea principal, seguida (cada pieza a continuación de la anterior y a la altura de su vía); las demás,
            // a lo largo donde están en el mapa y encima o debajo según el lado de la línea en que quedan, sin pisarse
            const float Gap = 60, Pad = 40, KV = 0.35f, MinSide = 140;
            // (2026-11-10, petición) entre bandas del puesto, más sitio: cada banda se ve aparte (sin pisarse sus recuadros) y unida
            // a la de al lado por su vía
            const float BandGap = 480, BandPadX = 360, BandPadY = 300;
            int BandOf(int i) => Bandas.Of.TryGetValue(S[i].Station, out int b) ? b : 0;
            bool OtherBand(int a, int b) { int ba = BandOf(a), bb = BandOf(b); return ba > 0 && bb > 0 && ba != bb; }
            float G(int a, int b) => OtherBand(a, b) ? BandGap : Gap;
            var placed = new List<int>(); var isChain = new HashSet<int>(chain);
            bool Hits(int i, float dx, float dy)
            {
                var b = S[i].Bounds; var r0 = RectangleF.Inflate(new RectangleF(b.X + dx, b.Y + dy, b.Width, b.Height), Pad, Pad);
                return placed.Any(o => Overlap(S[o].Bounds, r0) > 0.5f);
            }
            var axisX = new List<(double u, float x, float y)>();   // (la línea principal) dónde queda cada u en el dibujo
            for (int k = 0; k < chain.Count; k++)
            {
                int i = chain[k]; var s = S[i];
                if (k == 0) { s.Translate(-s.Bounds.Left, -s.Bounds.Top); placed.Add(i); }
                else
                {
                    int pa = chain[k - 1]; var host = S[pa]; var ph = host.Ports(); var pm = s.Ports();
                    var pairs = new List<(PointF a, PointF b)>();
                    foreach (var (mine, other) in Joints(i, pa))
                    {
                        var b = pm.Where(p => p.Key == mine).ToList(); var a = ph.Where(p => p.Key == other).ToList();
                        foreach (var bq in b) if (a.Count > 0) pairs.Add((a.OrderBy(q => Math.Abs(q.At.Y - bq.At.Y)).First().At, bq.At));
                    }
                    float gp = G(pa, i);
                    float dx = host.Bounds.Right + gp - s.Bounds.Left, dy;
                    if (pairs.Count > 0)
                    {
                        var grp = pairs.GroupBy(c => (int)Math.Round((c.a.Y - c.b.Y) / Row)).OrderByDescending(q => q.Count()).ThenBy(q => Math.Abs(q.Key)).First().ToList();
                        dy = grp[0].a.Y - grp[0].b.Y;
                        dx = Math.Max(dx, pairs.Max(c => c.a.X + gp - c.b.X));
                    }
                    else dy = (host.Bounds.Top + host.Bounds.Height / 2) - (s.Bounds.Top + s.Bounds.Height / 2);
                    s.Translate(dx, dy); placed.Add(i);
                }
                axisX.Add((GU[i], s.Bounds.Left + s.Bounds.Width / 2, s.Bounds.Top + s.Bounds.Height / 2));
            }
            axisX = axisX.OrderBy(t => t.u).ToList();
            (float x, float y) AxisAt(double u)
            {
                if (axisX.Count == 1) return (axisX[0].x + (float)(u - axisX[0].u), axisX[0].y);
                if (u <= axisX[0].u) return (axisX[0].x - (float)(axisX[0].u - u), axisX[0].y);
                if (u >= axisX[^1].u) return (axisX[^1].x + (float)(u - axisX[^1].u), axisX[^1].y);
                int k = 1; while (axisX[k].u < u) k++;
                var a = axisX[k - 1]; var b = axisX[k]; double t = (u - a.u) / Math.Max(1e-9, b.u - a.u);
                return ((float)(a.x + (b.x - a.x) * t), (float)(a.y + (b.y - a.y) * t));
            }
            // 4b) (2026-11-10, v3: como un plano de líneas) el resto, línea a línea: cada línea es una fila de piezas seguidas que
            // sale de una pieza ya puesta (A) y acaba en una vía muerta o en otra pieza ya puesta (B: un anillo, que se cierra). Va
            // en horizontal, hacia el lado por el que sale de A, a la altura libre más cercana del lado en que queda en el mapa, y
            // se une a A en diagonal a 45°. Si es un anillo y no cabe hasta B, se abre hueco en lo ya puesto; si sobra, se reparte
            var placedSet = new HashSet<int>(placed);
            var adjL = Enumerable.Range(0, N).Select(i => Enumerable.Range(0, N).Where(j => j != i && Adj(i, j)).ToList()).ToList();
            (double x, double y) RealOf(Port p)
            {
                if (p.Edge >= 0) return g.PointAt(p.Edge, p.Off);
                foreach (int e in g.EdgesAt(p.Node)) { var E = g.Edges[e]; return E.A == p.Node ? (E.X[0], E.Y[0]) : (E.X[^1], E.Y[^1]); }
                return (double.NaN, double.NaN);
            }
            // las puntas de i que empalman con j
            List<Port> JPorts(int i, int j)
            {
                var keys = new HashSet<string>(Joints(i, j).Select(t => t.mine));
                var l = new List<Port>();
                foreach (var p in S[i].Ports()) if (keys.Contains(p.Key) && !l.Any(q => Math.Abs(q.At.X - p.At.X) < 0.5f && Math.Abs(q.At.Y - p.At.Y) < 0.5f)) l.Add(p);
                return l;
            }
            PointF Avg(List<Port> l) => new PointF(l.Average(p => p.At.X), l.Average(p => p.At.Y));
            (double x, double y) RealAvg(int i, int j)
            {
                var l = JPorts(i, j).Select(RealOf).Where(q => !double.IsNaN(q.x)).ToList();
                return l.Count > 0 ? (l.Average(q => q.x), l.Average(q => q.y)) : (S[i]._cx, S[i]._cy);
            }
            // parejas de puntas (de host, de s0) que empalman: la de s0 con la más cercana en altura de host
            List<(PointF a, PointF b)> Pairs(int host, int s0)
            {
                var pairs = new List<(PointF a, PointF b)>(); var ph = S[host].Ports(); var pm = S[s0].Ports();
                foreach (var (mine, other) in Joints(s0, host))
                {
                    var b = pm.Where(p => p.Key == mine).ToList(); var a = ph.Where(p => p.Key == other).ToList();
                    foreach (var bq in b) if (a.Count > 0) pairs.Add((a.OrderBy(q => Math.Abs(q.At.Y - bq.At.Y)).First().At, bq.At));
                }
                return pairs;
            }
            // s0 a continuación de host, hacia dir, con sus puntas a la altura de las de host
            (float dx, float dy) Next(int host, int s0, int dir)
            {
                var hb = S[host].Bounds; var sb = S[s0].Bounds; var pairs = Pairs(host, s0); float gq = G(host, s0);
                float dx = dir > 0 ? hb.Right + gq - sb.Left : hb.Left - gq - sb.Right, dy;
                if (pairs.Count > 0)
                {
                    var grp = pairs.GroupBy(c => (int)Math.Round((c.a.Y - c.b.Y) / Row)).OrderByDescending(q => q.Count()).ThenBy(q => Math.Abs(q.Key)).First().ToList();
                    dy = grp[0].a.Y - grp[0].b.Y;
                    dx = dir > 0 ? Math.Max(dx, pairs.Max(c => c.a.X + gq - c.b.X)) : Math.Min(dx, pairs.Min(c => c.a.X - gq - c.b.X));
                }
                else dy = (hb.Top + hb.Height / 2) - (sb.Top + sb.Height / 2);
                return (dx, dy);
            }
            bool HitsPlaced(RectangleF r) => placed.Any(o => Overlap(S[o].Bounds, r) > 0.5f);
            // (la pieza i en r, sin acercarse a las de otra banda)
            // los recuadros de cada banda con lo ya puesto (como los dibuja el puesto: piezas a menos de 200 juntas)
            List<(int band, RectangleF r)> blocks = null;
            void Blocks()
            {
                blocks = new();
                foreach (var gb in placed.Where(o => BandOf(o) > 0).GroupBy(BandOf))
                {
                    var bl = gb.Select(o => S[o].Bounds).ToList();
                    for (bool m = true; m;) { m = false; for (int x = 0; x < bl.Count && !m; x++) for (int y = x + 1; y < bl.Count; y++) if (RectangleF.Inflate(bl[x], 400, 200).IntersectsWith(bl[y])) { bl[x] = RectangleF.Union(bl[x], bl[y]); bl.RemoveAt(y); m = true; break; } }
                    foreach (var q in bl) blocks.Add((gb.Key, q));
                }
            }
            bool HitsFor(int i, RectangleF r, float px, float py)
            {
                if (placed.Any(o => !OtherBand(i, o) && Overlap(S[o].Bounds, RectangleF.Inflate(r, px, py)) > 0.5f)) return true;
                int bi = BandOf(i); if (bi <= 0) return placed.Any(o => Overlap(S[o].Bounds, RectangleF.Inflate(r, px, py)) > 0.5f);
                if (blocks == null) Blocks();
                // (de otra banda: ni su recuadro ni el nuestro se tocan; entre ellos, sitio)
                return blocks.Any(q => q.band != bi && RectangleF.Inflate(q.r, BandPadX / 2, BandPadY / 2).IntersectsWith(RectangleF.Inflate(r, BandPadX / 2, BandPadY / 2)));
            }
            bool SegHitsPlaced(PointF a, PointF b, int ex)
            {
                foreach (int o in placed)
                {
                    if (o == ex) continue;
                    var r = RectangleF.Inflate(S[o].Bounds, 6, 6);
                    for (int k = 0; k <= 16; k++) { var q = new PointF(a.X + (b.X - a.X) * k / 16, a.Y + (b.Y - a.Y) * k / 16); if (r.Contains(q)) return true; }
                }
                return false;
            }
            const float LanePad = 120;   // (entre una línea y la de al lado, sitio para los rótulos: pegadas parecían una sola)
            if (log != null && Environment.GetEnvironmentVariable("SELECTOR_ZONAS_JUNTAS") is string jn)
                foreach (var nm in jn.Split(';'))
                {
                    int i = S.FindIndex(q => q.Station == nm); if (i < 0) continue;
                    foreach (int j in adjL[i])
                        foreach (var p in JPorts(i, j)) { var rr = RealOf(p); log($"JUNTA {nm} con {S[j].Station}: {p.Key} dibujo ({p.At.X:0},{p.At.Y:0}) real ({rr.x:0},{rr.y:0}) · pieza x {S[i].Bounds.Left:0}..{S[i].Bounds.Right:0}"); }
                }
            int lineNo = 0;
            for (int guard = 0; guard < 400; guard++)
            {
                // la mejor línea: primero las que cierran un anillo (por la vía con más empalmes), luego la más larga
                List<int> best = null; int bestA = -1, bestB = -1; double bestSc = -1;
                foreach (int s0 in Enumerable.Range(0, N).Where(i => !placedSet.Contains(i) && adjL[i].Any(placedSet.Contains)))
                {
                    int A = adjL[s0].Where(placedSet.Contains).OrderByDescending(j => Joints(s0, j).Count).ThenBy(j => isChain.Contains(j) ? 0 : 1).First();
                    var path = new List<int> { s0 }; var on = new HashSet<int> { s0 }; int steps = 0;
                    void Dfs(double w)
                    {
                        if (++steps > 30000) return;
                        int t = path[^1]; int B = -1, bj = 0;
                        foreach (int j in adjL[t])
                            if (placedSet.Contains(j) && !(j == A && path.Count == 1)) { int c = Joints(t, j).Count; if (c > bj) { bj = c; B = j; } }
                        double sc = w + (B >= 0 ? 20000 + 3000 * (bj + Joints(s0, A).Count) : 0);   // (un anillo, por sus empalmes de más vías: la línea, no un enlace)
                        if (sc > bestSc) { bestSc = sc; best = new List<int>(path); bestA = A; bestB = B; }
                        foreach (int j in adjL[t]) if (!placedSet.Contains(j) && on.Add(j)) { path.Add(j); Dfs(w + Len(j)); path.RemoveAt(path.Count - 1); on.Remove(j); }
                    }
                    Dfs(Len(s0));
                }
                if (best == null) break;
                lineNo++; blocks = null;
                int a0 = bestA, b0 = bestB; var line = best; int n = line.Count;
                // (2026-11-10) un anillo, del lado en que llega a B de frente: Aranda salía de Junto a Aranda (2) hacia atrás y
                // llegaba de espaldas a la vía única de Genzor, debajo de Genzor
                if (b0 >= 0 && JPorts(a0, line[0]).Count > 0 && JPorts(b0, line[^1]).Count > 0)
                {
                    bool Faces(int x, int y, int fx, int fy)
                    {
                        float px = Avg(JPorts(x, fx)).X, py = Avg(JPorts(y, fy)).X; var yb = S[y].Bounds;
                        return py >= px ? py <= yb.Left + yb.Width / 2 : py >= yb.Left + yb.Width / 2;
                    }
                    if (!Faces(a0, b0, line[0], line[^1]) && Faces(b0, a0, line[^1], line[0]))
                    {
                        (a0, b0) = (b0, a0); line = Enumerable.Reverse(line).ToList();
                        log?.Invoke($"anillo al revés: de {S[a0].Station} a {S[b0].Station}");
                    }
                }
                // hacia dónde sale de A: el lado de A en que está su empalme (si está en medio, hacia donde va en el mapa)
                var aPorts = JPorts(a0, line[0]);
                var ab = S[a0].Bounds; float apx = aPorts.Count > 0 ? Avg(aPorts).X : ab.Left + ab.Width / 2;
                int dir;
                if (b0 >= 0 && JPorts(b0, line[^1]).Count > 0) dir = Avg(JPorts(b0, line[^1])).X >= apx ? 1 : -1;   // (un anillo: hacia donde se cierra)
                else if (Math.Abs(apx - (ab.Left + ab.Width / 2)) > ab.Width * 0.15f) dir = apx > ab.Left + ab.Width / 2 ? 1 : -1;
                else dir = GU[line[^1]] >= GU[a0] - 300 ? 1 : -1;
                // cada pieza mirando hacia donde va la línea en su sitio (de su empalme de entrada al de salida, en el plano)
                for (int k = 0; k < n; k++)
                {
                    int i = line[k], prev = k == 0 ? a0 : line[k - 1], next = k + 1 < n ? line[k + 1] : b0;
                    var pin = RealAvg(i, prev);
                    var pout = next >= 0 ? RealAvg(i, next) : (S[i]._cx, S[i]._cy);
                    double tx = pout.Item1 - pin.Item1, ty = pout.Item2 - pin.Item2, tl = Math.Sqrt(tx * tx + ty * ty);
                    if (tl < 60) { var pc = RealAvg(prev, i); tx = S[i]._cx - pc.Item1; ty = S[i]._cy - pc.Item2; tl = Math.Sqrt(tx * tx + ty * ty); }
                    if (tl < 1e-6) continue;
                    Schematic s2 = null; try { s2 = BuildToward(g, geo, Z[i], dir * tx / tl, dir * ty / tl, skip[i], clip[i]); } catch { }
                    if (s2 != null && s2.Tracks.Count > 0) S[i] = s2;
                }
                // en fila, desde A (a su altura)
                {
                    var s = S[line[0]]; var pr = Pairs(a0, line[0]); float gq = G(a0, line[0]);
                    float dx, dy;
                    if (pr.Count > 0)
                    {
                        var grp = pr.GroupBy(c => (int)Math.Round((c.a.Y - c.b.Y) / Row)).OrderByDescending(q => q.Count()).ThenBy(q => Math.Abs(q.Key)).First().ToList();
                        dy = grp[0].a.Y - grp[0].b.Y;
                        dx = dir > 0 ? Math.Max(pr.Max(c => c.a.X + gq - c.b.X), pr.Max(c => c.a.X) + gq - s.Bounds.Left)
                                     : Math.Min(pr.Min(c => c.a.X - gq - c.b.X), pr.Min(c => c.a.X) - gq - s.Bounds.Right);
                    }
                    else { dx = dir > 0 ? apx + gq - s.Bounds.Left : apx - gq - s.Bounds.Right; dy = (ab.Top + ab.Height / 2) - (s.Bounds.Top + s.Bounds.Height / 2); }
                    s.Translate(dx, dy);
                }
                for (int k = 1; k < n; k++) { var (dx, dy) = Next(line[k - 1], line[k], dir); S[line[k]].Translate(dx, dy); }
                // a qué altura: la libre más cercana del lado del mapa (la diagonal de A a la primera pieza, sin pisar nada)
                var ap = aPorts.Count > 0 ? Avg(aPorts) : new PointF(apx, ab.Top + ab.Height / 2);
                var fp0 = JPorts(line[0], a0); var f0 = fp0.Count > 0 ? Avg(fp0) : new PointF(dir > 0 ? S[line[0]].Bounds.Left : S[line[0]].Bounds.Right, S[line[0]].Bounds.Top + S[line[0]].Bounds.Height / 2);
                bool Fits(float sy)
                {
                    float sx = dir * Math.Abs(sy);
                    foreach (int i in line) { var b = S[i].Bounds; if (HitsFor(i, new RectangleF(b.X + sx, b.Y + sy, b.Width, b.Height), Pad, LanePad)) return false; }
                    return Math.Abs(sy) < 1 || !SegHitsPlaced(new PointF(ap.X + dir * 30, ap.Y), new PointF(f0.X + sx - dir * 30, f0.Y + sy), a0);
                }
                int farI = line.OrderByDescending(i => Math.Abs(GV[i] - GV[a0])).First();
                double gvd = (GV[farI] - GV[a0]) * sy;
                int pref = Math.Abs(gvd) < 200 ? 0 : Math.Sign(gvd);   // (+1 debajo, −1 encima; 0: el más cercano)
                float Search(int side) { for (int k = 0; k < 600; k++) { float sy2 = side * k * Row; if (Fits(sy2)) return sy2; } return float.NaN; }
                float up = Search(-1), down = Search(1), shift;
                if (float.IsNaN(up) && float.IsNaN(down)) shift = 0;
                else if (float.IsNaN(up)) shift = down; else if (float.IsNaN(down)) shift = up;
                else if (pref > 0) shift = down <= -up + 1500 ? down : up;   // (del lado del mapa salvo que quede mucho más lejos)
                else if (pref < 0) shift = -up <= down + 1500 ? up : down;
                else shift = -up <= down ? up : down;
                foreach (int i in line) S[i].Translate(dir * Math.Abs(shift), shift);
                // un anillo: que llegue a B por delante (abriendo hueco en lo ya puesto) o, si sobra sitio, repartido
                string close = "";
                if (b0 >= 0)
                {
                    var bp = JPorts(b0, line[^1]); var ep = JPorts(line[^1], b0);
                    if (bp.Count > 0 && ep.Count > 0)
                    {
                        var pb = Avg(bp); var pe = Avg(ep); var bb = S[b0].Bounds;
                        bool faces = dir > 0 ? pb.X <= bb.Left + bb.Width / 2 : pb.X >= bb.Left + bb.Width / 2;   // (B recibe por el lado que mira a la línea)
                        float need = dir * (pb.X - pe.X) - (G(line[^1], b0) + Math.Abs(pb.Y - pe.Y));
                        if (faces && need < -1)
                        {
                            // hueco: un corte vertical entre A y B que no parta ninguna pieza puesta; todo lo de más allá, más allá
                            float lo = Math.Min(ap.X, pb.X), hi = Math.Max(ap.X, pb.X); float cut = float.NaN;
                            var xs = placed.SelectMany(o => new[] { S[o].Bounds.Left - Gap / 2, S[o].Bounds.Right + Gap / 2 }).Where(x => x > lo && x < hi)
                                           .OrderBy(x => dir > 0 ? hi - x : x - lo).ToList();
                            // (de preferencia entre dos bandas: el hueco queda entre sus recuadros, no dentro de una)
                            var okX = xs.Where(x => !placed.Any(o => S[o].Bounds.Left < x && S[o].Bounds.Right > x)).ToList();
                            bool Between(float x)
                            {
                                int l = placed.Where(o => S[o].Bounds.Right <= x).OrderByDescending(o => S[o].Bounds.Right).DefaultIfEmpty(-1).First();
                                int r = placed.Where(o => S[o].Bounds.Left >= x).OrderBy(o => S[o].Bounds.Left).DefaultIfEmpty(-1).First();
                                return l >= 0 && r >= 0 && OtherBand(l, r);
                            }
                            if (okX.Count > 0) cut = okX.Where(Between).DefaultIfEmpty(okX[0]).First();
                            if (!float.IsNaN(cut))
                            {
                                float d = -need;
                                foreach (int o in placed) if (dir > 0 ? S[o].Bounds.Left >= cut : S[o].Bounds.Right <= cut) S[o].Translate(dir * d, 0);
                                blocks = null;
                                close = $", hueco de {d:0} en {cut:0}";
                            }
                            else close = ", sin hueco";
                        }
                        else if (faces && need > 1)
                        {
                            for (int k = 0; k < n; k++) S[line[k]].Translate(dir * need * (k + 1) / (n + 1), 0);
                            bool bad = line.Any(i => HitsFor(i, S[i].Bounds, Pad, Pad));
                            if (bad) for (int k = 0; k < n; k++) S[line[k]].Translate(-dir * need * (k + 1) / (n + 1), 0);
                            else close = $", repartido {need:0}";
                        }
                        else if (!faces) close = ", B de espaldas";
                    }
                }
                foreach (int i in line) { placed.Add(i); placedSet.Add(i); }
                log?.Invoke($"línea {lineNo} ({(dir > 0 ? "→" : "←")}, {(shift > 0 ? "debajo" : shift < 0 ? "encima" : "a su altura")} {Math.Abs(shift):0}{close}) de {S[a0].Station}{(b0 >= 0 ? " a " + S[b0].Station : "")}: {string.Join(" · ", line.Select(i => S[i].Station))}");
            }
            foreach (int i in Enumerable.Range(0, N).Where(i => !placedSet.Contains(i)).ToList())
            {
                // (sueltas sin empalme con nada: debajo de todo)
                var bx = placed.Select(o => S[o].Bounds).Aggregate(RectangleF.Union);
                var (ax, _) = AxisAt(GU[i]); var s = S[i];
                s.Translate(ax - s.Bounds.Left, bx.Bottom + 200 - s.Bounds.Top); placed.Add(i); placedSet.Add(i);
                log?.Invoke($"suelta {s.Station}: sin empalmes, debajo");
            }
            log?.Invoke($"piezas: {N} · uniones: {conn.Count} · eje {gcum[^1]:0} m");
            log?.Invoke("línea principal: " + string.Join(" → ", chain.Select(i => S[i].Station)));
            if (log != null && Environment.GetEnvironmentVariable("SELECTOR_ZONAS_DEBUG") != null)
                for (int k = 1; k < chain.Count; k++)
                {
                    int i = chain[k - 1], j = chain[k];
                    log($"  {S[i].Station} → {S[j].Station}: {Joints(i, j).Count} uniones ({string.Join(", ", Joints(i, j).Select(t => t.mine + "/" + t.other))}) · con otras: {string.Join(", ", Enumerable.Range(0, N).Where(o => o != i && Adj(i, o)).Select(o => S[o].Station))}");
                }

            // 5) el esquema: las piezas; las uniones, de la más corta a la más larga, dibujadas como vía si no cortan nada; si no,
            // como en ADIF: una salida en cada lado con la estación a la que se va
            foreach (int i in placed) S[i].ZoneNames = new HashSet<string>(Z[i].Members ?? new List<string> { Z[i].Name }, StringComparer.Ordinal);
            var parts = placed.Select(i => S[i]).ToList();
            foreach (int i in placed) if (!Z[i].Line) foreach (var pf in S[i].Platforms) pf.Station ??= S[i].Station;   // (los rótulos de cada estación)
            var r = new Schematic { _g = g, Station = label, _parts = parts, _partOf = new(), _bandSt = new HashSet<string>(StringComparer.Ordinal) };
            var portsAll = S.Select(s => s.Ports()).ToList();
            var stl = Stations(g);
            (double x, double y) RealAt(Port p)
            {
                if (p.Edge >= 0) return g.PointAt(p.Edge, p.Off);
                foreach (int e in g.EdgesAt(p.Node)) { var E = g.Edges[e]; return E.A == p.Node ? (E.X[0], E.Y[0]) : (E.X[^1], E.Y[^1]); }
                return (0, 0);
            }
            string NearSt(Port p, int not)
            {
                var (x, y) = RealAt(p);
                return stl.Where(t => !(Z[not].Line == false && t.name == S[not].Station)).OrderBy(t => (t.x - x) * (t.x - x) + (t.y - y) * (t.y - y)).Select(t => t.name).FirstOrDefault();
            }
            var segs = new List<(PointF a, PointF b)>();
            foreach (var p in parts) foreach (var t in p.Tracks.Where(t => !p.HiddenEdges.Contains(t.Edge))) for (int k = 1; k < t.Pts.Length; k++) segs.Add((t.Pts[k - 1], t.Pts[k]));
            var bars = parts.SelectMany(p => p.Platforms).Select(pf => RectangleF.Inflate(pf.Bar, 0, 3)).ToList();
            bool Clean(PointF[] l)
            {
                for (int k = 1; k < l.Length; k++)
                {
                    foreach (var sg in segs) if (SegCut(l[k - 1], l[k], sg.a, sg.b)) return false;
                    for (int m = 1; m < 10; m++) { var q = new PointF(l[k - 1].X + (l[k].X - l[k - 1].X) * m / 10, l[k - 1].Y + (l[k].Y - l[k - 1].Y) * m / 10); if (bars.Any(b => b.Contains(q))) return false; }
                }
                return true;
            }
            // (2026-11-10, v3) cada empalme, dibujado como vía: horizontal y a 45°, por donde menos vías corte (si hace falta,
            // rodeando por encima o por debajo lo que haya en medio). Ya no hay salidas con nombre entre piezas
            var segGrid = new Dictionary<(int, int), List<(PointF a, PointF b)>>();
            const float GC = 160;
            void AddSeg(PointF a, PointF b)
            {
                int x0 = (int)Math.Floor(Math.Min(a.X, b.X) / GC), x1 = (int)Math.Floor(Math.Max(a.X, b.X) / GC), y0 = (int)Math.Floor(Math.Min(a.Y, b.Y) / GC), y1 = (int)Math.Floor(Math.Max(a.Y, b.Y) / GC);
                for (int x = x0; x <= x1; x++) for (int y = y0; y <= y1; y++) { if (!segGrid.TryGetValue((x, y), out var l)) segGrid[(x, y)] = l = new(); l.Add((a, b)); }
            }
            foreach (var sg in segs) AddSeg(sg.a, sg.b);
            int Cuts(PointF[] l)
            {
                int c = 0; var seen = new HashSet<(PointF, PointF)>();
                for (int k = 1; k < l.Length; k++)
                {
                    PointF a = l[k - 1], b = l[k];
                    int x0 = (int)Math.Floor(Math.Min(a.X, b.X) / GC), x1 = (int)Math.Floor(Math.Max(a.X, b.X) / GC), y0 = (int)Math.Floor(Math.Min(a.Y, b.Y) / GC), y1 = (int)Math.Floor(Math.Max(a.Y, b.Y) / GC);
                    for (int x = x0; x <= x1; x++) for (int y = y0; y <= y1; y++)
                            if (segGrid.TryGetValue((x, y), out var sl)) foreach (var sg in sl) if (seen.Add(sg) && (SegCut(a, b, sg.a, sg.b) || OnTop(a, b, sg.a, sg.b))) c++;
                    for (int m = 1; m < 10; m++) { var q = new PointF(a.X + (b.X - a.X) * m / 10, a.Y + (b.Y - a.Y) * m / 10); if (bars.Any(r0 => r0.Contains(q))) { c += 3; break; } }
                }
                return c;
            }
            // (encima de otra vía, en la misma fila: no se vería por dónde va)
            static bool OnTop(PointF a, PointF b, PointF c, PointF d)
            {
                if (Math.Abs(a.Y - b.Y) > 0.5f || Math.Abs(c.Y - d.Y) > 0.5f || Math.Abs(a.Y - c.Y) > 1.5f) return false;
                float lo = Math.Max(Math.Min(a.X, b.X), Math.Min(c.X, d.X)), hi = Math.Min(Math.Max(a.X, b.X), Math.Max(c.X, d.X));
                return hi - lo > 3;
            }
            static float PLen(PointF[] l) { float s = 0; for (int k = 1; k < l.Length; k++) s += Math.Abs(l[k].X - l[k - 1].X) + Math.Abs(l[k].Y - l[k - 1].Y); return s; }
            // las formas posibles de ir de A (sale hacia oa) a B (sale de su pieza hacia ob)
            int backs = 0;
            List<PointF[]> Shapes(PointF A, int oa, PointF B, int ob, IEnumerable<float> levels)
            {
                var l = new List<PointF[]>(); const float st = 14;
                float ady = Math.Abs(B.Y - A.Y);
                if (oa > 0 && ob < 0 && B.X > A.X || oa < 0 && ob > 0 && B.X < A.X)
                {
                    float d = oa, w = Math.Abs(B.X - A.X);
                    if (ady < 0.5f) l.Add(new[] { A, B });
                    else if (w >= ady + 2 * st)
                    {
                        // la diagonal al principio, en medio o al final
                        foreach (float f in new[] { 0f, 0.5f, 1f })
                        {
                            float x0 = A.X + d * (st + (w - ady - 2 * st) * f);
                            l.Add(new[] { A, new PointF(x0, A.Y), new PointF(x0 + d * ady, B.Y), B });
                        }
                    }
                    else l.Add(new[] { A, new PointF(A.X + d * Math.Min(st, w / 4), A.Y), new PointF(B.X - d * Math.Min(st, w / 4), B.Y), B });
                    // rodeando por otra altura
                    foreach (float L in levels)
                    {
                        float h1 = Math.Abs(L - A.Y), h2 = Math.Abs(B.Y - L);
                        if (w < h1 + h2 + 2 * st) continue;
                        float x1 = A.X + d * (st + h1), x2 = B.X - d * (st + h2);
                        l.Add(new[] { A, new PointF(A.X + d * st, A.Y), new PointF(x1, L), new PointF(x2, L), new PointF(B.X - d * st, B.Y), B });
                    }
                }
                else
                {
                    // de espaldas (no debería pasar): sale, cambia de altura y vuelve
                    foreach (float L in levels.Append(A.Y).Append(B.Y))
                    {
                        float xa = A.X + oa * 20, xb = B.X + ob * 20;
                        l.Add(new[] { A, new PointF(xa, A.Y), new PointF(xa, L), new PointF(xb, L), new PointF(xb, B.Y), B });
                    }
                }
                return l;
            }
            var linked = new List<(int i, Port a, int j, Port b)>();
            foreach (var (i, a, j, b) in conn)
            {
                var pa = portsAll[i].Where(p => p.Key == a).ToList(); var pb = portsAll[j].Where(p => p.Key == b).ToList();
                if (pa.Count == 0 || pb.Count == 0) continue;
                var bestP = pa.SelectMany(x => pb.Select(y => (x, y))).OrderBy(t => Math.Abs(t.x.At.X - t.y.At.X) + Math.Abs(t.x.At.Y - t.y.At.Y)).First();
                linked.Add((i, bestP.x, j, bestP.y));
            }
            int nCut = 0;
            foreach (var (i, a, j, b) in linked.OrderBy(t => Math.Abs(t.a.At.X - t.b.At.X) + Math.Abs(t.a.At.Y - t.b.At.Y)))
            {
                PointF A = a.At, B = b.At;
                if (Math.Abs(A.X - B.X) + Math.Abs(A.Y - B.Y) < 0.5f) continue;
                // hacia dónde sale cada punta: hacia fuera de su pieza; si las dos se miran, de una a la otra
                // (siempre de izquierda a derecha: horizontal y a 45°; nada de vueltas en ángulo recto)
                if (B.X < A.X) { (A, B) = (B, A); }
                int oa = 1, ob = -1;
                { int Out(PointF q, int pi) { var bb = S[pi].Bounds; return q.X >= bb.Left + bb.Width / 2 ? 1 : -1; }
                  var (pa0, pb0) = a.At.X <= b.At.X ? (i, j) : (j, i);
                  if (Out(A, pa0) < 0 && Out(B, pb0) > 0) { backs++; log?.Invoke($"  unión de espaldas: {S[i].Station} → {S[j].Station} ({A.X:0},{A.Y:0})→({B.X:0},{B.Y:0})"); } }
                float lo = Math.Min(A.X, B.X) - 30, hi = Math.Max(A.X, B.X) + 30;
                var mid = parts.Where(p => p.Bounds.Right > lo && p.Bounds.Left < hi).ToList();
                var levels = new List<float>();
                foreach (var p in mid) for (int k = 1; k <= 2; k++) { levels.Add(p.Bounds.Top - k * Row); levels.Add(p.Bounds.Bottom + k * Row); }
                levels = levels.Distinct().OrderBy(L => Math.Abs(L - A.Y) + Math.Abs(L - B.Y)).Take(16).ToList();
                var pick = Shapes(A, oa, B, ob, levels).Select(l => (l, c: Cuts(l))).OrderBy(t => t.c).ThenBy(t => PLen(t.l)).First();
                if (pick.c > 0) { nCut++; log?.Invoke($"  unión que corta {pick.c}: {S[i].Station} → {S[j].Station} ({A.X:0},{A.Y:0})→({B.X:0},{B.Y:0})"); }
                r.Links.Add(pick.l); for (int k = 1; k < pick.l.Length; k++) AddSeg(pick.l[k - 1], pick.l[k]);
            }
            // (2026-11-10, petición) donde una unión pasa por encima de una vía (o de otra unión) sin empalmar: marcado como paso
            // superior (los pretiles a los dos lados de la de arriba, y la de abajo cortada), como dentro de las estaciones
            {
                static bool Cross(PointF p1, PointF p2, PointF p3, PointF p4, out PointF c)
                {
                    c = default;
                    float d = (p2.X - p1.X) * (p4.Y - p3.Y) - (p2.Y - p1.Y) * (p4.X - p3.X);
                    if (Math.Abs(d) < 1e-6) return false;
                    float t = ((p3.X - p1.X) * (p4.Y - p3.Y) - (p3.Y - p1.Y) * (p4.X - p3.X)) / d, u = ((p3.X - p1.X) * (p2.Y - p1.Y) - (p3.Y - p1.Y) * (p2.X - p1.X)) / d;
                    if (t <= 0.001f || t >= 0.999f || u <= 0.001f || u >= 0.999f) return false;
                    c = new PointF(p1.X + t * (p2.X - p1.X), p1.Y + t * (p2.Y - p1.Y)); return true;
                }
                var tGrid = new Dictionary<(int, int), List<(PointF a, PointF b)>>();
                foreach (var sg in segs)
                {
                    int x0 = (int)Math.Floor(Math.Min(sg.a.X, sg.b.X) / GC), x1 = (int)Math.Floor(Math.Max(sg.a.X, sg.b.X) / GC), y0 = (int)Math.Floor(Math.Min(sg.a.Y, sg.b.Y) / GC), y1 = (int)Math.Floor(Math.Max(sg.a.Y, sg.b.Y) / GC);
                    for (int x = x0; x <= x1; x++) for (int y = y0; y <= y1; y++) { if (!tGrid.TryGetValue((x, y), out var l)) tGrid[(x, y)] = l = new(); l.Add(sg); }
                }
                int nb = 0;
                for (int li = 0; li < r.Links.Count; li++)
                {
                    var l = r.Links[li];
                    var hits = new List<(PointF at, PointF dir)>();
                    void Hit(PointF c, PointF a, PointF b)
                    {
                        if (Dist(c, l[0]) < 4 || Dist(c, l[^1]) < 4 || hits.Any(h => Dist(h.at, c) < 3)) return;
                        float dl = Dist(a, b); if (dl < 0.1f) return;
                        hits.Add((c, new PointF((b.X - a.X) / dl, (b.Y - a.Y) / dl)));
                    }
                    for (int k = 1; k < l.Length; k++)
                    {
                        PointF a = l[k - 1], b = l[k];
                        int x0 = (int)Math.Floor(Math.Min(a.X, b.X) / GC), x1 = (int)Math.Floor(Math.Max(a.X, b.X) / GC), y0 = (int)Math.Floor(Math.Min(a.Y, b.Y) / GC), y1 = (int)Math.Floor(Math.Max(a.Y, b.Y) / GC);
                        var seen = new HashSet<(PointF, PointF)>();
                        for (int x = x0; x <= x1; x++) for (int y = y0; y <= y1; y++)
                                if (tGrid.TryGetValue((x, y), out var sl)) foreach (var sg in sl) if (seen.Add(sg) && Cross(a, b, sg.a, sg.b, out var c)) Hit(c, a, b);
                        // (con las uniones de antes: esta va por encima)
                        for (int lj = 0; lj < li; lj++)
                        {
                            var o = r.Links[lj];
                            for (int m = 1; m < o.Length; m++) if (Cross(a, b, o[m - 1], o[m], out var c) && Dist(c, o[0]) >= 4 && Dist(c, o[^1]) >= 4) Hit(c, a, b);
                        }
                    }
                    foreach (var h in hits) { r.Bridges.Add((h.at, h.dir, -1 - li)); nb++; }
                }
                log?.Invoke($"cruces de las uniones: {nb}, marcados como paso superior");
            }
            // (2026-11-10, auditoría) la aguja cuya rama dibuja la pieza de al lado (Museo 382/395/371, Santa Ana 331…): su rama es el
            // principio de la unión que sale de ella; sin ella, no se veía hacia dónde está puesta
            {
                int nl = 0;
                foreach (var p in parts)
                    foreach (var sw in p.Switches)
                        for (int k = 0; k < 2; k++)
                        {
                            if (sw.Legs[k] != null || sw.LegEdge[k] < 0) continue;
                            var other = sw.Legs[1 - k];
                            PointF[] best = null; float bestScore = float.MaxValue;
                            foreach (var l in r.Links)
                            {
                                PointF[] from = Dist(l[0], sw.At) < 1.5f ? l : Dist(l[^1], sw.At) < 1.5f ? l.Reverse().ToArray() : null;
                                if (from == null) continue;
                                var stub = StubOf(from, 12);
                                if (stub == null) continue;
                                // (que no sea la misma dirección que la otra rama)
                                float score = other != null && other.Length >= 2 ? -Dist(stub[^1], other[^1]) : 0;
                                if (score < bestScore) { bestScore = score; best = stub; }
                            }
                            if (best != null) { sw.Legs[k] = best; nl++; }
                        }
                if (nl > 0) log?.Invoke($"ramas de aguja por su unión: {nl}");
            }
            var exitsAdd = new List<Exit>();
            log?.Invoke($"uniones: {r.Links.Count} dibujadas como vía ({nCut} cortan alguna vía, {backs} de espaldas)");
            foreach (var p in parts)
            {
                foreach (int e in p._byEdge.Keys) { r._partOf.TryAdd(e, p); r._partsOf ??= new(); if (!r._partsOf.TryGetValue(e, out var pl)) r._partsOf[e] = pl = new(); pl.Add(p); }
                r._bandSt.Add(p.Station);
                r.Tracks.AddRange(p.Tracks); r.Switches.AddRange(p.Switches); r.Slips.AddRange(p.Slips); r.Crossings.AddRange(p.Crossings);
                r.Bridges.AddRange(p.Bridges); r.SlipNodes.AddRange(p.SlipNodes); r.SlipGroups.AddRange(p.SlipGroups); r.ScissorGroups.AddRange(p.ScissorGroups);
                r.HiddenEdges.UnionWith(p.HiddenEdges); r.Missing.AddRange(p.Missing); r.Buffers.AddRange(p.Buffers); r.Signals.AddRange(p.Signals);
                r.LevelXs.AddRange(p.LevelXs); r.Platforms.AddRange(p.Platforms);
                foreach (var sl in p.SidingLabels) if (!r.SidingLabels.Any(o => o.Edge == sl.Edge && o.Name == sl.Name)) r.SidingLabels.Add(sl);   // (una vez, aunque dos piezas dibujen su vía)
                foreach (var kp in p.KmPosts) if (!r.KmPosts.Any(o => o.Edge == kp.Edge && Math.Abs(o.Off - kp.Off) < 1)) r.KmPosts.Add(kp);
                r.Rows = Math.Max(r.Rows, p.Rows);
            }
            // (una señal en un tramo que dibujan dos piezas, una vez: en la que la tiene más dentro de lo que dibuja)
            {
                var keep = new List<Signal>();
                foreach (var grp in parts.SelectMany(p => p.Signals.Select(q => (p, q))).GroupBy(t => t.q.Sig))
                {
                    if (grp.Key == null || grp.Count() == 1) { keep.AddRange(grp.Select(t => t.q)); continue; }
                    keep.Add(grp.OrderByDescending(t => t.p.DrawnRange(grp.Key.Edge, out double a0, out double b0) ? Math.Min(grp.Key.Off - a0, b0 - grp.Key.Off) : double.MinValue).First().q);
                }
                r.Signals.Clear(); r.Signals.AddRange(keep);
            }
            var linkEnds = r.Links.SelectMany(l => new[] { l[0], l[^1] }).ToList();
            foreach (var p in parts)
                r.Exits.AddRange(p.Exits.Where(x => !linkEnds.Any(q => Math.Abs(q.X - x.At.X) < 70 && Math.Abs(q.Y - x.At.Y) < Row * 0.6f)
                                                     && !exitsAdd.Any(q => Math.Abs(q.At.X - x.At.X) < 70 && Math.Abs(q.At.Y - x.At.Y) < Row * 0.6f)
                                                     && !(x.Station != null && r._bandSt.Contains(x.Station))));
            r.Exits.AddRange(exitsAdd.Where(x => !linkEnds.Any(q => Math.Abs(q.X - x.At.X) < 1 && Math.Abs(q.Y - x.At.Y) < 1)));
            var bbx = parts[0].Bounds;
            foreach (var p in parts.Skip(1)) bbx = RectangleF.Union(bbx, p.Bounds);
            r.Bounds = bbx;
            return r;
        }

        // los primeros len px de una línea
        static PointF[] StubOf(PointF[] pts, float len)
        {
            var res = new List<PointF> { pts[0] }; float left = len;
            for (int i = 1; i < pts.Length && left > 0; i++)
            {
                float d = Dist(pts[i - 1], pts[i]);
                if (d >= left) { float k = left / d; res.Add(new PointF(pts[i - 1].X + (pts[i].X - pts[i - 1].X) * k, pts[i - 1].Y + (pts[i].Y - pts[i - 1].Y) * k)); break; }
                res.Add(pts[i]); left -= d;
            }
            return res.Count >= 2 ? res.ToArray() : null;
        }

        static bool SegCut(PointF p1, PointF p2, PointF p3, PointF p4)
        {
            float d = (p2.X - p1.X) * (p4.Y - p3.Y) - (p2.Y - p1.Y) * (p4.X - p3.X);
            if (Math.Abs(d) < 1e-6) return false;
            float t = ((p3.X - p1.X) * (p4.Y - p3.Y) - (p3.Y - p1.Y) * (p4.X - p3.X)) / d, u = ((p3.X - p1.X) * (p2.Y - p1.Y) - (p3.Y - p1.Y) * (p2.X - p1.X)) / d;
            return t > 0.001f && t < 0.999f && u > 0.001f && u < 0.999f;
        }
    }
}
