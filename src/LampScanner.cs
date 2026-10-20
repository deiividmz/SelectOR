// Farolas de cualquier ruta para CYZYRails (CyzyLamps.cs): busca en los archivos de mundo de la ruta las formas
// colocadas como objeto estático cuyo nombre dice que dan luz (farola, foco, lámpara, luminaria, lamp, light, Leuchte,
// Laterne…; nunca las señales, que son otro tipo de objeto; «luz» sola no, que suele ser la eléctrica: postes, torres,
// casetas) y, en cada una (su .s, con el lector de Open Rails), dónde
// está la luz:
//  · si tiene piezas luminosas pequeñas (bombilla, fluorescente, cristal, la parte de noche…), en cada una;
//  · si la pieza luminosa es una tira larga (los fluorescentes de un techo), varias luces a lo largo, cada ~6 m;
//  · si no, en la cabeza del poste: arriba y, si tiene brazo, al final del brazo (en cada brazo si son varios).
// Lúmenes por la altura de la luz y color por las pistas de los nombres (naranja o sodio: 2000 K; fluorescente, LED o
// blanco: 4500 K; si no, 3000 K). Lo que no sale claro se deja fuera (mejor sin luz que una luz en mitad de un tejado).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Xna.Framework;
using Orts.Formats.Msts;

