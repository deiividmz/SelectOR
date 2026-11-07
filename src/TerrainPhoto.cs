// (1.2.91, petición 2026-11-07) VISTA FOTORREALISTA DE LA RUTA, SACADA DE LA PROPIA RUTA: cada tesela del terreno (TILES\*.t)
// vista desde arriba con sus mismas texturas (TERRTEX: en la CGL, fotos aéreas pegadas parche a parche) y el relieve
// sombreado con sus alturas (_y.raw). Sin internet ni licencias de terceros: lo mismo que se ve en el 3D de Open Rails.
//  · Mapeo de la textura de cada parche, como Open Rails (TerrainPrimitive.GetVertexBuffer): en el vértice (xi, zi) del
//    parche (0..2R/s, zi hacia el sur), U = X + W·xi + B·zi y V = Y + C·xi + H·zi; el vértice está a −R + índice·s del
//    centro del parche (CenterX, CenterZ: metros desde la esquina noroeste de la tesela; x al este, z negativa al sur).
//  · Cada tesela se pinta una vez (en segundo plano) y se guarda en la caché (JPEG, %AppData%\…\SelectOR\terreno\<ruta>):
//    una grande (Px) y una pequeña (128 px) para ver la ruta entera.
//  · Las teselas: las que tienen archivo de mundo (WORLD\w±xxxxxx±zzzzzz.w) y su .t (nombre: TileName.FromTileXZ).

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Orts.Formats.Msts;

