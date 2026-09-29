// Teleindicadores (carteles de destino): muchos trenes traen una carpeta «Destinos» con una subcarpeta
// por destino (p. ej. Destinos\Atocha\destinos.ace). El cartel que se ve en Open Rails es el fichero que
// está en la carpeta del tren; para cambiarlo, el autor incluye un .bat que copia los de una subcarpeta
// encima (xcopy .\Destinos\X\*.* /y). SelectOR hace lo mismo al pulsar CONDUCIR con el destino elegido,
// guardando antes los originales (una sola vez) para poder volver a ellos. Open Rails lee las texturas
// al cargar el escenario, así que el cambio se elige ANTES de salir.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;

namespace SelectOR
{
    public static class Teleindicadores
    {
        // Carpeta de las copias de los originales (una subcarpeta por carpeta de tren).
        // SELECTOR_TELE_BACKUP: otra carpeta (la usan las pruebas para no tocar la del usuario).
        public static string BackupRoot
        {
            get
            {
                var env = Environment.GetEnvironmentVariable("SELECTOR_TELE_BACKUP");
                if (!string.IsNullOrWhiteSpace(env)) return env;
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Open Rails", "SelectOR_teleindicadores");
            }
        }

        static string DestDir(string trainDir)
        {
            try
            {
                foreach (var d in Directory.GetDirectories(trainDir))
                    if (string.Equals(Path.GetFileName(d), "Destinos", StringComparison.OrdinalIgnoreCase)) return d;
            }
            catch { }
            return null;
        }

        // Carpetas del tren (TRAINS\TRAINSET\…) usadas por la composición que tienen carpeta «Destinos».
        public static List<string> FoldersOf(string contentDir, string conPath)
        {
            var list = new List<string>();
            try
            {
                if (string.IsNullOrEmpty(contentDir) || string.IsNullOrEmpty(conPath) || !File.Exists(conPath)) return list;
                var doc = ConsistDoc.Load(conPath);
                if (doc == null) return list;
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var car in doc.Cars)
                {
                    if (string.IsNullOrWhiteSpace(car.Folder) || !seen.Add(car.Folder)) continue;
                    string dir = Path.Combine(contentDir, "TRAINS", "TRAINSET", car.Folder);
                    if (Directory.Exists(dir) && DestDir(dir) != null) list.Add(Path.GetFullPath(dir));
                }
            }
            catch { }
            return list;
        }

        // Destinos disponibles (unión de todas las carpetas, por orden alfabético): nombre de la subcarpeta
        // y el fichero de cartel que se enseña en la lista.
        public static List<(string name, string sample)> Destinations(List<string> folders)
        {
            var map = new SortedDictionary<string, string>(StringComparer.CurrentCultureIgnoreCase);
            foreach (var f in folders)
            {
                var dd = DestDir(f); if (dd == null) continue;
                string[] subs; try { subs = Directory.GetDirectories(dd); } catch { continue; }
                foreach (var s in subs)
                {
                    string name = Path.GetFileName(s);
                    if (map.ContainsKey(name)) continue;
                    string sample = SampleFile(s);
                    if (sample != null) map[name] = sample;
                }
            }
            return map.Select(kv => (kv.Key, kv.Value)).ToList();
        }

        // El cartel de un destino: destinos.ace si lo hay; si no, el primer .ace/.dds de la subcarpeta.
        static string SampleFile(string destSub)
        {
            try
            {
                var files = Directory.GetFiles(destSub).Where(IsTexture).ToList();
                if (files.Count == 0) return null;
                return files.FirstOrDefault(x => string.Equals(Path.GetFileNameWithoutExtension(x), "destinos", StringComparison.OrdinalIgnoreCase)) ?? files[0];
            }
            catch { return null; }
        }

        static bool IsTexture(string f)
        {
            string e = Path.GetExtension(f).ToLowerInvariant();
            return e == ".ace" || e == ".dds";
        }

