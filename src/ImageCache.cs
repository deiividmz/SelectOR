// Cache asincrona de imagenes (ACE via AceImage; jpg/png/bmp nativo), con reescalado.
// Devuelve null la primera vez y decodifica en segundo plano; al terminar invoca onReady.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Threading.Tasks;

namespace SelectOR
{
    public static class ImageCache
    {
        class Entry { public Bitmap Bmp; public bool Failed; }

        static readonly Dictionary<string, Entry> _cache = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> _pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static readonly object _lock = new object();

        /// <summary>Devuelve el bitmap ya escalado (o null si aun no esta listo / ha fallado).</summary>
        public static Bitmap Get(string path, int maxW, int maxH, Action onReady)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            string key = path + "|" + maxW + "x" + maxH;
            lock (_lock)
            {
                if (_cache.TryGetValue(key, out var e)) return e.Failed ? null : e.Bmp;
                if (_pending.Contains(key)) return null;
                _pending.Add(key);
            }
            Task.Run(() =>
            {
                Bitmap scaled = null;
                try
                {
                    using (var raw = LoadRaw(path))
                        if (raw != null) scaled = Scale(raw, maxW, maxH);
                }
                catch { }
                lock (_lock)
                {
                    _pending.Remove(key);
                    _cache[key] = new Entry { Bmp = scaled, Failed = scaled == null };
                }
                if (onReady != null) { try { onReady(); } catch { } }
            });
            return null;
        }

        static Bitmap LoadRaw(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".ace") return AceImage.Load(path);
            using (var img = Image.FromFile(path))
                return new Bitmap(img); // copia para soltar el archivo
        }

        static Bitmap Scale(Bitmap src, int maxW, int maxH)
        {
            double s = Math.Min((double)maxW / src.Width, (double)maxH / src.Height);
            if (s > 1) s = 1;
            int w = Math.Max(1, (int)Math.Round(src.Width * s));
            int h = Math.Max(1, (int)Math.Round(src.Height * s));
            var b = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(b))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(src, 0, 0, w, h);
            }
            return b;
        }
    }
}