namespace SelectOR
{
    public static class TerrainPhoto
    {
        public const int Px = 1024, SmallPx = 128;
        const string Ver = "v2";
        static readonly Regex WorldRx = new Regex(@"^w([+-]\d{6})([+-]\d{6})\.w$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly ConcurrentDictionary<string, (int w, int h, int[] px)> _tex = new(StringComparer.OrdinalIgnoreCase);

        // las teselas de la ruta (con su .t)
        public static List<(int tx, int tz)> Tiles(string routeDir)
        {
            var l = new List<(int, int)>();
            try
            {
                foreach (var f in Directory.EnumerateFiles(Path.Combine(routeDir, "WORLD"), "*.w"))
                {
                    var m = WorldRx.Match(Path.GetFileName(f)); if (!m.Success) continue;
                    int tx = int.Parse(m.Groups[1].Value), tz = int.Parse(m.Groups[2].Value);
                    if (File.Exists(TileFile(routeDir, tx, tz))) l.Add((tx, tz));
                }
            }
            catch { }
            return l;
        }

        static string TileFile(string routeDir, int tx, int tz) => Path.Combine(routeDir, "TILES", TileName.FromTileXZ(tx, tz, TileName.Zoom.Small) + ".t");

        public static string CacheDir(string routeDir)
        {
            string name = Path.GetFileName(routeDir.TrimEnd('\\', '/'));
            using var sha = System.Security.Cryptography.SHA1.Create();
            string h = BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(routeDir).ToLowerInvariant())), 0, 4).Replace("-", "").ToLowerInvariant();
            return Path.Combine(AppDataTidy.CacheRoot, "terreno", name + "-" + h);
        }
        public static string CachedFile(string routeDir, int tx, int tz, int px) => Path.Combine(CacheDir(routeDir), $"{Ver}_{tx}_{tz}_{px}.jpg");

        // la imagen de una tesela (de la caché o pintándola); null si no se puede
        public static string Get(string routeDir, int tx, int tz, int px)
        {
            string file = CachedFile(routeDir, tx, tz, px);
            if (File.Exists(file)) return file;
            try
            {
                using var bmp = Render(routeDir, tx, tz, px);
                if (bmp == null) return null;
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                var enc = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
                using var ep = new EncoderParameters(1); ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 86L);
                string tmp = file + "." + Guid.NewGuid().ToString("N").Substring(0, 6) + ".tmp";
                bmp.Save(tmp, enc, ep);
                File.Move(tmp, file, true);
                return file;
            }
            catch { return null; }
        }

        // ---- (réplica satélite: ShapeRenderer.Sat.cs) la tesela con todo lo que tiene encima; se guarda a 2048, 512 y 128 px ----
        public const int SatPx = 2048, MidPx = 512;
        const string SatVer = "sat1";
        public static string SatFile(string routeDir, int tx, int tz, int px) => Path.Combine(CacheDir(routeDir), $"{SatVer}_{tx}_{tz}_{px}.jpg");

        // las teselas que ya tienen su réplica en la caché
        public static HashSet<(int, int)> SatOnDisk(string routeDir)
        {
            var set = new HashSet<(int, int)>();
            try
            {
                string d = CacheDir(routeDir);
                if (Directory.Exists(d))
                    foreach (var f in Directory.EnumerateFiles(d, SatVer + "_*_" + SatPx + ".jpg"))
                    {
                        var parts = Path.GetFileNameWithoutExtension(f).Split('_');
                        if (parts.Length == 4 && int.TryParse(parts[1], out int x) && int.TryParse(parts[2], out int z)) set.Add((x, z));
                    }
            }
            catch { }
            return set;
        }

        // guarda la réplica (de SatPx) en sus tres tamaños; la grande, la última (la que dice que está entera)
        public static bool SatSave(string routeDir, int tx, int tz, Bitmap big)
        {
            try
            {
                Directory.CreateDirectory(CacheDir(routeDir));
                foreach (int px in new[] { MidPx, SmallPx, SatPx })
                {
                    Bitmap b = big;
                    if (px != big.Width)
                    {
                        b = new Bitmap(px, px, PixelFormat.Format24bppRgb);
                        using var g = Graphics.FromImage(b);
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        using var ia = new ImageAttributes(); ia.SetWrapMode(WrapMode.TileFlipXY);
                        g.DrawImage(big, new Rectangle(0, 0, px, px), 0, 0, big.Width, big.Height, GraphicsUnit.Pixel, ia);
                    }
                    try { SaveJpeg(b, SatFile(routeDir, tx, tz, px), px == SatPx ? 84L : 88L); }
                    finally { if (!ReferenceEquals(b, big)) b.Dispose(); }
                }
                return true;
            }
            catch { return false; }
        }

        static void SaveJpeg(Bitmap bmp, string file, long q)
        {
            var enc = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
            using var ep = new EncoderParameters(1); ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, q);
            string tmp = file + "." + Guid.NewGuid().ToString("N").Substring(0, 6) + ".tmp";
            bmp.Save(tmp, enc, ep);
            File.Move(tmp, file, true);
        }

        // la tesela vista desde arriba: norte arriba, px × px para sus 2048 m
        public static Bitmap Render(string routeDir, int tx, int tz, int px)
        {
            string tfile = TileFile(routeDir, tx, tz);
            if (!File.Exists(tfile)) return null;
            var t = new TerrainFile(tfile).terrain;
            var smp = t.terrain_samples;
            int n = (int)smp.terrain_nsamples; float s = smp.terrain_sample_size, floorY = smp.terrain_sample_floor, scaleY = smp.terrain_sample_scale;
            float tileM = n * s, half = tileM / 2;
            var ps = t.terrain_patchsets[0];
            int np = ps.terrain_patchset_npatches;
            string terrtex = Path.Combine(routeDir, "TERRTEX");

            // el relieve: luz desde el noroeste, alta (1 en llano)
            float[] shade = null;
            try
            {
                string yfile = Path.Combine(routeDir, "TILES", Path.GetFileNameWithoutExtension(tfile) + "_y.raw");
                if (File.Exists(yfile))
                {
                    var alt = new TerrainAltitudeFile(yfile, n);
                    float H(int x, int z) => floorY + alt.GetElevation(Math.Max(0, Math.Min(n - 1, x)), Math.Max(0, Math.Min(n - 1, z))) * scaleY;
                    shade = new float[n * n];
                    double lx = -0.55, ly = 1.0, lz = -0.55, ll = Math.Sqrt(lx * lx + ly * ly + lz * lz); lx /= ll; ly /= ll; lz /= ll;
                    for (int z = 0; z < n; z++)
                        for (int x = 0; x < n; x++)
                        {
                            double dx = (H(x + 1, z) - H(x - 1, z)) / (2 * s), dz = (H(x, z + 1) - H(x, z - 1)) / (2 * s);   // (z: hacia el sur)
                            double nx = -dx, ny = 1, nz = -dz, nl = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                            double d = (nx * lx + ny * ly + nz * lz) / nl / ly;
                            shade[z * n + x] = (float)Math.Max(0.72, Math.Min(1.15, 0.6 + 0.4 * d));   // (suave: el relieve se intuye sin tapar la textura)
                        }
                }
            }
            catch { shade = null; }

            var outPx = new int[px * px];
            double mPerPx = tileM / px;
            for (int i = 0; i < ps.terrain_patchset_patches.Length; i++)
            {
                var p = ps.terrain_patchset_patches[i];
                var sh = t.terrain_shaders[Math.Max(0, Math.Min(t.terrain_shaders.Length - 1, p.ShaderIndex))];
                var tex = sh.terrain_texslots != null && sh.terrain_texslots.Length > 0 ? Tex(Path.Combine(terrtex, sh.terrain_texslots[0].Filename)) : default;
                float R = p.RadiusM, cx = p.CenterX - half, cz = p.CenterZ + half;   // (en el .t, desde la esquina noroeste: x 0…2048 hacia el este, z 0…−2048 hacia el sur)
                int ix0 = (int)Math.Floor((cx - R + half) / mPerPx), ix1 = (int)Math.Ceiling((cx + R + half) / mPerPx);
                int iy0 = (int)Math.Floor((half - (cz + R)) / mPerPx), iy1 = (int)Math.Ceiling((half - (cz - R)) / mPerPx);
                for (int iy = Math.Max(0, iy0); iy < Math.Min(px, iy1); iy++)
                {
                    double z = half - (iy + 0.5) * mPerPx;            // metros desde el centro de la tesela, hacia el norte
                    double zi = (cz + R - z) / s;                      // índice de vértice del parche (hacia el sur)
                    for (int ix = Math.Max(0, ix0); ix < Math.Min(px, ix1); ix++)
                    {
                        double x = (ix + 0.5) * mPerPx - half;
                        double xi = (x - (cx - R)) / s;
                        int c;
                        if (tex.px != null)
                        {
                            double u = p.X + p.W * xi + p.B * zi, v = p.Y + p.C * xi + p.H * zi;
                            u -= Math.Floor(u); v -= Math.Floor(v);
                            int tu = Math.Min(tex.w - 1, (int)(u * tex.w)), tv = Math.Min(tex.h - 1, (int)(v * tex.h));
                            c = tex.px[tv * tex.w + tu];
                        }
                        else c = unchecked((int)0xFF6E7A5A);
                        if (shade != null)
                        {
                            int gx = Math.Max(0, Math.Min(n - 1, (int)((x + half) / s))), gz = Math.Max(0, Math.Min(n - 1, (int)((half - z) / s)));
                            float f = shade[gz * n + gx];
                            int r = Math.Min(255, (int)(((c >> 16) & 255) * f)), g = Math.Min(255, (int)(((c >> 8) & 255) * f)), b = Math.Min(255, (int)((c & 255) * f));
                            c = unchecked((int)0xFF000000) | (r << 16) | (g << 8) | b;
                        }
                        outPx[iy * px + ix] = c | unchecked((int)0xFF000000);
                    }
                }
            }
            var bmp = new Bitmap(px, px, PixelFormat.Format32bppArgb);
            var bd = bmp.LockBits(new Rectangle(0, 0, px, px), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try { Marshal.Copy(outPx, 0, bd.Scan0, outPx.Length); } finally { bmp.UnlockBits(bd); }
            return bmp;
        }

        // una textura del terreno, como mucho de 256 px (de sobra para 64 px por parche)
        static (int w, int h, int[] px) Tex(string file)
        {
            if (_tex.TryGetValue(file, out var have)) return have;
            (int, int, int[]) res = default;
            try
            {
                Bitmap src = File.Exists(file) ? AceImage.Load(file) : null;
                if (src == null) { string dds = Path.ChangeExtension(file, ".dds"); if (File.Exists(dds)) src = AceImage.LoadDds(dds, out _); }
                if (src != null)
                    using (src)
                    {
                        int w = Math.Min(256, src.Width), h = Math.Min(256, src.Height);
                        using var b = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                        using (var g = Graphics.FromImage(b)) { g.InterpolationMode = InterpolationMode.HighQualityBilinear; g.PixelOffsetMode = PixelOffsetMode.HighQuality; g.DrawImage(src, new Rectangle(0, 0, w, h)); }
                        var px = new int[w * h];
                        var bd = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                        try { Marshal.Copy(bd.Scan0, px, 0, px.Length); } finally { b.UnlockBits(bd); }
                        res = (w, h, px);
                    }
            }
            catch { res = default; }
            if (_tex.Count > 600) _tex.Clear();
            _tex[file] = res;
            return res;
        }
    }
}