        // Ficheros que se copian a la carpeta del tren (todos los de la subcarpeta del destino).
        static List<string> FilesOf(string destSub)
        {
            try { return Directory.GetFiles(destSub).Where(x => !string.Equals(Path.GetFileName(x), "desktop.ini", StringComparison.OrdinalIgnoreCase)).ToList(); }
            catch { return new List<string>(); }
        }

        static string SubOf(string trainDir, string name)
        {
            var dd = DestDir(trainDir); if (dd == null || string.IsNullOrEmpty(name)) return null;
            string s = Path.Combine(dd, name);
            return Directory.Exists(s) ? s : null;
        }

        // Destino que hay puesto ahora en el tren (el primer destino cuyos ficheros coinciden byte a byte
        // con los de la carpeta del tren), o null si ninguno coincide (el cartel original del autor).
        public static string Installed(List<string> folders)
        {
            foreach (var f in folders)
            {
                var dd = DestDir(f); if (dd == null) continue;
                string[] subs; try { subs = Directory.GetDirectories(dd); } catch { continue; }
                foreach (var s in subs)
                {
                    var files = FilesOf(s);
                    if (files.Count == 0) continue;
                    bool all = true;
                    foreach (var x in files)
                        if (!SameFile(x, Path.Combine(f, Path.GetFileName(x)))) { all = false; break; }
                    if (all) return Path.GetFileName(s);
                }
                return null;   // basta con la primera carpeta con destinos
            }
            return null;
        }

        static bool SameFile(string a, string b)
        {
            try
            {
                var fa = new FileInfo(a); var fb = new FileInfo(b);
                if (!fa.Exists || !fb.Exists || fa.Length != fb.Length) return false;
                return File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
            }
            catch { return false; }
        }

        // Copia de los originales de una carpeta de tren.
        public static string BackupDirOf(string trainDir)
        {
            string key = Path.GetFullPath(trainDir).TrimEnd('\\').ToLowerInvariant();
            uint h = 2166136261; foreach (char c in key) { h = (h ^ c) * 16777619; }
            string safe = new string(Path.GetFileName(key).Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray());
            if (safe.Length > 40) safe = safe.Substring(0, 40);
            return Path.Combine(BackupRoot, safe + "_" + h.ToString("x8"));
        }

