// (2026-11-10, petición) LOS TRAYECTOS Y SU BLOQUEO: cada «Línea A – B» (los tramos de línea entre dos estaciones, de
// Schematic.Zones; los trozos «(2)», «(3)» del mismo par, juntos) y, dentro, cada vía seguida (una en vía única, dos en doble
// vía): sus tramos (de qué metro a qué metro de cada tramo del .tdb) y hacia qué estación va cada uno al recorrerlo en su
// sentido (de A a B del tramo). El sentido de bloqueo es por vía: hacia A, hacia B o sin establecer.
// Lo usan el Puesto de Mando (las flechas del esquema, DispatchForm.Bloqueo.cs) y el despachador de la sala (que lee estos
// mismos datos de bloqueos.txt, hecho con Trayectos.Txt: las señales contra el sentido, en parada). El número de cada
// trayecto y vía sale siempre igual de los mismos datos de la ruta (el orden es fijo).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SelectOR
{
    public static class Trayectos
    {
        public sealed class Item { public int Edge, Vec; public double From, To; public bool ToB; }   // ToB: al ir de A a B del tramo, hacia la estación B
        public sealed class Via { public string Id; public int K; public List<Item> Items = new(); public double Len => Items.Sum(i => i.To - i.From); }
        public sealed class Trayecto { public string Name, A, B; public List<string> Zones = new(); public List<Via> Vias = new(); }

        static readonly Regex Linea = new(@"^Línea (.+?) – (.+?)(?: \((\d+)\))?$");

        public static List<Trayecto> Build(RouteGraph g, IList<Schematic.Zone> zones, Action<string> log = null)
        {
            var res = new List<Trayecto>();
            var st = Schematic.Stations(g).GroupBy(s => s.name).ToDictionary(q => q.Key, q => (q.First().x, q.First().y), StringComparer.Ordinal);
            var groups = zones.Where(z => z.Line && Linea.IsMatch(z.Name)).GroupBy(z => { var m = Linea.Match(z.Name); return m.Groups[1].Value + " – " + m.Groups[2].Value; }).OrderBy(q => q.Key, StringComparer.Ordinal).ToList();
            // lo que dibuja cada trozo de cada trayecto (tramo, de qué metro a qué metro)
            var all = new Dictionary<string, Dictionary<int, (double a, double b)>>(StringComparer.Ordinal);
            foreach (var grp in groups)
            {
                var parts = new Dictionary<int, (double a, double b)>();
                foreach (var z in grp)
                {
                    Schematic s = null; try { s = Schematic.Build(g, null, z); } catch { }
                    if (s == null) continue;
                    foreach (int e in s.DrawnEdges)
                        if (s.DrawnRange(e, out double a, out double b) && b - a > 1)
                            parts[e] = parts.TryGetValue(e, out var o) ? (Math.Min(o.a, a), Math.Max(o.b, b)) : (a, b);
                }
                all[grp.Key] = parts;
            }
            Unshare(all, log);
            foreach (var grp in groups)
            {
                var m0 = Linea.Match(grp.First().Name);
                var t = new Trayecto { Name = "Línea " + grp.Key, A = m0.Groups[1].Value, B = m0.Groups[2].Value, Zones = grp.Select(z => z.Name).ToList() };
                if (!st.TryGetValue(t.A, out var ca) || !st.TryGetValue(t.B, out var cb)) { log?.Invoke($"{t.Name}: sin las dos estaciones"); continue; }
                var parts = all[grp.Key];
                if (parts.Count == 0) continue;
                // vías seguidas: en cada nudo, cada tramo sigue por el que sale más recto (en una aguja, el tronco con la rama más
                // recta): un escape entre las dos vías de una doble vía no las junta en una
                bool AtA(int e) => parts[e].a <= 1;
                bool AtB(int e) => parts[e].b >= g.Edges[e].Len - 1;
                var edges = parts.Keys.OrderBy(e => e).ToList();
                var grpOf = edges.ToDictionary(e => e, e => e);
                int Find(int e) { while (grpOf[e] != e) e = grpOf[e] = grpOf[grpOf[e]]; return e; }
                // hacia dónde sale un tramo de un nudo (el punto a 60 m, o lo que mida)
                (double x, double y) Out(int e, int n)
                {
                    double L = g.Edges[e].Len, d = Math.Min(60, L);
                    bool atA = g.Edges[e].A == n;
                    var p0 = g.PointAt(e, atA ? 0 : L); var p1 = g.PointAt(e, atA ? d : L - d);
                    double dx = p1.x - p0.x, dy = p1.y - p0.y, m = Math.Sqrt(dx * dx + dy * dy);
                    return m < 1e-6 ? (0, 0) : (dx / m, dy / m);
                }
                var nodes = new SortedSet<int>();
                foreach (int e in edges) { if (AtA(e) && g.Edges[e].A >= 0) nodes.Add(g.Edges[e].A); if (AtB(e) && g.Edges[e].B >= 0) nodes.Add(g.Edges[e].B); }
                foreach (int n in nodes)
                {
                    var here = g.EdgesAt(n).Distinct().Where(f => parts.ContainsKey(f) && g.Edges[f].A != g.Edges[f].B && (g.Edges[f].A == n ? AtA(f) : AtB(f))).OrderBy(f => f).ToList();
                    var pairs = new List<(int e, int f, double dot, double len)>();
                    for (int a = 0; a < here.Count; a++)
                        for (int b = a + 1; b < here.Count; b++)
                        {
                            var u = Out(here[a], n); var w = Out(here[b], n);
                            pairs.Add((here[a], here[b], u.x * w.x + u.y * w.y, parts[here[a]].b - parts[here[a]].a + parts[here[b]].b - parts[here[b]].a));
                        }
                    var used = new HashSet<int>();
                    foreach (var p in pairs.OrderBy(p => p.dot).ThenByDescending(p => p.len))
                    {
                        if (p.dot > -0.7 || used.Contains(p.e) || used.Contains(p.f)) continue;
                        used.Add(p.e); used.Add(p.f);
                        grpOf[Find(p.e)] = Find(p.f);
                    }
                }
                int k = 0;
                foreach (var comp in edges.GroupBy(Find).Select(q => q.ToList()).OrderBy(q => q.Min()))
                {
                    // sentido: desde el extremo de la vía más cerca de A, recorriéndola (cada tramo, entrando por un nudo)
                    (double x, double y) P(int e, bool atStart) => g.PointAt(e, atStart ? parts[e].a : parts[e].b);
                    double D2((double x, double y) p, (double x, double y) c) => (p.x - c.x) * (p.x - c.x) + (p.y - c.y) * (p.y - c.y);
                    // el punto de la vía más cerca de A y el más cerca de B (de sus extremos)
                    var ends = comp.SelectMany(e => new[] { (e, start: true), (e, start: false) }).ToList();
                    var endA = ends.OrderBy(q => D2(P(q.e, q.start), ca)).First();
                    var endB = ends.OrderBy(q => D2(P(q.e, q.start), cb)).First();
                    if (endA.e == endB.e && endA.start == endB.start) { log?.Invoke($"{t.Name}: vía {k} sin sentido claro"); continue; }
                    var via = new Via { K = k, Id = t.Name + "#" + k };
                    var seen = new HashSet<int>();
                    var queue = new Queue<(int e, bool fromStart)>(); queue.Enqueue((endA.e, endA.start));
                    while (queue.Count > 0)
                    {
                        var (e, fromStart) = queue.Dequeue();
                        if (!seen.Add(e)) continue;
                        via.Items.Add(new Item { Edge = e, Vec = g.Edges[e].Vec, From = parts[e].a, To = parts[e].b, ToB = fromStart });   // (entrando por su principio: de A a B del tramo, hacia B)
                        int exit = fromStart ? g.Edges[e].B : g.Edges[e].A;
                        bool exitDrawn = fromStart ? AtB(e) : AtA(e);
                        if (!exitDrawn || exit < 0) continue;
                        foreach (int f in g.EdgesAt(exit))
                            if (f != e && comp.Contains(f) && !seen.Contains(f)) queue.Enqueue((f, g.Edges[f].A == exit));
                    }
                    // (lo que no se alcanza así, por ejemplo un ramal: con el sentido de su vecino más cercano a A)
                    foreach (int e in comp.Where(e => !seen.Contains(e)))
                    {
                        bool toB = D2(P(e, true), ca) < D2(P(e, false), ca);
                        via.Items.Add(new Item { Edge = e, Vec = g.Edges[e].Vec, From = parts[e].a, To = parts[e].b, ToB = toB });
                    }
                    if (via.Len < 50) continue;   // (un trocito suelto: sin bloqueo)
                    t.Vias.Add(via); k++;
                }
                // las vías del trayecto: las de al menos la mitad de la más larga (fuera los escapes, ramales y playas sueltas)
                if (t.Vias.Count > 0)
                {
                    double max = t.Vias.Max(v => v.Len);
                    t.Vias = t.Vias.Where(v => v.Len >= max * 0.5).ToList();
                    for (int q = 0; q < t.Vias.Count; q++) { t.Vias[q].K = q; t.Vias[q].Id = t.Name + "#" + q; }
                    res.Add(t);
                }
            }
            return res;
        }

        // (2026-11-10, auditoría) lo que dibujan dos trayectos a la vez (el trozo de una estación entre los dos, p. ej. Pueblo
        // Novo entre Orobón – Pueblo Novo y Pueblo Novo – San Yago): de ninguno. Cada uno se recorta por el lado que toca a ese
        // trozo (si queda en medio, se queda con el lado más largo)
        static void Unshare(Dictionary<string, Dictionary<int, (double a, double b)>> all, Action<string> log)
        {
            var keys = all.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
            var orig = keys.ToDictionary(k => k, k => new Dictionary<int, (double a, double b)>(all[k]), StringComparer.Ordinal);
            for (int i = 0; i < keys.Count; i++)
                for (int j = i + 1; j < keys.Count; j++)
                    foreach (var e in orig[keys[i]].Keys.Where(orig[keys[j]].ContainsKey).ToList())
                    {
                        var p = orig[keys[i]][e]; var q = orig[keys[j]][e];
                        double lo = Math.Max(p.a, q.a), hi = Math.Min(p.b, q.b);
                        if (hi - lo <= 1) continue;
                        log?.Invoke($"tramo {e}: {lo:0}..{hi:0} m en «{keys[i]}» y «{keys[j]}»: de ninguno");
                        foreach (var k in new[] { keys[i], keys[j] })
                        {
                            if (!all[k].TryGetValue(e, out var r)) continue;
                            if (lo <= r.a + 1 && hi >= r.b - 1) { all[k].Remove(e); continue; }
                            if (lo <= r.a + 1) r = (hi, r.b);
                            else if (hi >= r.b - 1) r = (r.a, lo);
                            else r = lo - r.a >= r.b - hi ? (r.a, lo) : (hi, r.b);
                            if (r.b - r.a > 1) all[k][e] = r; else all[k].Remove(e);
                        }
                    }
        }

        // la huella de los trayectos (la misma que calcula el despachador, Bloqueo.Hash): FNV-1a del texto entero de bloqueos.txt
        // (vías y tramos: si cambia algo, no se mezclan versiones)
        public static string Hash(List<Trayecto> l)
        {
            uint h = 2166136261;
            foreach (char c in Txt(l)) { h ^= c; h *= 16777619; }
            return h.ToString("x8", CultureInfo.InvariantCulture);
        }

        // bloqueos.txt para el despachador (una línea por trayecto y otra por cada una de sus vías, separadas por tabuladores):
        //   T  nombre  A  B  zona|zona…
        //   V  id  vec,desde,hasta,haciaB;…      (haciaB: 1 si al ir de A a B del tramo se va hacia la estación B)
        public static string Txt(List<Trayecto> l)
        {
            var I = CultureInfo.InvariantCulture; var sb = new StringBuilder();
            foreach (var t in l)
            {
                sb.Append("T\t").Append(t.Name).Append('\t').Append(t.A).Append('\t').Append(t.B).Append('\t').Append(string.Join("|", t.Zones)).Append('\n');
                foreach (var v in t.Vias)
                    sb.Append("V\t").Append(v.Id).Append('\t')
                      .Append(string.Join(";", v.Items.Select(it => $"{it.Vec},{it.From.ToString("0.0", I)},{it.To.ToString("0.0", I)},{(it.ToB ? 1 : 0)}"))).Append('\n');
            }
            return sb.ToString();
        }
    }
}
