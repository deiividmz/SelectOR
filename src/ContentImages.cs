// Localiza las imagenes de una ruta (LoadingScreen / Graphic del .trk) y de un tren
// (foto raster junto al .eng, solo si es fiable). No decodifica: solo devuelve rutas de archivo.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SelectOR
{
    public static class ContentImages
    {
        static readonly Dictionary<string, (string banner, string thumb)> _routeCache =
            new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Devuelve (banner, miniatura) para la carpeta de una ruta.</summary>
        public static (string banner, string thumb) ForRoute(string routeDir)
        {
            if (string.IsNullOrEmpty(routeDir)) return (null, null);
            lock (_routeCache)
                if (_routeCache.TryGetValue(routeDir, out var hit)) return hit;

            (string, string) result = (null, null);
            try
            {
                var trk = Directory.GetFiles(routeDir, "*.trk").FirstOrDefault();
                string graphic = null, loading = null, wide = null;
                if (trk != null)
                {
                    var text = ReadText(trk);
                    graphic = Token(text, "graphic");
                    loading = Token(text, "loadingscreen");
                    wide = Token(text, "ortsloadingscreenwide");
                }
                string banner = FirstExisting(routeDir, wide, loading, graphic, "load.ace", "Load.ace", "details.ace");
                string thumb = FirstExisting(routeDir, graphic, loading, wide) ?? banner;
                result = (banner, thumb);
            }
            catch { }

            lock (_routeCache) _routeCache[routeDir] = result;
            return result;
        }

        static readonly string[] RasterExt = { ".jpg", ".jpeg", ".png", ".bmp" };
        static readonly string[] BadHints =
            { "cab", "interior", "cafeteria", "estandar", "extremo", "ambient", "detail",
              "night", "noche", "_i", "_n", "_s", "spec", "norm", "menu", "logo", "panel" };
        static readonly string[] GoodHints =
            { "preview", "perfil", "profile", "side", "lateral", "loco", "train", "tren", "foto", "photo", "render" };

        static System.Collections.Generic.IEnumerable<string> SafeFiles(string dir, SearchOption opt)
        {
            try { return Directory.GetFiles(dir, "*.*", opt); }
            catch { return System.Array.Empty<string>(); }
        }

        /// <summary>Foto de vista previa de un tren, solo si es razonablemente fiable; si no, null.</summary>
        public static string ForLocomotive(string engFilePath)
        {
            try
            {
                if (string.IsNullOrEmpty(engFilePath)) return null;
                var dir = Path.GetDirectoryName(engFilePath);
                if (dir == null || !Directory.Exists(dir)) return null;
                var baseName = Path.GetFileNameWithoutExtension(engFilePath).ToLowerInvariant();

                // Busca en la carpeta del modelo y en subcarpetas inmediatas (algunos packs
                // guardan las imagenes en subcarpetas tipo "TEXTURES" o "DOC").
                var rasters = SafeFiles(dir, SearchOption.TopDirectoryOnly)
                    .Concat(SafeFiles(dir, SearchOption.AllDirectories))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Where(f => RasterExt.Contains(Path.GetExtension(f).ToLowerInvariant()))
                    .ToList();
                if (rasters.Count == 0) return null;

                // 0) PRIORIDAD: un archivo de vista previa convencional (preview.jpg/png/bmp).
                var preview = rasters.FirstOrDefault(f =>
                    Path.GetFileNameWithoutExtension(f).Equals("preview", StringComparison.OrdinalIgnoreCase));
                if (preview != null) return preview;

                // 1) coincidencia por nombre con el .eng
                var byName = rasters.FirstOrDefault(f =>
                {
                    var n = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
                    return n == baseName || n.Contains(baseName) || baseName.Contains(n);
                });
                if (byName != null) return byName;

                // 2) descartar interiores/detalles/mapas y quedarnos con la mejor candidata "limpia"
                var clean = rasters.Where(f =>
                {
                    var n = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
                    return !BadHints.Any(h => n.Contains(h));
                }).ToList();
                if (clean.Count == 1) return clean[0];
                // Si hay varias limpias, preferimos una con pinta de foto de perfil (nombre con hints buenos).
                var profile = clean.FirstOrDefault(f =>
                {
                    var n = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
                    return GoodHints.Any(h => n.Contains(h));
                });
                if (profile != null) return profile;

                return null; // ambiguo: mejor icono generico
            }
            catch { return null; }
        }

        // ---------- helpers ----------
        static string ReadText(string path)
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes);
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes);
            return Encoding.UTF8.GetString(bytes);
        }

        static string Token(string text, string token)
        {
            var m = Regex.Match(text, token + @"\s*\(\s*([^)]+?)\s*\)", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value.Trim().Trim('"').Trim() : null;
        }

        static string FirstExisting(string dir, params string[] names)
        {
            foreach (var n in names)
            {
                if (string.IsNullOrWhiteSpace(n)) continue;
                var p = Path.Combine(dir, n);
                if (File.Exists(p)) return p;
            }
            return null;
        }
    }
}
