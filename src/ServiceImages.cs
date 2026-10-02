// Composición 2D de cada servicio (sql/servicio-imagen.sql): una imagen JPEG del tren tal como lo llevaba el
// maquinista, en el almacén privado «servicios» (<empresa>/<servicio>.jpg). La dibuja SU PC al ponerse de
// servicio (él sí tiene esos vehículos) y así la ve cualquier socio, tenga o no ese tren en su contenido.
//  · La tira: cada coche visto de lado (ShapeRenderer.RenderSide), uno detrás de otro sobre un carril, con la
//    cabeza a la izquierda; los coches que no se pueden dibujar quedan como un hueco con su nombre.
//  · Caché local en %AppData%\…\servicios: se descarga una sola vez.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;

namespace SelectOR
{
    public static class ServiceImages
    {
        public const string Bucket = "servicios";
        const float WorldH = 5.6f;     // altura de encuadre (m) de cada coche, como en el editor
        const int MaxWidth = 3600;     // ancho máximo de la tira (los trenes muy largos se dibujan más pequeños)

        static string CacheDir
        {
            get
            {
                string d = Path.Combine(AppDataTidy.CacheRoot, "servicios");
                try { Directory.CreateDirectory(d); } catch { }
                return d;
            }
        }

        static bool Ids(string company, string service) => Guid.TryParse(company, out _) && Guid.TryParse(service, out _);
        static string ObjectPath(string company, string service) => company.ToLowerInvariant() + "/" + service.ToLowerInvariant() + ".jpg";
        public static string LocalFile(string service) => Path.Combine(CacheDir, (service ?? "").ToLowerInvariant() + ".jpg");

