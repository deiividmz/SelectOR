// Identificador de una ruta para todo lo que se comparte en el servidor (megafonía, mapa en vivo…):
// el RouteID de su .trk. En la interfaz se enseña siempre el Name. Varias rutas pueden tener el mismo
// Name (p. ej. dos «LARGA DISTANCIA») y una misma ruta puede venir en variantes con distinto Name y el
// mismo RouteID (CGL 2.2, LATAM, RE): con el RouteID lo compartido va a la ruta correcta.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace SelectOR
{
    public static class RouteIds
    {
        static readonly Dictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);

        // RouteID de la ruta de esa carpeta. Si el .trk no se puede leer: el Name y, si no, la carpeta.
        public static string IdOf(string routeDir, string name = null)
        {
            if (string.IsNullOrWhiteSpace(routeDir)) return (name ?? "").Trim();
            lock (_cache)
                if (_cache.TryGetValue(routeDir, out var hit)) return hit;
            string id = null;
            try
            {
                var trk = Directory.GetFiles(routeDir, "*.trk").FirstOrDefault();
                string text = trk != null ? ConsistDoc.ReadText(trk) : null;
                if (!string.IsNullOrEmpty(text))
                {
                    var m = Regex.Match(text, @"RouteID\s*\(\s*(?:""([^""]*)""|([^)\s][^)]*?))\s*\)", RegexOptions.IgnoreCase);
                    if (m.Success) id = (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Trim();
                }
            }
            catch { }
            if (string.IsNullOrWhiteSpace(id)) id = !string.IsNullOrWhiteSpace(name) ? name.Trim() : Path.GetFileName(routeDir.TrimEnd('\\', '/'));
            lock (_cache) _cache[routeDir] = id;
            return id;
        }
    }
}
