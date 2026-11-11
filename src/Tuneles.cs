// (2026-11-09) Los túneles de una ruta, sobre su grafo de vías (para el puesto de mando): los tramos de cada vía que van por
// dentro de un túnel. Salen del mundo de la ruta (WORLD\*.w): las piezas de vía de túnel (su forma lleva «Tun»: las del
// tsection) y los tubos que se ponen encima de la vía normal («TunelAbadias10m», «TunelUnica100m», «JP1tunnel1»…, como hace
// la CGL). Cada pieza marca su vía a lo largo de su largo (el del nombre: «10m», «100m», «500r10d»; si no, 10 m) y lo que
// queda seguido (huecos de menos de 25 m) es un túnel. Se calcula una vez por ruta, en segundo plano.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SelectOR
{
    public static class Tuneles
    {
        public sealed class Tunnel { public int E; public double A, B; }   // tramo e, de A a B metros
        static readonly ConcurrentDictionary<string, Task<List<Tunnel>>> _cache = new(StringComparer.OrdinalIgnoreCase);

        public static Task<List<Tunnel>> For(string routeDir, RouteGraph g)
            => g == null || string.IsNullOrEmpty(routeDir) ? Task.FromResult(new List<Tunnel>()) : _cache.GetOrAdd(routeDir, _ => Task.Run(() => Build(routeDir, g)));

        static readonly Regex Tube = new(@"t[uú]nel|tunnel|(^|[^a-z])tun([^a-z]|$)|tun\.s$|tun[lr]?\.s$|rndtun", RegexOptions.IgnoreCase);
        static readonly Regex NotTube = new(@"foco|fluor|vent|luz|light|lamp|se[nñ]al|cartel|placa|cable|farol", RegexOptions.IgnoreCase);
        static readonly Regex Block = new(@"\b(TrackObj|Static)\s*\(", RegexOptions.IgnoreCase);
        static readonly Regex FileRx = new(@"FileName\s*\(\s*""?([^""\)\r\n]+?)""?\s*\)", RegexOptions.IgnoreCase);
        static readonly Regex PosRx = new(@"Position\s*\(\s*([-\d.eE+]+)\s+([-\d.eE+]+)\s+([-\d.eE+]+)\s*\)", RegexOptions.IgnoreCase);
        static readonly Regex TileRx = new(@"w([-+]\d+)([-+]\d+)\.w$", RegexOptions.IgnoreCase);

        // el largo de una pieza por su nombre: «100m» → 100; «500r10d» → radio × ángulo; si no, 10 m
        static double LenOf(string name)
        {
            var c = Regex.Match(name, @"(\d+(?:\.\d+)?)r(\d+(?:\.\d+)?)d", RegexOptions.IgnoreCase);
            if (c.Success) return double.Parse(c.Groups[1].Value, CultureInfo.InvariantCulture) * double.Parse(c.Groups[2].Value, CultureInfo.InvariantCulture) * Math.PI / 180;
            var m = Regex.Match(name, @"(\d+(?:\.\d+)?)\s*m(?![a-z])", RegexOptions.IgnoreCase);
            if (m.Success) { double v = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture); if (v >= 2 && v <= 2000) return v; }
            return 10;
        }

        static List<Tunnel> Build(string routeDir, RouteGraph g)
        {
            var res = new List<Tunnel>();
            try
            {
                string wd = Path.Combine(routeDir, "WORLD");
                if (!Directory.Exists(wd) || g?.Edges == null) return res;
                // la vía, muestreada cada 8 m, en una rejilla de 40 m (coordenadas del grafo, en metros)
                const double Step = 8, Cell = 40;
                var grid = new Dictionary<long, List<(int e, double off, double x, double y)>>();
                static long K(int a, int b) => ((long)a << 32) ^ (uint)b;
                for (int e = 0; e < g.Edges.Length; e++)
                {
                    double len = g.Edges[e].Len; int n = Math.Max(1, (int)Math.Ceiling(len / Step));
                    for (int i = 0; i <= n; i++)
                    {
                        double off = Math.Min(len, i * Step); var (x, y) = g.PointAt(e, off);
                        long k = K((int)Math.Floor(x / Cell), (int)Math.Floor(y / Cell));
                        if (!grid.TryGetValue(k, out var l)) grid[k] = l = new();
                        l.Add((e, off, x, y));
                    }
                }
                var marks = new Dictionary<int, List<(double a, double b)>>();
                foreach (var f in Directory.EnumerateFiles(wd, "*.w"))
                {
                    var tm = TileRx.Match(Path.GetFileName(f)); if (!tm.Success) continue;
                    int tx = int.Parse(tm.Groups[1].Value, CultureInfo.InvariantCulture), tz = int.Parse(tm.Groups[2].Value, CultureInfo.InvariantCulture);
                    string t;
                    try { var raw = File.ReadAllBytes(f); t = raw.Length > 1 && raw[0] == 0xFF && raw[1] == 0xFE ? Encoding.Unicode.GetString(raw, 2, raw.Length - 2) : Encoding.Latin1.GetString(raw); }
                    catch { continue; }
                    if (t.IndexOf("SIMISA@F", StringComparison.Ordinal) >= 0 && t.IndexOf("FileName", StringComparison.OrdinalIgnoreCase) < 0) continue;   // (comprimido: no se lee)
                    foreach (Match b in Block.Matches(t))
                    {
                        int end = Math.Min(t.Length, b.Index + 900);
                        string seg = t.Substring(b.Index, end - b.Index);
                        var fm = FileRx.Match(seg); if (!fm.Success) continue;
                        string fn = Path.GetFileName(fm.Groups[1].Value.Replace('\\', '/'));
                        if (!Tube.IsMatch(fn) || NotTube.IsMatch(fn)) continue;
                        var pm = PosRx.Match(seg); if (!pm.Success) continue;
                        double px = double.Parse(pm.Groups[1].Value, CultureInfo.InvariantCulture), pz = double.Parse(pm.Groups[3].Value, CultureInfo.InvariantCulture);
                        if (!OrGeo.TryLatLon(tx * 2048.0 + px, tz * 2048.0 + pz, out double la, out double lo)) continue;
                        var (gx, gy) = g.Proj(la, lo);
                        double L = LenOf(fn), reach = 6.5;
                        bool track = b.Groups[1].Value.Equals("TrackObj", StringComparison.OrdinalIgnoreCase);
                        // cada vía a menos de 6,5 m (un tubo puede cubrir dos vías): su punto más cercano
                        var best = new Dictionary<int, (double d, double off)>();
                        int cx = (int)Math.Floor(gx / Cell), cy = (int)Math.Floor(gy / Cell);
                        for (int ix = cx - 1; ix <= cx + 1; ix++)
                            for (int iy = cy - 1; iy <= cy + 1; iy++)
                                if (grid.TryGetValue(K(ix, iy), out var l))
                                    foreach (var (e, off, x, y) in l)
                                    {
                                        double d = Math.Sqrt((x - gx) * (x - gx) + (y - gy) * (y - gy));
                                        if (d <= reach + Step / 2 && (!best.TryGetValue(e, out var o) || d < o.d)) best[e] = (d, off);
                                    }
                        foreach (var kv in best)
                        {
                            if (kv.Value.d > reach + Step / 2) continue;
                            double half = track ? L * 0.6 : L / 2 + 2;   // (la pieza de vía empieza en su origen: hacia los dos lados, un poco más)
                            if (!marks.TryGetValue(kv.Key, out var ml)) marks[kv.Key] = ml = new();
                            ml.Add((Math.Max(0, kv.Value.off - half), Math.Min(g.Edges[kv.Key].Len, kv.Value.off + half)));
                        }
                    }
                }
                // lo seguido de cada vía (huecos de menos de 25 m), y solo lo que mide al menos 30 m
                foreach (var kv in marks)
                {
                    var l = kv.Value.OrderBy(x => x.a).ToList();
                    double a = l[0].a, bb = l[0].b;
                    void Flush() { if (bb - a >= 30) res.Add(new Tunnel { E = kv.Key, A = a, B = bb }); }
                    for (int i = 1; i < l.Count; i++)
                    {
                        if (l[i].a <= bb + 25) bb = Math.Max(bb, l[i].b);
                        else { Flush(); a = l[i].a; bb = l[i].b; }
                    }
                    Flush();
                }
            }
            catch { }
            return res;
        }
    }
}