        // ---------------- dibujo ----------------
        // Debe llamarse en el hilo de la interfaz (usa el dispositivo gráfico de la vista 3D).
        public static Bitmap ComposeStrip(List<(ShapeGeom geom, bool flip, string name)> cars)
        {
            if (cars == null || cars.Count == 0) return null;
            const int gap = 3, padX = 10, padY = 8, missingM = 4;
            float ppm = 11f;
            double meters = 0;
            foreach (var c in cars) meters += c.geom != null && !c.geom.IsEmpty ? Math.Max(0.3, c.geom.Max.Z - c.geom.Min.Z) * 1.02 : missingM;
            double natural = meters * ppm + gap * cars.Count + padX * 2;
            if (natural > MaxWidth) ppm = (float)Math.Max(4.0, ppm * (MaxWidth - padX * 2 - gap * cars.Count) / (meters * ppm));

            // Un tren suele repetir el mismo coche o vagón muchas veces: cada modelo (y sentido) se dibuja UNA vez
            // y se reutiliza (un mercancías de 12 tolvas iguales pasa de 13 dibujos a 2).
            var rendered = new Dictionary<(ShapeGeom, bool), Bitmap>();
            var slots = new List<(Bitmap bmp, string name, int w)>();
            foreach (var (geom, flip, name) in cars)
            {
                Bitmap bmp = null;
                if (geom != null && !geom.IsEmpty)
                {
                    if (!rendered.TryGetValue((geom, flip), out bmp))
                    {
                        try { bmp = TrimX(ShapeRenderer.RenderSide(geom, ppm, WorldH, flip ^ true)); } catch { bmp = null; }
                        rendered[(geom, flip)] = bmp;
                    }
                }
                slots.Add((bmp, name, bmp?.Width ?? (int)(missingM * ppm)));
            }
            if (slots.All(s => s.bmp == null)) return null;   // nada que enseñar

            int carH = (int)(WorldH * ppm);
            int width = padX * 2 + slots.Sum(s => s.w) + gap * (slots.Count - 1);
            int height = padY * 2 + carH + 4;
            var img = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(img))
            {
                g.Clear(Color.FromArgb(28, 31, 36));
                g.SmoothingMode = SmoothingMode.AntiAlias;
                int railY = padY + carH;
                using (var rail = new Pen(Color.FromArgb(96, 102, 110), 2f)) g.DrawLine(rail, 0, railY + 1, width, railY + 1);
                int x = padX;
                using var f = new Font("Segoe UI", 7.5f);
                foreach (var (bmp, name, w) in slots)
                {
                    if (bmp != null) g.DrawImage(bmp, x, padY, bmp.Width, bmp.Height);
                    else
                    {
                        var r = new Rectangle(x, padY + carH / 3, w, carH - carH / 3 - 2);
                        using (var b = new SolidBrush(Color.FromArgb(58, 62, 70))) g.FillRectangle(b, r);
                        using (var p = new Pen(Color.FromArgb(90, 96, 104))) g.DrawRectangle(p, r);
                        var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter };
                        using (var tb = new SolidBrush(Color.FromArgb(170, 176, 184))) g.DrawString(name ?? "?", f, tb, r, sf);
                    }
                    x += w + gap;
                }
            }
            foreach (var b in rendered.Values) b?.Dispose();
            return img;
        }

        // Recorta los lados hasta la caja del coche: lo que sobresale con poca altura (la rueda articulada de
        // los Talgo, topes, enganches) dejaba huecos grandes entre coches. Cuenta como caja la columna que
        // tiene al menos la cuarta parte de la altura de la columna más alta.
        internal static Bitmap TrimX(Bitmap bmp)
        {
            if (bmp == null || bmp.PixelFormat != PixelFormat.Format32bppArgb) return bmp;
            int w = bmp.Width, h = bmp.Height, left = w, right = -1;
            var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var px = new int[w * h];
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0, px, 0, px.Length);   // stride = w * 4 en 32 bpp
                var col = new int[w]; int max = 0;
                for (int x = 0; x < w; x++)
                {
                    int n = 0;
                    for (int y = 0; y < h; y++) if (((uint)px[y * w + x] >> 24) > 24) n++;
                    col[x] = n; if (n > max) max = n;
                }
                int min = Math.Max(1, max / 4);
                for (int x = 0; x < w; x++) if (col[x] >= min) { if (x < left) left = x; right = x; }
            }
            finally { bmp.UnlockBits(data); }
            if (right < 0 || (left <= 1 && right >= w - 2)) return bmp;   // vacío o nada que recortar
            var cut = bmp.Clone(new Rectangle(left, 0, right - left + 1, h), PixelFormat.Format32bppArgb);
            bmp.Dispose();
            return cut;
        }

        public static byte[] ToJpeg(Bitmap bmp, long quality = 82)
        {
            var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
            using var ep = new EncoderParameters(1);
            ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
            using var ms = new MemoryStream();
            bmp.Save(ms, codec, ep);
            return ms.ToArray();
        }

        public static void SaveLocal(string service, byte[] jpg)
        {
            try { File.WriteAllBytes(LocalFile(service), jpg); } catch { }
        }

        // ---------------- servidor ----------------
        static HttpRequestMessage Req(HttpMethod m, string path)
        {
            var r = new HttpRequestMessage(m, Supa.BuiltInUrl.TrimEnd('/') + path);
            r.Headers.TryAddWithoutValidation("apikey", Supa.BuiltInAnonKey);
            r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", string.IsNullOrEmpty(Supa.AccessToken) ? Supa.BuiltInAnonKey : Supa.AccessToken);
            return r;
        }

        // Sube (o sustituye) la imagen del servicio. null si va bien, o el error.
        public static async Task<string> UploadAsync(string company, string service, byte[] jpg)
        {
            if (!Ids(company, service) || jpg == null || jpg.Length == 0) return "datos no válidos";
            try
            {
                await Supa.EnsureFreshTokenAsync();
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
                using var req = Req(HttpMethod.Post, $"/storage/v1/object/{Bucket}/{ObjectPath(company, service)}");
                req.Headers.TryAddWithoutValidation("x-upsert", "true");
                req.Content = new ByteArrayContent(jpg);
                req.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
                using var resp = await http.SendAsync(req);
                return resp.IsSuccessStatusCode ? null : await resp.Content.ReadAsStringAsync();
            }
            catch (Exception e) { return e.Message; }
        }

        // Ruta local de la imagen (la descarga si no está en el caché), o null si no se puede.
        public static async Task<string> GetAsync(string company, string service)
        {
            if (!Ids(company, service)) return null;
            string file = LocalFile(service);
            if (File.Exists(file)) return file;
            try
            {
                await Supa.EnsureFreshTokenAsync();
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                using var req = Req(HttpMethod.Get, $"/storage/v1/object/{Bucket}/{ObjectPath(company, service)}");
                using var resp = await http.SendAsync(req);
                if (!resp.IsSuccessStatusCode) return null;
                var data = await resp.Content.ReadAsByteArrayAsync();
                if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8) return null;   // tiene que ser un JPEG
                File.WriteAllBytes(file, data);
                return file;
            }
            catch { return null; }
        }

        // Imagen en memoria (sin dejar el archivo bloqueado).
        public static Image Load(string file)
        {
            try
            {
                if (string.IsNullOrEmpty(file) || !File.Exists(file)) return null;
                using var ms = new MemoryStream(File.ReadAllBytes(file));
                using var tmp = Image.FromStream(ms);
                return new Bitmap(tmp);
            }
            catch { return null; }
        }
    }
}
