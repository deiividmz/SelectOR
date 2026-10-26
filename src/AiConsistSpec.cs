// HORARIO DE LA SALA (1.2.77): la composición de un servicio de trenes IA, descrita para el despachador de la sala, que
// no tiene las composiciones de cada uno (solo un juego genérico): cada vehículo —locomotora (E) o vagón (W), carpeta,
// nombre, largo (m), masa (t) y si va volteado— y la velocidad máxima de la composición (m/s). Con ella el despachador
// monta el tren con el genérico de largo parecido de cada vehículo, y a los jugadores les llegan los de verdad.
// Formato: «v=36.11;E,carpeta,nombre,20.50,84.0,0;W,…» (carpeta y nombre con %xx; sin «|» ni saltos de línea).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Linq;

namespace SelectOR
{
    public static class AiConsistSpec
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // baseDir: la carpeta de contenido (la que tiene TRAINS). null si no está la composición.
        public static string Build(string baseDir, string consist)
        {
            try
            {
                string con = Path.Combine(baseDir, "TRAINS", "CONSISTS", consist.EndsWith(".con", StringComparison.OrdinalIgnoreCase) ? consist : consist + ".con");
                if (!File.Exists(con)) return null;
                // (con el lector propio, ConsistDoc: el ConsistFile de Open Rails cambia de una versión a otra y en la del
                // jugador fallaba con «Method not found»)
                var cf = ConsistDoc.Load(con);
                if (cf == null) return null;
                string mv = (cf.MaxVelocity ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
                double vmax = double.TryParse(mv, NumberStyles.Float, Inv, out double vv) ? vv : 0;
                var sb = new StringBuilder("v=" + vmax.ToString("0.##", Inv));
                foreach (var wg in cf.Cars)
                {
                    string f = Path.Combine(baseDir, "TRAINS", "TRAINSET", wg.Folder, wg.Name + (wg.IsEngine ? ".eng" : ".wag"));
                    var (len, mass) = Measure(f);
                    sb.Append(';').Append(wg.IsEngine ? 'E' : 'W').Append(',')
                      .Append(Uri.EscapeDataString(wg.Folder ?? "")).Append(',').Append(Uri.EscapeDataString(wg.Name ?? "")).Append(',')
                      .Append(len.ToString("0.##", Inv)).Append(',').Append(mass.ToString("0.#", Inv)).Append(',').Append(wg.Flip ? '1' : '0');
                }
                return sb.ToString();
            }
            catch { return null; }
        }

        // Largo (de «Size ( ancho alto largo )») y masa (t) de un .eng/.wag; con su «include» si lo tiene. 0 si no se sabe.
        public static (double len, double mass) Measure(string file)
        {
            string t = Read(file);
            if (t == null) return (0, 0);
            var inc = Regex.Match(t, @"\binclude\s*\(\s*""?([^"")]+)""?\s*\)", RegexOptions.IgnoreCase);
            if (inc.Success)
            {
                string it = Read(Path.Combine(Path.GetDirectoryName(file) ?? "", inc.Groups[1].Value.Trim()));
                if (it != null) t = it + "\n" + t;   // (lo propio del archivo, después: manda)
            }
            double len = 0, mass = 0;
            var all = Regex.Matches(t, @"\bSize\s*\(\s*([-\d.]+)\s*([a-z]*)\s+([-\d.]+)\s*([a-z]*)\s+([-\d.]+)\s*([a-z]*)\s*\)", RegexOptions.IgnoreCase);
            if (all.Count > 0)
            {
                var m = all[all.Count - 1];
                if (double.TryParse(m.Groups[5].Value, NumberStyles.Float, Inv, out double v)) len = v * Unit(m.Groups[6].Value);
            }
            var ms = Regex.Matches(t, @"\bMass\s*\(\s*""?\s*([\d.]+)\s*([a-zA-Z-]*)", RegexOptions.IgnoreCase);
            if (ms.Count > 0)
            {
                var m = ms[ms.Count - 1];
                if (double.TryParse(m.Groups[1].Value, NumberStyles.Float, Inv, out double v))
                {
                    string u = m.Groups[2].Value.ToLowerInvariant();
                    mass = u.StartsWith("t-uk") ? v * 1.016 : u.StartsWith("t-us") ? v * 0.907 : u.StartsWith("t") ? v : u.StartsWith("kg") ? v / 1000 : u.StartsWith("lb") ? v * 0.000453592 : v > 1000 ? v / 1000 : v;
                }
            }
            return (len, mass);
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
                return b.Length >= 2 && (b[0] == 0xFF && b[1] == 0xFE || b[0] == 0xFE && b[1] == 0xFF) ? Encoding.Unicode.GetString(b) : Encoding.Latin1.GetString(b);
            }
            catch { return null; }
        }
    }
}
