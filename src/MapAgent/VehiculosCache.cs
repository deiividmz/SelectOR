// Los vehículos de la carpeta de trenes con su largo, para sustituir los que faltan (1.2.80). Lo comparten el agente de
// SelectOR (en el Open Rails de cada jugador) y SelectOR.
// Cuando a un jugador le falta un vehículo del tren de otro, Open Rails busca uno de largo parecido entre TODOS los
// .eng/.wag de su carpeta de trenes, abriéndolos uno a uno (MPManager.GetList). Con una colección grande tarda minutos y
// lo hace en el hilo que lee la conexión con la sala: mientras tanto no lee ni manda nada y la conexión se cae
// (agente-mapa.log: 3 min entre entrar y «vehículo que falta», y el servidor la había cerrado a los 40 s).
// Aquí la lista se hace una vez y se guarda en disco (la ruta relativa, el largo y la fecha de cada archivo): las siguientes
// veces solo se leen los archivos nuevos o cambiados. SelectOR la prepara al abrirse, en segundo plano; el agente la carga
// al arrancar Open Rails y se la da hecha (MapAgent/Vehiculos.cs). Mismo formato que la de Open Rails: «carpeta\archivo».
// (1.2.81) Cada vehículo, también con su clase (e eléctrica, d diésel, s vapor, c coche de viajeros, f mercancías, ? no se
// sabe) y, aparte, las composiciones (.con de TRAINS\CONSISTS) que el jugador tiene enteras: para sustituir el tren que le
// falta por uno suyo parecido (MapAgent/Sustitutos.cs). Archivos «vehiculos2-…» y «composiciones-…».

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SelectOR.Comun
{
    public static class VehiculosCache
    {
        sealed class Item { public string Rel; public long Ticks; public double Len; public char Cls = '?'; }
        public sealed class Con { public string Name; public List<(string rel, bool eng)> Cars = new List<(string, bool)>(); }
        // (1.2.81) lo último que se ha preparado: cada vehículo (ruta relativa) con su largo y su clase, y las composiciones enteras
        public static Dictionary<string, (double len, char cls)> Info = new Dictionary<string, (double, char)>(StringComparer.OrdinalIgnoreCase);
        public static List<Con> Consists = new List<Con>();
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static readonly object _lock = new object();

        public static string CacheDir()
        {
            string dir = Environment.GetEnvironmentVariable("SELECTOR_CACHE_DIR");
            if (string.IsNullOrWhiteSpace(dir)) dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Open Rails", "SelectOR");
            return dir;
        }

        static string FileFor(string trainset)
        {
            using (var sha = SHA1.Create())
            {
                var h = sha.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(trainset).TrimEnd('\\', '/').ToLowerInvariant()));
                return Path.Combine(CacheDir(), "vehiculos2-" + BitConverter.ToString(h, 0, 6).Replace("-", "").ToLowerInvariant() + ".txt");
            }
        }

        // La lista de «basePath» (la carpeta con TRAINS): locomotoras y vagones, por largo (como MPManager.GetList: clave =
        // largo + algo al azar por debajo de 0,1 m para que no se repitan). null si no hay carpeta de trenes.
        public static (SortedList<double, string> eng, SortedList<double, string> wag, string info)? Build(string basePath)
        {
            string trainset = Path.Combine(basePath ?? "", "trains", "trainset");
            if (!Directory.Exists(trainset)) return null;
            lock (_lock)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                string cache = FileFor(trainset);
                var old = new Dictionary<string, Item>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    if (File.Exists(cache))
                        foreach (var line in File.ReadAllLines(cache, Encoding.UTF8))
                        {
                            var p = line.Split('|'); if (p.Length != 4) continue;
                            if (long.TryParse(p[1], NumberStyles.Integer, Inv, out long t) && double.TryParse(p[2], NumberStyles.Float, Inv, out double l))
                                old[p[0]] = new Item { Rel = p[0], Ticks = t, Len = l, Cls = p[3].Length > 0 ? p[3][0] : '?' };
                        }
                }
                catch { }
                var now = new List<Item>(); int read = 0, kept = 0;
                foreach (var pattern in new[] { "*.eng", "*.wag" })
                {
                    // (con FileInfo: la fecha viene con el listado, sin abrir cada archivo)
                    List<FileInfo> files;
                    try { files = new DirectoryInfo(trainset).EnumerateFiles(pattern, SearchOption.AllDirectories).ToList(); } catch { continue; }
                    foreach (var fi in files)
                    {
                        string f = fi.FullName;
                        string rel = f.Substring(trainset.Length).TrimStart('\\', '/');
                        if (!rel.Contains("\\") && !rel.Contains("/")) continue;   // (como Open Rails: solo los de una subcarpeta)
                        rel = rel.Replace('/', '\\');
                        long ticks; try { ticks = fi.LastWriteTimeUtc.Ticks; } catch { continue; }
                        if (old.TryGetValue(rel, out var o) && o.Ticks == ticks) { now.Add(o); kept++; continue; }
                        var (ln, cl) = LengthAndClass(f);
                        now.Add(new Item { Rel = rel, Ticks = ticks, Len = ln, Cls = cl }); read++;
                    }
                }
                if (read > 0 || now.Count != old.Count)
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(cache));
                        string tmp = cache + "." + Guid.NewGuid().ToString("N").Substring(0, 6) + ".tmp";
                        File.WriteAllLines(tmp, now.Select(x => $"{x.Rel}|{x.Ticks.ToString(Inv)}|{x.Len.ToString("0.###", Inv)}|{x.Cls}"), new UTF8Encoding(false));
                        if (File.Exists(cache)) File.Replace(tmp, cache, null); else File.Move(tmp, cache);
                    }
                    catch { }
                var info = new Dictionary<string, (double, char)>(StringComparer.OrdinalIgnoreCase);
                foreach (var x in now) info[x.Rel] = (x.Len, x.Cls);
                Info = info;
                // (1.2.81) las composiciones enteras (en segundo plano no molesta: ya se está en un hilo aparte)
                try { Consists = BuildConsists(basePath, info); } catch { }
                var rnd = new Random();
                var eng = new SortedList<double, string>(); var wag = new SortedList<double, string>();
                foreach (var x in now)
                {
                    var l = x.Rel.EndsWith(".eng", StringComparison.OrdinalIgnoreCase) ? eng : wag;
                    double k = x.Len + rnd.NextDouble() / 10.0;
                    while (l.ContainsKey(k)) k += 1e-6;
                    l.Add(k, x.Rel);
                }
                return (eng, wag, $"{eng.Count} locomotoras y {wag.Count} vagones ({kept} de la lista guardada, {read} leídos) y {Consists.Count} composiciones enteras en {sw.Elapsed.TotalSeconds:F1} s");
            }
        }

        // (1.2.81) Las composiciones de TRAINS\CONSISTS con todos sus vehículos en la carpeta de trenes (las demás no sirven
        // para sustituir). Guardadas en disco por fecha, como los vehículos.
        static readonly Regex CarRx = new Regex(@"\b(EngineData|WagonData)\s*\(\s*(?:""([^""]*)""|(\S+))\s+(?:""([^""]*)""|([^\s)]+))\s*\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static List<Con> BuildConsists(string basePath, Dictionary<string, (double, char)> have)
        {
            string dir = Path.Combine(basePath ?? "", "trains", "consists");
            var res = new List<Con>();
            if (!Directory.Exists(dir)) return res;
            string cache;
            using (var sha = SHA1.Create())
                cache = Path.Combine(CacheDir(), "composiciones-" + BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(dir).TrimEnd('\\', '/').ToLowerInvariant())), 0, 6).Replace("-", "").ToLowerInvariant() + ".txt");
            var old = new Dictionary<string, (long t, string cars)>(StringComparer.OrdinalIgnoreCase);
            try { if (File.Exists(cache)) foreach (var l in File.ReadAllLines(cache, Encoding.UTF8)) { var p = l.Split('|'); if (p.Length == 3 && long.TryParse(p[1], out long t)) old[p[0]] = (t, p[2]); } } catch { }
            var lines = new List<string>(); bool changed = false;
            List<FileInfo> files;
            try { files = new DirectoryInfo(dir).EnumerateFiles("*.con", SearchOption.TopDirectoryOnly).ToList(); } catch { return res; }
            foreach (var fi in files)
            {
                long ticks; try { ticks = fi.LastWriteTimeUtc.Ticks; } catch { continue; }
                string cars;
                if (old.TryGetValue(fi.Name, out var o) && o.t == ticks) cars = o.cars;
                else
                {
                    changed = true;
                    var sb = new StringBuilder();
                    string t = Read(fi.FullName) ?? "";
                    foreach (Match m in CarRx.Matches(t))
                    {
                        bool eng = m.Groups[1].Value.StartsWith("E", StringComparison.OrdinalIgnoreCase);
                        string name = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value, folder = m.Groups[4].Success ? m.Groups[4].Value : m.Groups[5].Value;
                        if (sb.Length > 0) sb.Append(';');
                        sb.Append(folder.Trim()).Append('\\').Append(name.Trim()).Append(eng ? ".eng" : ".wag");
                    }
                    cars = sb.ToString().Replace("|", "");
                }
                lines.Add(fi.Name + "|" + ticks.ToString(Inv) + "|" + cars);
                if (cars.Length == 0) continue;
                var c = new Con { Name = Path.GetFileNameWithoutExtension(fi.Name) };
                bool all = true;
                foreach (var r in cars.Split(';'))
                {
                    if (!have.ContainsKey(r)) { all = false; break; }
                    c.Cars.Add((r, r.EndsWith(".eng", StringComparison.OrdinalIgnoreCase)));
                }
                if (all && c.Cars.Count > 0) res.Add(c);
            }
            if (changed || lines.Count != old.Count)
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(cache));
                    string tmp = cache + "." + Guid.NewGuid().ToString("N").Substring(0, 6) + ".tmp";
                    File.WriteAllLines(tmp, lines, new UTF8Encoding(false));
                    if (File.Exists(cache)) File.Replace(tmp, cache, null); else File.Move(tmp, cache);
                }
                catch { }
            return res;
        }

        // (1.2.81) largo y clase: e/d/s (Engine: Type ( Electric / Diesel / Steam )), c/f (Wagon: Type ( Carriage / Freight ))
        static readonly Regex TypeRx = new Regex(@"\bType\s*\(\s*""?([A-Za-z]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex EngBlockRx = new Regex(@"\bEngine\s*\(", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static (double len, char cls) LengthAndClass(string file)
        {
            double len = Length(file);
            char cls = '?';
            try
            {
                string t = Read(file) ?? "";
                bool isEng = file.EndsWith(".eng", StringComparison.OrdinalIgnoreCase);
                if (isEng)
                {
                    var eb = EngBlockRx.Match(t);
                    var m = eb.Success ? TypeRx.Match(t, eb.Index) : null;
                    string ty = m != null && m.Success ? m.Groups[1].Value.ToLowerInvariant() : "";
                    cls = ty.StartsWith("elec") ? 'e' : ty.StartsWith("dies") ? 'd' : ty.StartsWith("steam") ? 's' : '?';
                }
                else
                {
                    int w = t.IndexOf("Wagon", StringComparison.OrdinalIgnoreCase);
                    var m = w >= 0 ? TypeRx.Match(t, w) : TypeRx.Match(t);
                    string ty = m.Success ? m.Groups[1].Value.ToLowerInvariant() : "";
                    cls = ty.StartsWith("carr") ? 'c' : ty.StartsWith("freight") ? 'f' : ty.StartsWith("engine") ? 'e' : ty.StartsWith("tender") ? 'f' : '?';
                }
            }
            catch { }
            return (len, cls);
        }

        // Largo (m) de «Size ( ancho alto largo )» dentro de «Wagon ( … )», con su «include» si lo tiene; 0 si no se sabe
        static readonly Regex SizeRx = new Regex(@"\bSize\s*\(\s*([-\d.]+)\s*([a-z]*)\s+([-\d.]+)\s*([a-z]*)\s+([-\d.]+)\s*([a-z]*)\s*\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex IncRx = new Regex(@"\binclude\s*\(\s*""?([^"")]+)""?\s*\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static double Length(string file)
        {
            string t = Read(file);
            if (t == null) return 0;
            var inc = IncRx.Match(t);
            if (inc.Success)
            {
                string it = Read(Path.Combine(Path.GetDirectoryName(file) ?? "", inc.Groups[1].Value.Trim()));
                if (it != null) t = it + "\n" + t;
            }
            // el «Size» del vagón (el primero después de «Wagon (»); si no, el primero que haya
            int w = t.IndexOf("Wagon", StringComparison.OrdinalIgnoreCase);
            var m = w >= 0 ? SizeRx.Match(t, w) : SizeRx.Match(t);
            if (!m.Success) m = SizeRx.Match(t);
            if (!m.Success || !double.TryParse(m.Groups[5].Value, NumberStyles.Float, Inv, out double v)) return 0;
            return v * Unit(m.Groups[6].Value);
        }

        static double Unit(string u)
        {
            switch ((u ?? "").ToLowerInvariant())
            {
                case "ft": return 0.3048;
                case "in": return 0.0254;
                case "cm": return 0.01;
                case "mm": return 0.001;
                default: return 1.0;
            }
        }

        static string Read(string f)
        {
            try
            {
                if (!File.Exists(f)) return null;
                var b = File.ReadAllBytes(f);
                if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return Encoding.Unicode.GetString(b);
                if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(b);
                if (b.Length >= 2 && b[1] == 0 && b[0] != 0) return Encoding.Unicode.GetString(b);
                return Encoding.GetEncoding(28591).GetString(b);
            }
            catch { return null; }
        }
    }
}
