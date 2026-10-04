// Novedades de esta versión, en español y en inglés, dentro del propio SelectOR (Novedades\es.txt y
// Novedades\en.txt, recursos incrustados). Las rellena el script de publicación con el mismo texto que
// la web. Formato: la primera línea es la versión; cada línea siguiente, «Título|texto».

using System;
using System.Collections.Generic;
using System.IO;

namespace SelectOR
{
    public static class ReleaseNotes
    {
        public sealed class Notes { public string Version = ""; public List<(string title, string text)> Items = new(); }

        public static Notes Load(bool english)
        {
            var n = new Notes();
            try
            {
                using var s = typeof(ReleaseNotes).Assembly.GetManifestResourceStream(english ? "novedades.en.txt" : "novedades.es.txt")
                              ?? typeof(ReleaseNotes).Assembly.GetManifestResourceStream("novedades.es.txt");
                if (s == null) return n;
                using var r = new StreamReader(s);
                string line; bool first = true;
                while ((line = r.ReadLine()) != null)
                {
                    line = line.Trim();
                    if (line.Length == 0) continue;
                    if (first) { n.Version = line; first = false; continue; }
                    int bar = line.IndexOf('|');
                    n.Items.Add(bar > 0 ? (line.Substring(0, bar).Trim(), line.Substring(bar + 1).Trim()) : ("", line));
                }
            }
            catch { }
            return n;
        }
    }
}
