using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace SelectOR
{
    // (2026-11-09) El esquema de la ruta entera, de principio a fin: el de cada banda (BuildBand), girado en el sentido de la
    // ruta, colocado a continuación del de la banda con la que empalma y unido a él por sus vías (Links). Cada pieza sigue
    // siendo un esquema completo; el de la ruta reparte las consultas (Map, Piece…) a la pieza que dibuja cada tramo.
    public sealed partial class Schematic
    {
        List<Schematic> _parts; Dictionary<int, Schematic> _partOf;
        float _ox, _oy;                                  // (una pieza: lo que se ha movido dentro del esquema de la ruta)
        public IReadOnlyList<Schematic> Parts => _parts;
        public Schematic Focus;                           // (el puesto) la banda elegida: sus rótulos, primero y enteros
        public HashSet<Schematic> FocusSet;               // (2026-11-10, esquema por estaciones) las piezas de la banda elegida
        public readonly List<PointF[]> Links = new();    // las vías que unen una pieza con otra
        Schematic PartOf(int e) => _partOf != null && _partOf.TryGetValue(e, out var p) ? p : null;
        // (2026-11-10) un tramo que dibujan a trozos varias piezas (cada una lo suyo): la del trozo en el que está off
        Dictionary<int, List<Schematic>> _partsOf;
        Schematic PartOf(int e, double off)
        {
            if (_partsOf == null || !_partsOf.TryGetValue(e, out var l) || l.Count < 2) return PartOf(e);
            Schematic best = null; double bd = double.MaxValue;
            foreach (var p in l)
                if (p.DrawnRange(e, out double a, out double b)) { double d = off < a ? a - off : off > b ? off - b : 0; if (d < bd) { bd = d; best = p; } }
            return best ?? PartOf(e);
        }
        public IReadOnlyList<Schematic> PartsOf(int e) => _partsOf != null && _partsOf.TryGetValue(e, out var l) ? l : (PartOf(e) is Schematic p ? new[] { p } : Array.Empty<Schematic>());

        // mover todo lo dibujado de una pieza
        void Translate(float dx, float dy)
        {
            if (dx == 0 && dy == 0) return;
            PointF M(PointF q) => new PointF(q.X + dx, q.Y + dy);
            PointF[] MA(PointF[] a) => a?.Select(M).ToArray();
            foreach (var t in Tracks) t.Pts = MA(t.Pts);
            foreach (var w in Switches) { w.At = M(w.At); for (int i = 0; i < w.Legs.Length; i++) w.Legs[i] = MA(w.Legs[i]); }
            for (int i = 0; i < Slips.Count; i++) Slips[i] = M(Slips[i]);
            for (int i = 0; i < Crossings.Count; i++) Crossings[i] = M(Crossings[i]);
            for (int i = 0; i < Bridges.Count; i++) Bridges[i] = (M(Bridges[i].At), Bridges[i].Dir, Bridges[i].Edge);
            foreach (var sg in SlipGroups) sg.At = M(sg.At);
            foreach (var tg in ScissorGroups) for (int i = 0; i < tg.At.Length; i++) tg.At[i] = M(tg.At[i]);
            for (int i = 0; i < Buffers.Count; i++) Buffers[i] = M(Buffers[i]);
            foreach (var sg in Signals) sg.At = M(sg.At);
            foreach (var lx in LevelXs) lx.At = M(lx.At);
            foreach (var sl in SidingLabels) sl.At = M(sl.At);
            foreach (var kp in KmPosts) kp.At = M(kp.At);
            foreach (var p in Platforms) p.Bar = new RectangleF(p.Bar.X + dx, p.Bar.Y + dy, p.Bar.Width, p.Bar.Height);
            foreach (var x in Exits) x.At = M(x.At);
            for (int i = 0; i < Links.Count; i++) Links[i] = MA(Links[i]);
            Bounds = new RectangleF(Bounds.X + dx, Bounds.Y + dy, Bounds.Width, Bounds.Height);
            _ox += dx; _oy += dy;
            _arc.Clear();
        }

        // las puntas de las vías de un esquema, por nodo de la ruta (para empalmar con otro)
        static Dictionary<int, List<PointF>> Ends(Schematic s)
        {
            var d = new Dictionary<int, List<PointF>>();
            foreach (var t in s.Tracks)
            {
                if (t.Pts == null || t.Pts.Length < 2) continue;
                for (int k = 0; k < 2; k++)
                {
                    int n = t.EndNode[k]; if (n < 0 || t.EndKind[k] == 'e') continue;
                    // (solo si la vía llega de verdad a ese nudo: una recortada sale con el nudo de su extremo, que puede estar lejos)
                    var E = s._g.Edges[t.Edge];
                    if (n != (k == 0 ? E.A : E.B) && t.EndKind[k] != 'g' && t.EndKind[k] != 'j') continue;
                    if (s._offRange.TryGetValue(t.Edge, out var rg) && (k == 0 ? rg.a > 0.5 : rg.b < E.Len - 0.5)) continue;
                    if (!d.TryGetValue(n, out var l)) d[n] = l = new();
                    l.Add(k == 0 ? t.Pts[0] : t.Pts[^1]);
                }
            }
            return d;
        }

        // (el puesto) un esquema vacío: «ya se intentó y no salió»
        public static Schematic Placeholder() => new Schematic();
        public sealed class BandDef { public string Label; public List<int> Edges; public List<string> Stations; }

        // el esquema de la ruta: bands, las bandas (sus tramos y estaciones). log: (pruebas) qué se hizo
        // La línea principal (el camino de bandas de más metros) va de izquierda a derecha, cada banda a continuación de la
        // anterior; las demás, ramales, salen de su nudo hacia arriba o hacia abajo (hacia donde van en el mapa).
        // start: (2026-11-09) la estación donde empieza la línea principal, a la izquierda (en CGL, Estación Sur: su gran terminal)
        public static Schematic Route(RouteGraph g, RoomMapGeo geo, string label, IList<BandDef> bands, Action<string> log = null, string start = null)
        {
            // (2026-11-09, auditoría) cada tramo con andén, de la banda de su estación (por metros podía ir a la de al lado y la estación
            // no salía en ninguna: Castañeda)
            {
                var stBand = new Dictionary<string, BandDef>(StringComparer.Ordinal);
                foreach (var b in bands) foreach (var n in b.Stations) stBand[n] = b;
                var own = new Dictionary<int, BandDef>();
                foreach (var b in bands) foreach (int e in b.Edges) own[e] = b;
                foreach (var e in own.Keys.ToList())
                {
                    var pl = g.Edges[e].Platforms.Select(q => q.name).FirstOrDefault(n => n != null && stBand.ContainsKey(n));
                    if (pl == null || stBand[pl] == own[e] || g.Edges[e].Len > 800) continue;   // (solo vías de la estación: un tramo largo con un andén es la línea)
                    own[e].Edges = own[e].Edges.Where(x => x != e).ToList();
                    stBand[pl].Edges = stBand[pl].Edges.Append(e).ToList();
                    own[e] = stBand[pl];
                    log?.Invoke($"tramo {e} (andén de {pl}): a {stBand[pl].Label}");
                }
            }
            var all = bands.SelectMany(b => b.Edges).Distinct().ToList();
            var gsp = BandSpine(g, all);
            if (gsp == null) return null;
            var (gcum, ggrid) = PolyIndex(gsp);
            double GU((double x, double y) q) => PolyNearest(gsp, gcum, ggrid, q.x, q.y, true, true).u;

            // 1) cada banda, como sale
            var P = new List<(BandDef b, List<(double x, double y)> main, List<List<(double x, double y)>> br, Schematic s, double len, double u)>();
            foreach (var b in bands)
            {
                try
                {
                    var (main, br) = BandSpines(g, b.Edges, b.Stations);
                    if (main == null || main.Count < 2) { log?.Invoke($"{b.Label}: sin eje"); continue; }
                    var s = BuildBand(g, geo, b.Label, b.Stations, main, 1, b.Edges, br);
                    if (s == null || s.Tracks.Count == 0) { log?.Invoke($"{b.Label}: sin esquema"); continue; }
                    double len = 0;
                    for (int k = 1; k < main.Count; k++) len += Math.Sqrt((main[k].x - main[k - 1].x) * (main[k].x - main[k - 1].x) + (main[k].y - main[k - 1].y) * (main[k].y - main[k - 1].y));
                    P.Add((b, main, br, s, len, (GU(main[0]) + GU(main[^1])) / 2));
                }
                catch (Exception ex) { log?.Invoke($"{b.Label}: {ex.Message}"); }
            }
            if (P.Count == 0) return null;
            int N = P.Count;
            string Short(int i) => P[i].b.Label.Split(' ')[0];

            // 2) los empalmes entre bandas: un nudo de las dos, o unidos por vía que no dibuja ninguna (hasta 3 km)
            var conn = new HashSet<(int i, int n, int j, int m)>();
            void Connect()
            {
            conn.Clear();
            var drawn = new HashSet<int>(P.SelectMany(q => q.s._byEdge.Keys));
            var endNodes = P.Select(q => new HashSet<int>(Ends(q.s).Keys)).ToList();
            var owner = new Dictionary<int, List<int>>();
            for (int i = 0; i < N; i++) foreach (int n in endNodes[i]) { if (!owner.TryGetValue(n, out var l)) owner[n] = l = new(); l.Add(i); }
            for (int i = 0; i < N; i++)
                foreach (int n0 in endNodes[i])
                {
                    var dist = new Dictionary<int, double> { [n0] = 0 };
                    var pq = new PriorityQueue<int, double>(); pq.Enqueue(n0, 0);
                    while (pq.TryDequeue(out int n, out double d))
                    {
                        if (d > dist[n] + 1e-9) continue;
                        if (owner.TryGetValue(n, out var ow)) foreach (int j in ow) if (j > i) conn.Add((i, n0, j, n));
                        if (n != n0 && owner.ContainsKey(n)) continue;   // (llegó a otra pieza: no se sigue)
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
            bool Adj(int i, int j) => conn.Any(c => (c.i == i && c.j == j) || (c.i == j && c.j == i));
            // los nudos de i que empalman con j (y el de j en cada caso)
            List<(int mine, int other)> Joints(int i, int j) => conn.Where(c => c.i == i && c.j == j).Select(c => (c.n, c.m)).Concat(conn.Where(c => c.i == j && c.j == i).Select(c => (c.m, c.n))).ToList();
            for (int i = 0; i < N; i++) log?.Invoke($"{P[i].b.Label}: {P[i].len:0} m · empalma con {string.Join(", ", Enumerable.Range(0, N).Where(j => j != i && Adj(i, j)).Select(Short))}");

            // 3) la línea principal: el camino de bandas (sin repetir) de más metros, de oeste a este
            List<int> chain = new() { 0 }; double bestLen = -1;
            void Dfs(List<int> path, double acc)
            {
                if (acc > bestLen) { bestLen = acc; chain = path.ToList(); }
                for (int j = 0; j < N; j++) if (!path.Contains(j) && Adj(path[^1], j)) { path.Add(j); Dfs(path, acc + P[j].len); path.RemoveAt(path.Count - 1); }
            }
            int first = start == null ? -1 : P.FindIndex(q => q.b.Stations.Contains(start));
            if (first >= 0)
            {
                // (desde la banda de la estación de cabecera) el camino directo, el más corto, hasta la banda más alejada por la vía: la
                // línea; el más largo daba la vuelta por los anillos (Estación Sur → Gadea → Santa Ana → Princesa Susana)
                var dd = new Dictionary<int, double> { [first] = 0 }; var pv = new Dictionary<int, int>(); var pqb = new PriorityQueue<int, double>(); pqb.Enqueue(first, 0);
                while (pqb.TryDequeue(out int a0, out double d0))
                {
                    if (d0 > dd[a0] + 1e-9) continue;
                    for (int j = 0; j < N; j++) if (j != a0 && Adj(a0, j)) { double nd = d0 + P[j].len; if (!dd.TryGetValue(j, out double od) || nd < od) { dd[j] = nd; pv[j] = a0; pqb.Enqueue(j, nd); } }
                }
                int far = dd.OrderByDescending(kv => kv.Value).First().Key;
                chain = new List<int>(); for (int c = far; ; c = pv[c]) { chain.Insert(0, c); if (!pv.ContainsKey(c)) break; }
            }
            else
            {
                for (int i = 0; i < N; i++) Dfs(new List<int> { i }, P[i].len);
                if (P[chain[0]].u > P[chain[^1]].u) chain.Reverse();
            }
            // los ramales: de la ya colocada con la que empalman (primero las de la línea principal)
            var order = new List<int>(chain); var parent = new Dictionary<int, int>();
            for (int k = 1; k < chain.Count; k++) parent[chain[k]] = chain[k - 1];
            for (int qi = 0; qi < order.Count; qi++)
                for (int j = 0; j < N; j++) if (!order.Contains(j) && Adj(order[qi], j)) { order.Add(j); parent[j] = order[qi]; }
            foreach (int j in Enumerable.Range(0, N).Where(j => !order.Contains(j)).ToList()) order.Add(j);   // (sueltas)
            // (auditoría) un ramal que empalma con otro ramal ya puesto, de ese: sigue su línea (Santa Ana – Aeropuerto tras Gadea –
            // Industrias CAF); colgado de la línea principal, su empalme con el otro cruzaba todo el esquema
            for (int k = chain.Count; k < order.Count; k++)
            {
                int i = order[k];
                var cand = order.Take(k).Where(j => !chain.Contains(j) && Adj(i, j)).ToList();
                if (cand.Count > 0) parent[i] = cand[0];
            }
            log?.Invoke("línea principal: " + string.Join(" → ", chain.Select(Short)) + " · ramales: "
                        + string.Join(", ", order.Skip(chain.Count).Select(i => Short(i) + (parent.TryGetValue(i, out int pa) ? " (de " + Short(pa) + ")" : ""))));

            // 3b) (2026-11-09) la primera banda otra vez, con su eje desde la cabecera de la estación de salida (Estación Sur): la
            // línea se lee desde allí, y lo que sale de en medio (Ingeniero Pizias – Urb. Pere Comas) queda de ramal
            // (auditoría) y cada banda de la línea principal, de su empalme con la anterior al de la siguiente (la línea de verdad: con
            // el camino más largo, el eje de un nudo se iba por otro brazo y la estación quedaba atravesada, Castañeda); los ramales,
            // desde su empalme. Lo de los otros brazos va en ramales (BandSpines)
            foreach (int i in order)
            {
                int entry = -1, exit = -1; string entrySt = null; int ci = chain.IndexOf(i);
                if (ci == 0 && first >= 0) entrySt = start;   // (las demás: el eje por sus estaciones, BandSpine; giradas en el paso 4)
                try
                {
                    var (main, br) = BandSpines(g, P[i].b.Edges, P[i].b.Stations, entry, entrySt, exit);
                    if (main == null || main.Count < 2) continue;
                    var s2 = BuildBand(g, geo, P[i].b.Label, P[i].b.Stations, main, 1, P[i].b.Edges, br);
                    if (s2 != null && s2.Tracks.Count > 0) P[i] = (P[i].b, main, br, s2, P[i].len, P[i].u);
                    if (log != null)
                    {
                        var stl = Stations(g);
                        string NearSt((double x, double y) q) => stl.OrderBy(t => (t.x - q.x) * (t.x - q.x) + (t.y - q.y) * (t.y - q.y)).First().name;
                        var drawnSt = s2 == null ? "" : string.Join(", ", s2.Platforms.Select(q => q.Station).Where(q => q != null).Distinct());
                        log($"   {Short(i)} desde {(entrySt ?? "nudo " + entry)}: eje {NearSt(main[0])} → {NearSt(main[^1])} · ramales: {string.Join(", ", br.Select(q => NearSt(q[0]) + "→" + NearSt(q[^1])))} · andenes de: {drawnSt}");
                    }
                }
                catch (Exception ex) { log?.Invoke($"{P[i].b.Label} (desde su entrada): {ex.Message}"); }
            }
            Connect();

            // 4) el giro: los empalmes con la anterior (o con su madre), a la izquierda; la primera, con la siguiente a la derecha
            foreach (int i in order)
            {
                var s = P[i].s; bool wantLeft; List<int> nodes;
                if (parent.TryGetValue(i, out int pa)) { nodes = Joints(i, pa).Select(t => t.mine).ToList(); wantLeft = true; }
                else if (chain.Count > 1 && i == chain[0]) { nodes = Joints(i, chain[1]).Select(t => t.mine).ToList(); wantLeft = false; }
                else continue;
                var e0 = Ends(s);
                var xs = nodes.Where(e0.ContainsKey).SelectMany(n => e0[n]).Select(q => q.X).ToList();
                if (xs.Count == 0) continue;
                float mx = xs.Average();
                bool atLeft = mx - s.Bounds.Left < s.Bounds.Right - mx;
                if (atLeft != wantLeft)
                {
                    var s2 = BuildBand(g, geo, P[i].b.Label, P[i].b.Stations, P[i].main, -1, P[i].b.Edges, P[i].br);
                    if (s2 != null && s2.Tracks.Count > 0) P[i] = (P[i].b, P[i].main, P[i].br, s2, P[i].len, P[i].u);
                }
            }

            // 5) colocar
            const float Gap = 60, Margin = 50, BranchGap = 320;   // (BranchGap: entre un ramal y lo de encima o debajo, con sitio para los rótulos de sus estaciones)
            var placed = new List<int>();
            var sideOf = new Dictionary<int, int>();   // (ramales: 1 debajo, −1 encima)
            bool Hits(int i, float dx, float dy, float margin = Margin)
            {
                var s = P[i].s; var r0 = new RectangleF(s.Bounds.X + dx, s.Bounds.Y + dy - margin / 2, s.Bounds.Width, s.Bounds.Height + margin);
                return placed.Any(o => Overlap(P[o].s.Bounds, r0) > 4);
            }
            foreach (int i in order)
            {
                var s = P[i].s;
                if (placed.Count == 0) { s.Translate(-s.Bounds.Left, 0); placed.Add(i); continue; }
                float dx, dy;
                if (parent.TryGetValue(i, out int pa))
                {
                    var host = P[pa].s; var eh = Ends(host); var em = Ends(s);
                    var pairs = new List<(PointF a, PointF b)>();
                    foreach (var (mine, other) in Joints(i, pa))
                        if (em.TryGetValue(mine, out var lb) && eh.TryGetValue(other, out var la))
                            foreach (var bq in lb) pairs.Add((la.OrderBy(q => Math.Abs(q.Y - bq.Y)).First(), bq));
                    if (pairs.Count == 0) { dx = host.Bounds.Right + Gap - s.Bounds.Left; dy = 0; }
                    else
                    {
                        var grp = pairs.GroupBy(c => (int)Math.Round((c.a.Y - c.b.Y) / Row)).OrderByDescending(q => q.Count()).ThenBy(q => Math.Abs(q.Key)).First().ToList();
                        dy = grp[0].a.Y - grp[0].b.Y;
                        dx = pairs.Max(c => c.a.X + Gap - c.b.X);
                        if (chain.Contains(i)) dx = Math.Max(dx, host.Bounds.Right + Gap - s.Bounds.Left);
                    }
                    // (un ramal que sale del final de su madre sigue en la misma fila, a continuación, si cabe)
                    if (Hits(i, dx, dy) || (!chain.Contains(i) && s.Bounds.Left + dx < host.Bounds.Right + Gap * 0.5f))
                    {
                        // un ramal: encima o debajo de lo que ya hay, hacia donde va en el mapa (el lado de su madre en el que queda
                        // su mitad: v en el eje de la madre, y hacia dónde crecen las filas con v)
                        var mid = P[i].main[P[i].main.Count / 2];
                        double vb = host.AxisV(mid.x, mid.y);
                        double cv = host.Tracks.Average(t => t.V), cy = host.Tracks.Average(t => t.Pts.Average(q => q.Y));
                        double corr = host.Tracks.Sum(t => (t.V - cv) * (t.Pts.Average(q => q.Y) - cy));
                        bool down = (vb - cv) * corr > 0;
                        // (el ramal de un ramal, del mismo lado que su madre: si no, su unión cruzaría la línea principal)
                        if (sideOf.TryGetValue(pa, out int ps) && ps != 0) down = ps > 0;
                        sideOf[i] = down ? 1 : -1;
                        // (con sitio para los rótulos de sus estaciones, que van encima de sus andenes)
                        dy = down ? Math.Max(dy, host.Bounds.Bottom + BranchGap - s.Bounds.Top) : Math.Min(dy, host.Bounds.Top - BranchGap - s.Bounds.Bottom);
                        for (int k = 0; k < 400 && Hits(i, dx, dy, BranchGap); k++) dy += down ? Row : -Row;
                    }
                }
                else
                {
                    // suelta: debajo de todo
                    float bottom = placed.Max(o => P[o].s.Bounds.Bottom);
                    dx = -s.Bounds.Left; dy = bottom + Margin * 2 - s.Bounds.Top;
                }
                s.Translate(dx, dy);
                placed.Add(i);
                log?.Invoke($"{s.Station}: en x {s.Bounds.Left:0}..{s.Bounds.Right:0}, y {s.Bounds.Top:0}..{s.Bounds.Bottom:0}{(s.Flip < 0 ? " (girada)" : "")}");
            }

            // 6) el esquema: todas las piezas y las vías de los empalmes
            var parts = placed.Select(i => P[i].s).ToList();
            var r = new Schematic { _g = g, Station = label, _parts = parts, _partOf = new(), _bandSt = new HashSet<string>(StringComparer.Ordinal) };
            var endsAll = P.Select(q => Ends(q.s)).ToList();
            foreach (var (i, n, j, m) in conn)
            {
                if (!endsAll[i].TryGetValue(n, out var la) || !endsAll[j].TryGetValue(m, out var lb)) continue;
                var best = la.SelectMany(a => lb.Select(bq => (a, bq))).OrderBy(t => Math.Abs(t.a.X - t.bq.X) + Math.Abs(t.a.Y - t.bq.Y)).First();
                if (Math.Abs(best.a.X - best.bq.X) + Math.Abs(best.a.Y - best.bq.Y) < 0.5f) continue;
                var (A, B) = best.a.X <= best.bq.X ? (best.a, best.bq) : (best.bq, best.a);
                r.Links.Add(LinkPts(A, B));
            }
            foreach (var p in parts)
            {
                foreach (int e in p._byEdge.Keys) r._partOf.TryAdd(e, p);
                foreach (var nm in p._bandSt) r._bandSt.Add(nm);
                r.Tracks.AddRange(p.Tracks); r.Switches.AddRange(p.Switches); r.Slips.AddRange(p.Slips); r.Crossings.AddRange(p.Crossings);
                r.Bridges.AddRange(p.Bridges); r.SlipNodes.AddRange(p.SlipNodes); r.SlipGroups.AddRange(p.SlipGroups); r.ScissorGroups.AddRange(p.ScissorGroups);
                r.HiddenEdges.UnionWith(p.HiddenEdges); r.Missing.AddRange(p.Missing); r.Buffers.AddRange(p.Buffers); r.Signals.AddRange(p.Signals);
                r.LevelXs.AddRange(p.LevelXs); r.Platforms.AddRange(p.Platforms); r.SidingLabels.AddRange(p.SidingLabels); r.KmPosts.AddRange(p.KmPosts);
                r.Rows = Math.Max(r.Rows, p.Rows);
            }
            // las salidas: solo las que no van a otra pieza (las de los empalmes ya se ven unidas)
            var linkEnds = r.Links.SelectMany(l => new[] { l[0], l[^1] }).ToList();
            foreach (var p in parts)
                r.Exits.AddRange(p.Exits.Where(x => !linkEnds.Any(q => Math.Abs(q.X - x.At.X) < 70 && Math.Abs(q.Y - x.At.Y) < Row * 0.6f)
                                                     && !(x.Edge >= 0 && r._partOf.ContainsKey(x.Edge)) && !(x.Station != null && r._bandSt.Contains(x.Station))));
            var bb = parts[0].Bounds;
            foreach (var p in parts.Skip(1)) bb = RectangleF.Union(bb, p.Bounds);
            r.Bounds = bb;
            log?.Invoke($"empalmes: {conn.Count} · dibujados {r.Links.Count}");
            return r;
        }

        static float Overlap(RectangleF a, RectangleF b) => Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left)) > 0 ? Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top)) : 0;

        // la vía de un empalme: recta si van a la misma altura; si no, un tramo recto en cada punta y la diagonal en medio
        static PointF[] LinkPts(PointF a, PointF b)
        {
            if (Math.Abs(a.Y - b.Y) < 0.5f) return new[] { a, b };
            float dir = b.X >= a.X ? 1 : -1, w = Math.Abs(b.X - a.X), run = Math.Min(14, w / 4);
            return new[] { a, new PointF(a.X + dir * run, a.Y), new PointF(b.X - dir * run, b.Y), b };
        }

        // la pieza (banda) por su nombre
        public Schematic Part(string label) => _parts?.FirstOrDefault(p => p.Station == label);
        // el recuadro de la pieza en la que está un punto (el esquema entero si no es de la ruta)
        public RectangleF PartBounds(PointF q) => _parts?.FirstOrDefault(p => RectangleF.Inflate(p.Bounds, 2, 2).Contains(q))?.Bounds ?? Bounds;
    }
}
