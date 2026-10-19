// Figuras de tren para cuando un servicio programado no lleva un tren concreto (1.2.69): un tren de VIAJEROS (una 440
// de Cercanías) o uno de MERCANCÍAS (una 253 con portacontenedores, una cisterna y tolvas); «cualquier tren», el de
// viajeros y detrás la locomotora con dos portacontenedores. Son 2D de verdad, sacados con el mismo dibujo que los de
// Flota (de sus modelos 3D) y guardados dentro de SelectOR (Figuras\*.png); aquí se escalan a la altura que se pida.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace SelectOR
{
    public static class TrainFigures
    {
        static readonly Dictionary<string, Bitmap> _cache = new();
        static Bitmap _pax, _freight;

        static Bitmap Res(string name)
        {
            try
            {
                using var s = typeof(TrainFigures).Assembly.GetManifestResourceStream("figura." + name + ".png");
                return s == null ? null : new Bitmap(s);
            }
            catch { return null; }
        }

        // kind: «pax», «freight» o null (cualquiera). h: alto en píxeles. null si no hay figura.
        public static Bitmap For(string kind, int h)
        {
            h = Math.Max(12, h);
            string key = (kind ?? "any") + ":" + h;
            lock (_cache)
            {
                if (_cache.TryGetValue(key, out var b)) return b;
                _pax ??= Res("viajeros"); _freight ??= Res("mercancias");
                if (_pax == null || _freight == null) return null;
                b = kind == "pax" ? Scale(_pax, _pax.Width, h)
                  : kind == "freight" ? Scale(_freight, _freight.Width, h)
                  : Join(h);
                _cache[key] = b;
                return b;
            }
        }

        // (la imagen entera, o su parte izquierda de «srcW» de ancho) a «h» de alto
        static Bitmap Scale(Bitmap src, int srcW, int h)
        {
            int w = Math.Max(1, (int)Math.Round(srcW * (double)h / src.Height));
            var bmp = new Bitmap(w, h);
            using var g = Graphics.FromImage(bmp);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(src, new Rectangle(0, 0, w, h), new Rectangle(0, 0, srcW, src.Height), GraphicsUnit.Pixel);
            return bmp;
        }

        // cualquier tren: el de viajeros, un hueco y la locomotora de mercancías con sus dos primeros vagones
        static Bitmap Join(int h)
        {
            var a = Scale(_pax, _pax.Width, h);
            var b = Scale(_freight, (int)(_freight.Width * 0.5), h);
            int gap = Math.Max(4, h / 3);
            var bmp = new Bitmap(a.Width + gap + b.Width, h);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.FromArgb(28, 31, 36));
                g.DrawImage(a, 0, 0);
                g.DrawImage(b, a.Width + gap, 0);
            }
            a.Dispose(); b.Dispose();
            return bmp;
        }
    }
}