        // Nombres de fichero que un destino puede cambiar en la carpeta del tren (los de todos los destinos).
        static HashSet<string> ManagedNames(string trainDir)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var dd = DestDir(trainDir); if (dd == null) return set;
            try { foreach (var s in Directory.GetDirectories(dd)) foreach (var x in FilesOf(s)) set.Add(Path.GetFileName(x)); } catch { }
            return set;
        }

        // Guarda los originales la PRIMERA vez que SelectOR va a cambiar el cartel de esa carpeta.
        static void EnsureBackup(string trainDir)
        {
            string bk = BackupDirOf(trainDir);
            if (File.Exists(Path.Combine(bk, "origen.txt"))) return;
            Directory.CreateDirectory(bk);
            foreach (var n in ManagedNames(trainDir))
            {
                string src = Path.Combine(trainDir, n);
                if (File.Exists(src)) File.Copy(src, Path.Combine(bk, n), true);
            }
            File.WriteAllText(Path.Combine(bk, "origen.txt"), trainDir);   // se escribe al final: copia completa
        }

        static bool HasBackup(string trainDir) => File.Exists(Path.Combine(BackupDirOf(trainDir), "origen.txt"));

        // Pone el destino en las carpetas del tren (name null o "" = los carteles originales).
        // Devuelve null si todo fue bien, o el motivo del fallo (p. ej. carpeta sin permiso de escritura).
        public static string Apply(List<string> folders, string name)
        {
            try
            {
                foreach (var f in folders)
                {
                    if (string.IsNullOrEmpty(name))
                    {
                        if (!HasBackup(f)) continue;   // nunca se cambió: ya está el original
                        string bk = BackupDirOf(f);
                        foreach (var n in ManagedNames(f))
                        {
                            string b = Path.Combine(bk, n);
                            if (File.Exists(b)) File.Copy(b, Path.Combine(f, n), true);
                        }
                        continue;
                    }
                    string sub = SubOf(f, name);
                    if (sub == null) continue;   // esta carpeta no tiene ese destino: se deja como está
                    EnsureBackup(f);
                    foreach (var x in FilesOf(sub)) File.Copy(x, Path.Combine(f, Path.GetFileName(x)), true);
                }
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }

        // Para la vista 3D: qué fichero de textura se enseña en lugar del de la carpeta del tren.
        public static Dictionary<string, string> Redirects(List<string> folders, string name)
        {
            var r = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in folders)
            {
                if (string.IsNullOrEmpty(name))
                {
                    if (!HasBackup(f)) continue;
                    string bk = BackupDirOf(f);
                    foreach (var n in ManagedNames(f))
                    {
                        string b = Path.Combine(bk, n);
                        if (File.Exists(b)) r[Path.Combine(f, n)] = b;
                    }
                    continue;
                }
                string sub = SubOf(f, name);
                if (sub == null) continue;
                foreach (var x in FilesOf(sub)) r[Path.Combine(f, Path.GetFileName(x))] = x;
            }
            return r;
        }

        // «La_Garriga» → «La Garriga».
        public static string Nice(string name) => string.IsNullOrEmpty(name) ? "" : name.Replace('_', ' ').Trim();

        // ---- Cartel LED para la lista: se recorta lo encendido y se estira a lo ancho (en el modelo el
        //      panel es mucho más ancho que la textura), sobre un fondo casi negro. ----
        static readonly Dictionary<string, Bitmap> _thumbs = new(StringComparer.OrdinalIgnoreCase);

        public static Bitmap LedThumb(string file, int height)
        {
            string key = file + "|" + height;
            if (_thumbs.TryGetValue(key, out var hit)) return hit;
            Bitmap res = null;
            try
            {
                using var src = Path.GetExtension(file).Equals(".dds", StringComparison.OrdinalIgnoreCase)
                    ? AceImage.LoadDds(file, out _) : AceImage.Load(file);
                var bb = LitBounds(src);
                if (bb.Width > 0 && bb.Height > 0)
                {
                    int inner = Math.Max(4, height - 6);
                    int w = Math.Max(8, (int)Math.Round(bb.Width * 5.0 * inner / bb.Height));
                    w = Math.Min(w, height * 12);
                    res = new Bitmap(w + 10, height, PixelFormat.Format32bppArgb);
                    using var g = Graphics.FromImage(res);
                    g.Clear(Color.FromArgb(8, 8, 8));
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                    g.DrawImage(src, new Rectangle(5, 3, w, inner), bb, GraphicsUnit.Pixel);
                }
            }
            catch { res = null; }
            _thumbs[key] = res;
            return res;
        }

        // Zona con píxeles encendidos (los LED); el resto de la textura es negro.
        static Rectangle LitBounds(Bitmap b)
        {
            int minX = b.Width, minY = b.Height, maxX = -1, maxY = -1;
            var bd = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var buf = new byte[bd.Stride * b.Height];
                System.Runtime.InteropServices.Marshal.Copy(bd.Scan0, buf, 0, buf.Length);
                for (int y = 0; y < b.Height; y++)
                    for (int x = 0; x < b.Width; x++)
                    {
                        int i = y * bd.Stride + x * 4;
                        int lum = buf[i] + buf[i + 1] + buf[i + 2];
                        if (lum > 120 && buf[i + 3] > 40)
                        {
                            if (x < minX) minX = x; if (x > maxX) maxX = x;
                            if (y < minY) minY = y; if (y > maxY) maxY = y;
                        }
                    }
            }
            finally { b.UnlockBits(bd); }
            if (maxX < 0) return new Rectangle(0, 0, b.Width, b.Height);
            return Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
        }
    }
}
