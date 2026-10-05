// Orden en %AppData%\Open Rails: SelectOR solo deja ahí lo que necesita (sus preferencias, la clave de
// firma de quien publica versiones, las copias de los teleindicadores originales y dos cachés que se
// podan solas). Al arrancar, en segundo plano, se borra lo que sobra:
//   · diagnósticos del pupitre y de la megafonía (solo se escriben si se piden con SELECTOR_CABLOG /
//     SELECTOR_PALOG = 1);
//   · restos de versiones antiguas (MenuParalelo.json, las carpetas de vistas previas que ya no se usan y las
//     composiciones 2D sin acoplar);
//   · índices de contenido que llevan 60 días sin usarse (se vuelven a crear si hacen falta);
//   · audios de megafonía que llevan 45 días sin sonar (se vuelven a bajar si hacen falta);
//   · copias de teleindicadores de trenes que ya no existen.
// Las cachés pueden ir a otra carpeta con SELECTOR_CACHE_DIR (lo usan las pruebas).

using System;
using System.IO;
using System.Linq;

namespace SelectOR
{
    public static class AppDataTidy
    {
        public static string OrData => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Open Rails");

        // Carpeta de las cachés (índice de contenido y audios de megafonía).
        public static string CacheRoot
        {
            get
            {
                var env = Environment.GetEnvironmentVariable("SELECTOR_CACHE_DIR");
                return !string.IsNullOrWhiteSpace(env) ? env : Path.Combine(OrData, "SelectOR");
            }
        }

        public static bool Flag(string name) => Environment.GetEnvironmentVariable(name) == "1";

        public static void Run()
        {
            // Con preferencias o cachés de prueba no se toca nada de la carpeta del usuario.
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SELECTOR_PREFS_FILE"))
                || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SELECTOR_CACHE_DIR"))) return;
            RunIn(OrData, Teleindicadores.BackupRoot);
        }

        // root = %AppData%\Open Rails (o una carpeta de pruebas); tele = copias de los teleindicadores.
        public static void RunIn(string root, string tele)
        {
            try
            {
                string sel = Path.Combine(root, "SelectOR");
                if (!Flag("SELECTOR_CABLOG")) Del(Path.Combine(sel, "pupitre-diagnostico.json"));
                if (!Flag("SELECTOR_PALOG")) Del(Path.Combine(sel, "megafonia.log"));

                // Versiones antiguas: preferencias con el nombre de antes (ya migradas) y vistas previas en disco.
                if (File.Exists(Path.Combine(root, "SelectOR.json"))) Del(Path.Combine(root, "MenuParalelo.json"));
                foreach (var d in new[] { "MenuParalelo_previews", "SelectOR_previews" }) DelPngDir(Path.Combine(root, d));
                DelPngDir(Path.Combine(sel, "composiciones"));   // composiciones 2D sin acoplar (hasta la 1.2.48)

                var now = DateTime.UtcNow;
                if (Directory.Exists(sel))
                    foreach (var f in Directory.GetFiles(sel, "contenido-*.idx"))
                        if (File.GetLastWriteTimeUtc(f) < now.AddDays(-60)) Del(f);

                string audio = Path.Combine(sel, "megafonias");
                if (Directory.Exists(audio))
                {
                    foreach (var f in Directory.GetFiles(audio))
                    {
                        string ext = Path.GetExtension(f).ToLowerInvariant();
                        if (ext == ".tag") { if (!File.Exists(f.Substring(0, f.Length - 4))) Del(f); continue; }   // etiqueta sin audio
                        if (File.GetLastWriteTimeUtc(f) < now.AddDays(-45)) { Del(f); Del(f + ".tag"); }
                    }
                    if (!Directory.EnumerateFileSystemEntries(audio).Any()) try { Directory.Delete(audio); } catch { }
                }

                if (!string.IsNullOrEmpty(tele) && Directory.Exists(tele))
                {
                    foreach (var d in Directory.GetDirectories(tele))
                    {
                        string o = Path.Combine(d, "origen.txt");
                        string train = File.Exists(o) ? File.ReadAllText(o).Trim() : null;
                        if (string.IsNullOrEmpty(train) || !Directory.Exists(train)) try { Directory.Delete(d, true); } catch { }
                    }
                    if (!Directory.EnumerateFileSystemEntries(tele).Any()) try { Directory.Delete(tele); } catch { }
                }
            }
            catch { }
        }

        static void Del(string f) { try { if (File.Exists(f)) File.Delete(f); } catch { } }

        // Carpeta de vistas previas antiguas: solo se borra si no tiene más que imágenes .png.
        static void DelPngDir(string d)
        {
            try
            {
                if (!Directory.Exists(d)) return;
                if (Directory.GetDirectories(d).Length > 0) return;
                if (Directory.GetFiles(d).Any(f => !f.EndsWith(".png", StringComparison.OrdinalIgnoreCase))) return;
                Directory.Delete(d, true);
            }
            catch { }
        }
    }
}
