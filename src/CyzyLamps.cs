// Farolas de verdad en CYZYRails (OrFlavor): CYZYRails ilumina el entorno con las farolas y focos de la ruta, pero
// necesita que se le diga cuáles son y dónde está su luz (Ruta\OpenRails\CyzyLamps.txt: forma .s, posición de la luz en
// la forma, lúmenes, temperatura de color y tipo). Las de cualquier ruta las busca y las mide LampScanner (en sus
// archivos de mundo y sus .s) y se guardan por ruta (%AppData%\Open Rails\SelectOR\cyzy-farolas\): solo se vuelven a
// buscar si cambian sus archivos de mundo. En CGL, además, van las medidas a mano (Routes: mandan sobre las buscadas):
//  · al lanzar Open Rails desde SelectOR, si es CYZYRails, se pone el archivo en la ruta que se va a conducir (si ya
//    hay uno que no es de SelectOR, no se toca); si hay que buscarlas, antes, en segundo plano (Prepare);
//  · al volver (y al abrir SelectOR) se quita. Así la ruta queda como estaba y los demás Open Rails ni lo ven.
// CYZYRails usa como mucho las 24 luces más cercanas (a menos de 800 m) y solo con sus gráficos mejorados.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SelectOR
{
    public static class CyzyLamps
    {
        const string Mark = "# SelectOR: farolas de CYZYRails";

        // por el principio del nombre de la carpeta de la ruta
        static readonly (string prefix, string text)[] Routes =
        {
            ("CGL", Mark + @" para CGL (se pone al conducir con CYZYRails y se quita al volver)
# forma .s                 x      y      z      lumen  kelvin  tipo
# focos de túnel (bombilla naranja, 382 en la ruta)
focotunel.s                0.22   0.23   0.20   3000   2000    omni
# farolas de forja (las tres del mismo modelo; la cabeza arriba del poste)
farola-gb.s                0.00   4.20   0.00   5000   3000    down
farola-gris-forjado.s      0.00   4.20   0.00   5000   3000    down
farola-roja.s              0.00   4.20   0.00   5000   3000    down
# farola de andén de Cercanías (fluorescente arriba, luz blanca azulada)
farolasud.s                0.00   4.25   0.00   4000   5000    down
# báculos con brazo (el poste en x = 2; la luminaria al final del brazo, hacia x = 0,3)
cplamp1ti.s                0.45   6.80   0.00   8000   2200    down
cplamp3ti.s                0.45   3.96   0.00   4000   2200    down
# fluorescentes del techo de las estaciones (tiras de 29 y 21 m: unas cuantas luces a lo largo)
piezaluzcipriano.s         0.20   0.00   0.50   4000   4000    down
piezaluzcipriano.s         0.20   0.00  -6.00   4000   4000    down
piezaluzcipriano.s         0.20   0.00 -12.50   4000   4000    down
piezaluzcipriano.s         0.20   0.00 -19.00   4000   4000    down
piezaluzcipriano.s         0.20   0.00 -24.50   4000   4000    down
piezaluzcolombia3.s        0.20   0.00   0.50   4000   4000    down
piezaluzcolombia3.s        0.20   0.00  -5.50   4000   4000    down
piezaluzcolombia3.s        0.20   0.00 -11.50   4000   4000    down
piezaluzcolombia3.s        0.20   0.00 -16.50   4000   4000    down
# marquesinas de estaciones (1.2.72): luz neutra (4000 K) hacia abajo, cada 4-14 m
# MarqueSur (25 en la ruta): el fluorescente, 98 m a lo largo
marquesur.s                          6.00   4.15   -0.10   3500   4000    down
marquesur.s                         18.57   4.15   -0.10   3500   4000    down
marquesur.s                         31.14   4.15   -0.10   3500   4000    down
marquesur.s                         43.71   4.15   -0.10   3500   4000    down
marquesur.s                         56.29   4.15   -0.10   3500   4000    down
marquesur.s                         68.86   4.15   -0.10   3500   4000    down
marquesur.s                         81.43   4.15   -0.10   3500   4000    down
marquesur.s                         94.00   4.15   -0.10   3500   4000    down
# marquepiziasvia1_3.s: el fluorescente del borde
marquepiziasvia1_3.s                 7.50   4.68    2.60   3000   4000    down
marquepiziasvia1_3.s                 7.50   4.68    6.90   3000   4000    down
# marquepiziasvia1_2.s: el fluorescente del borde
marquepiziasvia1_2.s                 7.50   4.68    2.60   3000   4000    down
marquepiziasvia1_2.s                 7.50   4.68    6.90   3000   4000    down
# marquepiziasvia1.s: el fluorescente del borde
marquepiziasvia1.s                   7.50   4.68    2.60   3000   4000    down
marquepiziasvia1.s                   7.50   4.68    6.90   3000   4000    down
# marquesina_caf.s: bajo la cubierta (sin fluorescente)
marquesina_caf.s                     0.10   3.15    2.50   3000   4000    down
marquesina_caf.s                     0.10   3.15   10.00   3000   4000    down
marquesina_caf.s                     0.10   3.15   17.50   3000   4000    down
# marquesina_caf_2.s: bajo la cubierta (sin fluorescente)
marquesina_caf_2.s                   0.10   3.15    2.50   3000   4000    down
marquesina_caf_2.s                   0.10   3.15   10.00   3000   4000    down
marquesina_caf_2.s                   0.10   3.15   17.50   3000   4000    down
# marquesinasantaana_2.s: el fluorescente, 155 m a lo largo
marquesinasantaana_2.s               1.58   6.50    6.00   3500   4000    down
marquesinasantaana_2.s               1.58   6.50   18.33   3500   4000    down
marquesinasantaana_2.s               1.58   6.50   30.67   3500   4000    down
marquesinasantaana_2.s               1.58   6.50   43.00   3500   4000    down
marquesinasantaana_2.s               1.58   6.50   55.33   3500   4000    down
marquesinasantaana_2.s               1.58   6.50   67.67   3500   4000    down
marquesinasantaana_2.s               1.58   6.50   80.00   3500   4000    down
marquesinasantaana_2.s               1.58   6.50   92.33   3500   4000    down
marquesinasantaana_2.s               1.58   6.50  104.67   3500   4000    down
marquesinasantaana_2.s               1.58   6.50  117.00   3500   4000    down
marquesinasantaana_2.s               1.58   6.50  129.33   3500   4000    down
marquesinasantaana_2.s               1.58   6.50  141.67   3500   4000    down
marquesinasantaana_2.s               1.58   6.50  154.00   3500   4000    down
# marquesinasantaana_1.s: el fluorescente, 155 m a lo largo
marquesinasantaana_1.s               1.58   6.50    6.00   3500   4000    down
marquesinasantaana_1.s               1.58   6.50   18.33   3500   4000    down
marquesinasantaana_1.s               1.58   6.50   30.67   3500   4000    down
marquesinasantaana_1.s               1.58   6.50   43.00   3500   4000    down
marquesinasantaana_1.s               1.58   6.50   55.33   3500   4000    down
marquesinasantaana_1.s               1.58   6.50   67.67   3500   4000    down
marquesinasantaana_1.s               1.58   6.50   80.00   3500   4000    down
marquesinasantaana_1.s               1.58   6.50   92.33   3500   4000    down
marquesinasantaana_1.s               1.58   6.50  104.67   3500   4000    down
marquesinasantaana_1.s               1.58   6.50  117.00   3500   4000    down
marquesinasantaana_1.s               1.58   6.50  129.33   3500   4000    down
marquesinasantaana_1.s               1.58   6.50  141.67   3500   4000    down
marquesinasantaana_1.s               1.58   6.50  154.00   3500   4000    down
# Colomé: bajo la cubierta entre sus postes (sin fluorescente)
colome.s                             2.80   4.20   27.00   3500   4000    down
colome.s                             2.80   4.20   38.43   3500   4000    down
colome.s                             2.80   4.20   49.86   3500   4000    down
colome.s                             2.80   4.20   61.29   3500   4000    down
colome.s                             2.80   4.20   72.71   3500   4000    down
colome.s                             2.80   4.20   84.14   3500   4000    down
colome.s                             2.80   4.20   95.57   3500   4000    down
colome.s                             2.80   4.20  107.00   3500   4000    down
# marqueaerop.s: bajo las vigas de la cubierta (18 × 10 m)
marqueaerop.s                        3.00   3.45    3.00   3000   4000    down
marqueaerop.s                        9.00   3.45    3.00   3000   4000    down
marqueaerop.s                       15.00   3.45    3.00   3000   4000    down
marqueaerop.s                        3.00   3.45    7.00   3000   4000    down
marqueaerop.s                        9.00   3.45    7.00   3000   4000    down
marqueaerop.s                       15.00   3.45    7.00   3000   4000    down
# marqueaeropcc.s: bajo las vigas de la cubierta (18 × 10 m)
marqueaeropcc.s                      3.00   3.45    3.00   3000   4000    down
marqueaeropcc.s                      9.00   3.45    3.00   3000   4000    down
marqueaeropcc.s                     15.00   3.45    3.00   3000   4000    down
marqueaeropcc.s                      3.00   3.45    7.00   3000   4000    down
marqueaeropcc.s                      9.00   3.45    7.00   3000   4000    down
marqueaeropcc.s                     15.00   3.45    7.00   3000   4000    down
# marqueaeropsc.s: bajo las vigas de la cubierta (18 × 10 m)
marqueaeropsc.s                      3.00   3.45    3.00   3000   4000    down
marqueaeropsc.s                      9.00   3.45    3.00   3000   4000    down
marqueaeropsc.s                     15.00   3.45    3.00   3000   4000    down
marqueaeropsc.s                      3.00   3.45    7.00   3000   4000    down
marqueaeropsc.s                      9.00   3.45    7.00   3000   4000    down
marqueaeropsc.s                     15.00   3.45    7.00   3000   4000    down
# marqueaeropfinal.s: bajo las vigas de la cubierta (18 × 10 m)
marqueaeropfinal.s                   3.00   3.45    3.00   3000   4000    down
marqueaeropfinal.s                   9.00   3.45    3.00   3000   4000    down
marqueaeropfinal.s                  15.00   3.45    3.00   3000   4000    down
marqueaeropfinal.s                   3.00   3.45    7.00   3000   4000    down
marqueaeropfinal.s                   9.00   3.45    7.00   3000   4000    down
marqueaeropfinal.s                  15.00   3.45    7.00   3000   4000    down
# apd_filella_edif.s: el techo de fluorescentes (13 m de ancho)
apd_filella_edif.s                   4.50   5.00    2.00   3500   4000    down
apd_filella_edif.s                  11.70   5.00    2.00   3500   4000    down
apd_filella_edif.s                   4.50   5.00   15.29   3500   4000    down
apd_filella_edif.s                  11.70   5.00   15.29   3500   4000    down
apd_filella_edif.s                   4.50   5.00   28.57   3500   4000    down
apd_filella_edif.s                  11.70   5.00   28.57   3500   4000    down
apd_filella_edif.s                   4.50   5.00   41.86   3500   4000    down
apd_filella_edif.s                  11.70   5.00   41.86   3500   4000    down
apd_filella_edif.s                   4.50   5.00   55.14   3500   4000    down
apd_filella_edif.s                  11.70   5.00   55.14   3500   4000    down
apd_filella_edif.s                   4.50   5.00   68.43   3500   4000    down
apd_filella_edif.s                  11.70   5.00   68.43   3500   4000    down
apd_filella_edif.s                   4.50   5.00   81.71   3500   4000    down
apd_filella_edif.s                  11.70   5.00   81.71   3500   4000    down
apd_filella_edif.s                   4.50   5.00   95.00   3500   4000    down
apd_filella_edif.s                  11.70   5.00   95.00   3500   4000    down
# apd_filella.s: el techo de fluorescentes (13 m de ancho)
apd_filella.s                        4.50   5.00    5.00   3500   4000    down
apd_filella.s                       11.70   5.00    5.00   3500   4000    down
apd_filella.s                        4.50   5.00   20.17   3500   4000    down
apd_filella.s                       11.70   5.00   20.17   3500   4000    down
apd_filella.s                        4.50   5.00   35.33   3500   4000    down
apd_filella.s                       11.70   5.00   35.33   3500   4000    down
apd_filella.s                        4.50   5.00   50.50   3500   4000    down
apd_filella.s                       11.70   5.00   50.50   3500   4000    down
apd_filella.s                        4.50   5.00   65.67   3500   4000    down
apd_filella.s                       11.70   5.00   65.67   3500   4000    down
apd_filella.s                        4.50   5.00   80.83   3500   4000    down
apd_filella.s                       11.70   5.00   80.83   3500   4000    down
apd_filella.s                        4.50   5.00   96.00   3500   4000    down
apd_filella.s                       11.70   5.00   96.00   3500   4000    down
# andenpgrandeconmarq.s: el fluorescente
andenpgrandeconmarq.s               -0.53   5.15    2.60   3000   4000    down
andenpgrandeconmarq.s               -0.53   5.15    7.30   3000   4000    down
# andenpgrandeconmarq_carteleria1.s: el fluorescente
andenpgrandeconmarq_carteleria1.s   -0.53   5.15    2.60   3000   4000    down
andenpgrandeconmarq_carteleria1.s   -0.53   5.15    7.30   3000   4000    down
# andenpgrandeconmarq_carteleria2.s: el fluorescente
andenpgrandeconmarq_carteleria2.s   -0.53   5.15    2.60   3000   4000    down
andenpgrandeconmarq_carteleria2.s   -0.53   5.15    7.30   3000   4000    down
# andenpgrandeconmarq_carteleria3.s: el fluorescente
andenpgrandeconmarq_carteleria3.s   -0.53   5.15    2.60   3000   4000    down
andenpgrandeconmarq_carteleria3.s   -0.53   5.15    7.30   3000   4000    down
# andenpgrandeconmarq_carteleria4.s: el fluorescente
andenpgrandeconmarq_carteleria4.s   -0.53   5.15    2.60   3000   4000    down
andenpgrandeconmarq_carteleria4.s   -0.53   5.15    7.30   3000   4000    down
# Susana, brazo corto: bajo la cubierta
susana_marquesina_brazo_corto.s      2.20   4.95    4.50   3500   4000    down
susana_marquesina_brazo_corto.s      2.20   4.95   16.25   3500   4000    down
susana_marquesina_brazo_corto.s      2.20   4.95   28.00   3500   4000    down
susana_marquesina_brazo_corto.s      2.20   4.95   39.75   3500   4000    down
susana_marquesina_brazo_corto.s      2.20   4.95   51.50   3500   4000    down
susana_marquesina_brazo_corto.s      2.20   4.95   63.25   3500   4000    down
susana_marquesina_brazo_corto.s      2.20   4.95   75.00   3500   4000    down
susana_marquesina_brazo_corto.s      2.20   4.95   86.75   3500   4000    down
susana_marquesina_brazo_corto.s      2.20   4.95   98.50   3500   4000    down
susana_marquesina_brazo_corto.s      2.20   4.95  110.25   3500   4000    down
susana_marquesina_brazo_corto.s      2.20   4.95  122.00   3500   4000    down
susana_marquesina_brazo_corto.s      2.20   4.95  133.75   3500   4000    down
susana_marquesina_brazo_corto.s      2.20   4.95  145.50   3500   4000    down
# Susana, brazo largo (10 m de ancho): dos filas bajo la cubierta
susana_marquesina_brazo_largo.s     -2.40   4.95    5.00   3000   4000    down
susana_marquesina_brazo_largo.s      2.80   4.95    5.00   3000   4000    down
susana_marquesina_brazo_largo.s     -2.40   4.95   16.67   3000   4000    down
susana_marquesina_brazo_largo.s      2.80   4.95   16.67   3000   4000    down
susana_marquesina_brazo_largo.s     -2.40   4.95   28.33   3000   4000    down
susana_marquesina_brazo_largo.s      2.80   4.95   28.33   3000   4000    down
susana_marquesina_brazo_largo.s     -2.40   4.95   40.00   3000   4000    down
susana_marquesina_brazo_largo.s      2.80   4.95   40.00   3000   4000    down
susana_marquesina_brazo_largo.s     -2.40   4.95   51.67   3000   4000    down
susana_marquesina_brazo_largo.s      2.80   4.95   51.67   3000   4000    down
susana_marquesina_brazo_largo.s     -2.40   4.95   63.33   3000   4000    down
susana_marquesina_brazo_largo.s      2.80   4.95   63.33   3000   4000    down
susana_marquesina_brazo_largo.s     -2.40   4.95   75.00   3000   4000    down
susana_marquesina_brazo_largo.s      2.80   4.95   75.00   3000   4000    down
susana_marquesina_brazo_largo.s     -2.40   4.95   86.67   3000   4000    down
susana_marquesina_brazo_largo.s      2.80   4.95   86.67   3000   4000    down
susana_marquesina_brazo_largo.s     -2.40   4.95   98.33   3000   4000    down
susana_marquesina_brazo_largo.s      2.80   4.95   98.33   3000   4000    down
susana_marquesina_brazo_largo.s     -2.40   4.95  110.00   3000   4000    down
susana_marquesina_brazo_largo.s      2.80   4.95  110.00   3000   4000    down
susana_marquesina_brazo_largo.s     -2.40   4.95  121.67   3000   4000    down
susana_marquesina_brazo_largo.s      2.80   4.95  121.67   3000   4000    down
susana_marquesina_brazo_largo.s     -2.40   4.95  133.33   3000   4000    down
susana_marquesina_brazo_largo.s      2.80   4.95  133.33   3000   4000    down
susana_marquesina_brazo_largo.s     -2.40   4.95  145.00   3000   4000    down
susana_marquesina_brazo_largo.s      2.80   4.95  145.00   3000   4000    down
# SusanaEdif: la marquesina del andén
susanaedif.s                        12.00   5.25    3.00   3000   4000    down
susanaedif.s                        16.00   5.25    3.00   3000   4000    down
susanaedif.s                        12.00   5.25   11.67   3000   4000    down
susanaedif.s                        16.00   5.25   11.67   3000   4000    down
susanaedif.s                        12.00   5.25   20.33   3000   4000    down
susanaedif.s                        16.00   5.25   20.33   3000   4000    down
susanaedif.s                        12.00   5.25   29.00   3000   4000    down
susanaedif.s                        16.00   5.25   29.00   3000   4000    down
# SusanaEdif: los soportales
susanaedif.s                         6.30   4.50   12.00   3000   4000    down
susanaedif.s                         6.30   4.50   20.00   3000   4000    down
susanaedif.s                         6.30   4.50   28.00   3000   4000    down
"),
        };

        // ── farolas buscadas (LampScanner), guardadas por ruta
        static string CacheDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Open Rails", "SelectOR", "cyzy-farolas");
        static string CacheFile(string route)
        {
            using var sha = System.Security.Cryptography.SHA1.Create();
            var h = sha.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(route).TrimEnd('\\').ToLowerInvariant()));
            return Path.Combine(CacheDir, Regex.Replace(Path.GetFileName(route), @"[^\w\-]", "_") + "-" + BitConverter.ToString(h, 0, 4).Replace("-", "") + ".txt");
        }

        // la huella de la ruta: cuántos archivos de mundo hay y el más reciente (y la versión de la búsqueda)
        static string Fingerprint(string route)
        {
            try
            {
                var w = new DirectoryInfo(Path.Combine(route, "WORLD")).GetFiles("*.w");
                long last = w.Length == 0 ? 0 : w.Max(f => f.LastWriteTimeUtc.Ticks);
                return $"# huella v{LampScanner.Version} {w.Length} {last}";
            }
            catch { return "# huella ?"; }
        }

        static string Cached(string route)
        {
            try
            {
                string f = CacheFile(route);
                if (!File.Exists(f)) return null;
                string text = File.ReadAllText(f);
                int nl = text.IndexOf('\n');
                return nl > 0 && text.Substring(0, nl).TrimEnd('\r') == Fingerprint(route) ? text.Substring(nl + 1) : null;
            }
            catch { return null; }
        }

        // ¿Hay que buscar las farolas de la ruta de estos argumentos? (para avisar «Preparando…» mientras tanto)
        public static bool NeedsScan(string args, string orDir = null)
        {
            if (!OrFlavor.IsCyzy(orDir)) return false;
            string route = RouteOf(args);
            return route != null && Directory.Exists(route) && Cached(route) == null;
        }

        // Busca las farolas de la ruta y las guarda (en segundo plano: con las rutas grandes, unos segundos).
        public static string Prepare(string args)
        {
            try
            {
                string route = RouteOf(args);
                if (route == null || !Directory.Exists(route)) return null;
                var r = LampScanner.Scan(route);
                var sb = new StringBuilder();
                sb.AppendLine(Fingerprint(route));
                sb.AppendLine($"# {Path.GetFileName(route)}: {r.Shapes} formas de farola, {r.Lamps} luces, {r.Placed} colocadas en la ruta (buscadas por SelectOR)");
                sb.Append(r.Text);
                foreach (var n in r.Notes) sb.AppendLine("# (fuera) " + n);
                Directory.CreateDirectory(CacheDir);
                File.WriteAllText(CacheFile(route), sb.ToString(), new UTF8Encoding(false));
                return $"farolas de CYZYRails: {Path.GetFileName(route)}: {r.Shapes} formas, {r.Lamps} luces";
            }
            catch (Exception ex) { return "farolas de CYZYRails: " + ex.GetType().Name + " " + ex.Message; }
        }

        static string StateFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Open Rails", "SelectOR", "cyzy-farolas.txt");

        // La carpeta de la ruta que se va a conducir, sacada de los argumentos de RunActivity (el .pat, la actividad o el
        // horario están dentro de ROUTES\<ruta>\…).
        public static string RouteOf(string args)
        {
            foreach (Match m in Regex.Matches(args ?? "", "\"([^\"]+)\""))
            {
                string p = m.Groups[1].Value;
                int k = p.IndexOf(@"\ROUTES\", StringComparison.OrdinalIgnoreCase);
                if (k < 0) continue;
                int end = p.IndexOf('\\', k + 8);
                return end > 0 ? p.Substring(0, end) : p;
            }
            return null;
        }

        // Al lanzar. Devuelve qué ha hecho (para el registro) o null si nada.
        public static string Install(string args, string orDir = null)
        {
            try
            {
                Remove();
                if (!OrFlavor.IsCyzy(orDir)) return null;
                string route = RouteOf(args);
                if (route == null || !Directory.Exists(route)) return null;
                string name = Path.GetFileName(route);
                string dir = Path.Combine(route, "OpenRails"), file = Path.Combine(dir, "CyzyLamps.txt");
                if (File.Exists(file) && !Ours(file)) return "farolas de CYZYRails: la ruta ya tiene su CyzyLamps.txt (no se toca)";
                // las medidas a mano de la ruta (si las hay) y las buscadas de las demás formas
                var def = Routes.FirstOrDefault(r => name.StartsWith(r.prefix, StringComparison.OrdinalIgnoreCase));
                string found = Cached(route);
                if (found == null) { Prepare(args); found = Cached(route) ?? ""; }
                var hand = def.text == null ? new string[0] : def.text.Replace("\r\n", "\n").Split('\n');
                var handShapes = new HashSet<string>(hand.Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("#")).Select(l => l.Split(' ')[0].ToLowerInvariant()));
                var sb = new StringBuilder();
                sb.AppendLine(Mark + $" para {name} (se pone al conducir con CYZYRails y se quita al volver)");
                foreach (var l in hand.Skip(1)) if (l.Trim().Length > 0) sb.AppendLine(l);
                int lamps = handShapes.Count == 0 ? 0 : hand.Count(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith("#"));
                foreach (var l in found.Replace("\r\n", "\n").Split('\n'))
                {
                    string t = l.Trim();
                    if (t.Length == 0) continue;
                    if (!t.StartsWith("#"))
                    {
                        if (handShapes.Contains(t.Split(' ')[0].ToLowerInvariant())) continue;   // (manda la medida a mano)
                        lamps++;
                    }
                    sb.AppendLine(l);
                }
                if (lamps == 0) return $"farolas de CYZYRails: {name}: ninguna farola reconocida";
                Directory.CreateDirectory(dir);
                File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false));
                Directory.CreateDirectory(Path.GetDirectoryName(StateFile));
                File.WriteAllText(StateFile, file);
                return $"farolas de CYZYRails: puestas en {name} ({lamps} luces)";
            }
            catch (Exception ex) { return "farolas de CYZYRails: " + ex.Message; }
        }

        // Al volver de conducir y al abrir SelectOR: fuera el archivo que se puso (solo si sigue siendo el de SelectOR).
        public static void Remove()
        {
            try
            {
                if (!File.Exists(StateFile)) return;
                string file = File.ReadAllText(StateFile).Trim();
                if (file.Length > 0 && File.Exists(file) && Ours(file)) File.Delete(file);
                File.Delete(StateFile);
            }
            catch { }
        }

        static bool Ours(string file)
        {
            try { using var r = new StreamReader(file); return (r.ReadLine() ?? "").StartsWith(Mark, StringComparison.Ordinal); }
            catch { return false; }
        }
    }
}