namespace SelectOR
{
    public static class LampScanner
    {
        public const string Version = "1";   // (cambia cuando cambia la forma de medir: las cachés se rehacen)
        // nombre de la forma: da luz (y no es otra cosa con esas letras)
        static readonly Regex LampName = new Regex(@"farol|lamp|l[aá]mpara|luminari|alumbrado|foco|proyector|reflector|light|licht|leuchte|laterne|lantern|kandelaber|lampa|lampy|svetl|swiatl|flood|piezaluz",
                                                   RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        static readonly Regex NotLamp = new Regex(@"se[nñ]al|signal|semaf|sig_|lightrail|light_rail|lighthouse|highlight|lightning|lightweight|flight|faro_?tren|headlight|tail_?light|lightbox|light_?box|panel|pulse|flash|destell|lampister|posteca|torre\d|caseta|subestaci|transfo|hilos|cono_luz|off\.s$|_off|apagad",
                                                  RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        // pieza luminosa (por su textura, su nombre o el de su matriz)
        static readonly Regex Glow = new Regex(@"night|noche|glow|bombilla|bulb|fluoresc|neon|luz|light|lumin|lente|lens|cristal|glass|lamp|emis|halo",
                                               RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        static readonly Regex Warm = new Regex(@"naranja|orange|sodio|sodium|amarill|yellow|vsap|hps", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        static readonly Regex Cool = new Regex(@"fluoresc|neon|led|blanc|white|azul|blue|cool|fri[oa]|tubo", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        static readonly Regex Bulb = new Regex(@"bombilla|bulb|globo|globe|esfera|sphere", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public sealed class Result { public string Text = ""; public int Shapes, Lamps, Placed, Skipped; public List<string> Notes = new List<string>(); }

        // Las formas que dan luz de la ruta y cuántas veces están colocadas (solo objetos estáticos).
        public static Dictionary<string, (string file, int n)> LampShapes(string routeDir)
        {
            var res = new Dictionary<string, (string, int)>(StringComparer.OrdinalIgnoreCase);
            string world = Path.Combine(routeDir, "WORLD");
            if (!Directory.Exists(world)) return res;
            foreach (var w in Directory.GetFiles(world, "*.w"))
            {
                WorldFile wf;
                try { wf = new WorldFile(w); } catch { continue; }
                foreach (var o in wf.Tr_Worldfile)
                {
                    if (!(o is StaticObj) || string.IsNullOrEmpty(o.FileName)) continue;
                    string name = Path.GetFileName(o.FileName.Replace('/', '\\'));
                    if (!LampName.IsMatch(name) || NotLamp.IsMatch(name)) continue;
                    res[name] = res.TryGetValue(name, out var c) ? (c.Item1, c.Item2 + 1) : (o.FileName, 1);
                }
            }
            return res;
        }

        public static Result Scan(string routeDir, ISet<string> skip = null)
        {
            var r = new Result();
            var sb = new StringBuilder();
            string basePath = Path.GetDirectoryName(Path.GetDirectoryName(routeDir.TrimEnd('\\')));
            foreach (var kv in LampShapes(routeDir).OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                string key = kv.Key.ToLowerInvariant();
                if (skip != null && skip.Contains(key)) continue;
                string path = Path.Combine(routeDir, "SHAPES", kv.Value.file.Replace('/', '\\'));
                if (!File.Exists(path)) path = Path.Combine(basePath ?? "", "GLOBAL", "SHAPES", kv.Value.file.Replace('/', '\\'));
                if (!File.Exists(path)) { r.Skipped++; r.Notes.Add($"{kv.Key}: no se encuentra el .s"); continue; }
                List<string> lines;
                try { lines = Measure(path, key, out string why); if (lines.Count == 0) { r.Skipped++; r.Notes.Add($"{kv.Key}: {why}"); continue; } }
                catch (Exception ex) { r.Skipped++; r.Notes.Add($"{kv.Key}: {ex.GetType().Name}"); continue; }
                sb.AppendLine($"# {kv.Key} ({kv.Value.n} en la ruta)");
                foreach (var l in lines) sb.AppendLine(l);
                r.Shapes++; r.Lamps += lines.Count; r.Placed += kv.Value.n;
            }
            r.Text = sb.ToString();
            return r;
        }

        sealed class Part { public string Name; public List<Vector3> V = new List<Vector3>(); }

        static Matrix M(matrix m) => new Matrix(m.AX, m.AY, m.AZ, 0, m.BX, m.BY, m.BZ, 0, m.CX, m.CY, m.CZ, 0, m.DX, m.DY, m.DZ, 1);

        // Las líneas de CyzyLamps.txt de una forma (vacío si no se ve claro dónde va la luz; «why» dice por qué).
        public static List<string> Measure(string path, string key, out string why)
        {
            why = null;
            var s = new ShapeFile(path, true).shape;
            var dl = s.lod_controls[0].distance_levels[0];
            var hier = dl.distance_level_header.hierarchy;
            Matrix World(int i)
            {
                var w = Matrix.Identity;
                for (int k = i, guard = 0; k >= 0 && k < s.matrices.Count && guard < 32; guard++)
                {
                    w *= M(s.matrices[k]);
                    if (hier == null || k >= hier.Length || hier[k] == k) break;
                    k = hier[k];
                }
                return w;
            }
            var parts = new List<Part>();
            foreach (var so in dl.sub_objects)
                foreach (var pr in so.primitives)
                {
                    var ps = s.prim_states[pr.prim_state_idx];
                    var vs = s.vtx_states[ps.ivtx_state];
                    string img = ps.tex_idxs != null && ps.tex_idxs.Length > 0 && ps.tex_idxs[0] < s.textures.Count ? s.images[s.textures[ps.tex_idxs[0]].iImage] : "";
                    string mname = vs.imatrix >= 0 && vs.imatrix < s.matrices.Count ? s.matrices[vs.imatrix].Name : "";
                    var p = new Part { Name = img + " " + ps.Name + " " + mname };
                    var w = World(vs.imatrix);
                    foreach (var t in pr.indexed_trilist.vertex_idxs)
                        foreach (int vi in new[] { t.a, t.b, t.c })
                        {
                            var pt = s.points[so.vertices[vi].ipoint];
                            p.V.Add(Vector3.Transform(new Vector3(pt.X, pt.Y, pt.Z), w));
                        }
                    if (p.V.Count > 0) parts.Add(p);
                }
            var all = parts.SelectMany(p => p.V).ToList();
            if (all.Count == 0) { why = "sin geometría"; return new List<string>(); }
            float minY = all.Min(v => v.Y), maxY = all.Max(v => v.Y);
            string names = string.Join(" ", parts.Select(p => p.Name)) + " " + key;
            int kelvin = Warm.IsMatch(names) ? 2000 : Cool.IsMatch(names) ? 4500 : 3000;
            var lamps = new List<(Vector3 at, bool omni)>();

            // 1) piezas luminosas: pequeñas (bombilla, fluorescente) o tiras largas; los halos grandes (planos de noche
            //    de varios metros) no dicen dónde está la luz y se dejan
            foreach (var p in parts.Where(p => Glow.IsMatch(p.Name)))
            {
                var lo = new Vector3(p.V.Min(v => v.X), p.V.Min(v => v.Y), p.V.Min(v => v.Z));
                var hi = new Vector3(p.V.Max(v => v.X), p.V.Max(v => v.Y), p.V.Max(v => v.Z));
                var size = hi - lo;
                float big = Math.Max(size.X, Math.Max(size.Y, size.Z));
                int bigAxes = (size.X > 1.2f ? 1 : 0) + (size.Y > 1.2f ? 1 : 0) + (size.Z > 1.2f ? 1 : 0);
                if (bigAxes >= 2) continue;                                  // halo o plano grande
                bool omni = Bulb.IsMatch(p.Name);
                if (big <= 1.5f) { lamps.Add(((lo + hi) / 2, omni)); continue; }
                if (size.Y > 1.5f) continue;                                 // algo vertical y alto: no es una luz
                // tira larga: a lo largo del eje mayor, cada ~6 m
                bool alongX = size.X >= size.Z;
                float len = alongX ? size.X : size.Z;
                int n = Math.Max(2, (int)Math.Round(len / 6f));
                for (int i = 0; i < n; i++)
                {
                    float f = (i + 0.5f) / n;
                    var at = (lo + hi) / 2;
                    if (alongX) at.X = lo.X + f * size.X; else at.Z = lo.Z + f * size.Z;
                    lamps.Add((at, false));
                }
            }
            lamps = Merge(lamps, 0.6f);

            // 2) si no hay piezas luminosas claras: una farola pequeña (de pared, de techo) entera es la luz; si no, la
            //    cabeza del poste (si es un poste: a media altura solo está el fuste, estrecho)
            if (lamps.Count == 0)
            {
                float h = maxY - minY;
                float wx = all.Max(v => v.X) - all.Min(v => v.X), wz = all.Max(v => v.Z) - all.Min(v => v.Z);
                if (h <= 1.6f && wx <= 1.6f && wz <= 1.6f && h > 0.05f)
                    lamps.Add((new Vector3((all.Max(v => v.X) + all.Min(v => v.X)) / 2, minY + h * 0.4f, (all.Max(v => v.Z) + all.Min(v => v.Z)) / 2), Bulb.IsMatch(names) || h > Math.Max(wx, wz)));
                else
                {
                    if (h < 2.5f) { why = $"no es un poste ({h:F1} m de alto) y no tiene piezas luminosas claras"; return new List<string>(); }
                    // (sin las piezas luminosas: los halos de noche son planos grandes alrededor de la farola)
                    var body = parts.Where(p => !Glow.IsMatch(p.Name)).SelectMany(p => p.V).ToList();
                    if (body.Count == 0) body = all;
                    // la sección a media altura: donde los triángulos de la forma cortan los planos a 0,4, 0,5 y 0,6 de su altura
                    var mid = new List<Vector3>();
                    foreach (var p in parts.Where(p => !Glow.IsMatch(p.Name) || body == all))
                        for (int i = 0; i + 2 < p.V.Count; i += 3)
                            foreach (float f in new[] { 0.4f, 0.5f, 0.6f })
                            {
                                float y = minY + f * h;
                                for (int e = 0; e < 3; e++)
                                {
                                    var a0 = p.V[i + e]; var a1 = p.V[i + (e + 1) % 3];
                                    if ((a0.Y - y) * (a1.Y - y) > 0 || Math.Abs(a1.Y - a0.Y) < 1e-4f) continue;
                                    mid.Add(Vector3.Lerp(a0, a1, (y - a0.Y) / (a1.Y - a0.Y)));
                                }
                            }
                    float midW = mid.Count == 0 ? 0 : Math.Max(mid.Max(v => v.X) - mid.Min(v => v.X), mid.Max(v => v.Z) - mid.Min(v => v.Z));
                    if (mid.Count == 0 || midW > 1.5f) { why = $"no es un poste ({(mid.Count == 0 ? "nada" : midW.ToString("F1") + " m")} de ancho a media altura) y no tiene piezas luminosas claras"; return new List<string>(); }
                    var axis = new Vector2(mid.Average(v => v.X), mid.Average(v => v.Z));
                    var top = body.Where(v => v.Y >= maxY - Math.Max(0.6f, h * 0.12f)).ToList();
                    if (top.Count == 0) top = all.Where(v => v.Y >= maxY - Math.Max(0.6f, h * 0.12f)).ToList();
                    float far = top.Max(v => Vector2.Distance(new Vector2(v.X, v.Z), axis));
                    if (far < 0.8f)
                        lamps.Add((new Vector3(axis.X, maxY - Math.Min(0.5f, h * 0.1f), axis.Y), false));   // farola de copa
                    else
                    {
                        // brazos: los extremos más alejados del poste, agrupados por dirección
                        var ends = top.Where(v => Vector2.Distance(new Vector2(v.X, v.Z), axis) >= far * 0.8f).ToList();
                        var groups = new List<List<Vector3>>();
                        foreach (var v in ends)
                        {
                            var d = Vector2.Normalize(new Vector2(v.X, v.Z) - axis);
                            var g = groups.FirstOrDefault(gr => Vector2.Dot(Vector2.Normalize(new Vector2(gr[0].X, gr[0].Z) - axis), d) > 0.7f);
                            if (g == null) groups.Add(g = new List<Vector3>());
                            g.Add(v);
                        }
                        foreach (var g in groups.Take(4))
                            lamps.Add((new Vector3(g.Average(v => v.X), g.Min(v => v.Y) - 0.05f, g.Average(v => v.Z)), false));
                    }
                }
            }
            if (lamps.Count == 0) { why = "no se ve dónde va la luz"; return new List<string>(); }
            if (lamps.Count > 12) { why = $"demasiadas piezas luminosas ({lamps.Count}): no parece una farola"; return new List<string>(); }

            var inv = CultureInfo.InvariantCulture;
            return lamps.Select(l =>
            {
                float y = l.at.Y - minY;   // altura sobre la base de la forma
                int lumen = y >= 9 ? 15000 : y >= 6 ? 9000 : y >= 3.5f ? 5000 : 3000;
                return string.Format(inv, "{0,-26} {1,6:F2} {2,6:F2} {3,6:F2} {4,6} {5,6}  {6}", key, l.at.X, l.at.Y, l.at.Z, lumen, kelvin, l.omni ? "omni" : "down");
            }).ToList();
        }

        static List<(Vector3 at, bool omni)> Merge(List<(Vector3 at, bool omni)> l, float d)
        {
            var res = new List<(Vector3 at, bool omni)>();
            foreach (var x in l) if (!res.Any(y => Vector3.Distance(x.at, y.at) < d)) res.Add(x);
            return res;
        }
    }
}
