// (2026-11-09) Los trenes muy detallados tardaban en salir (vista 3D de Conducción libre, Compra y Flota, y sus vistas 2D):
// leer un .s de 190 MB con el lector de Open Rails son 2-3 s CADA VEZ que se elegía, y sus texturas de 2048×2048, casi 1 s.
//  · La geometría ya leída se guarda: en memoria (las últimas, hasta unos 2 millones de triángulos) y, la de los modelos que
//    tardan en leerse (más de 0,4 s), también en disco (SelectOR_previews\geo_*.bin, como mucho 600 MB: se borran las más
//    viejas). Se vuelve a leer si cambia el .eng o el .s.
//  · Las versiones reducidas de cada textura (mipmaps) se hacen con medias de 2×2 sobre los píxeles, no con GDI+.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace SelectOR
{
    public static partial class ShapeRenderer
    {
        const int GeoMemTris = 2_000_000, GeoMemMax = 8;
        const long GeoDiskMax = 600L * 1024 * 1024;
        const long GeoDiskMinMs = 400;
        static readonly object _geoLock = new object();
        static readonly LinkedList<(string key, ShapeGeom geom, int tris)> _geoMem = new();

        public static ShapeGeom BuildGeometry(string engPath)
        {
            string key = GeoKey(engPath);
            if (key == null) return BuildGeometryCore(engPath);
            lock (_geoLock)
            {
                for (var n = _geoMem.First; n != null; n = n.Next)
                    if (n.Value.key == key) { _geoMem.Remove(n); _geoMem.AddFirst(n); return n.Value.geom; }
            }
            var g = LoadGeo(key);
            if (g == null)
            {
                var sw = Stopwatch.StartNew();
                g = BuildGeometryCore(engPath);
                if (g != null && sw.ElapsedMilliseconds >= GeoDiskMinMs) SaveGeo(key, g);
            }
            if (g != null) Remember(key, g);
            return g;
        }

        static void Remember(string key, ShapeGeom g)
        {
            int tris = g.Groups.Sum(x => x.Indices.Length / 3);
            lock (_geoLock)
            {
                _geoMem.AddFirst((key, g, tris));
                int total = _geoMem.Sum(x => x.tris);
                while (_geoMem.Count > 1 && (_geoMem.Count > GeoMemMax || total > GeoMemTris)) { total -= _geoMem.Last.Value.tris; _geoMem.RemoveLast(); }
            }
        }

        // el .eng y el .s (y su fecha y tamaño): si cambia algo, se vuelve a leer
        static string GeoKey(string engPath)
        {
            try
            {
                string s = ShapePathFor(engPath);
                if (s == null) return null;
                var fe = new FileInfo(engPath); var fs = new FileInfo(s);
                return "g1|" + engPath.ToLowerInvariant() + "|" + fe.LastWriteTimeUtc.Ticks + "|" + s.ToLowerInvariant() + "|" + fs.LastWriteTimeUtc.Ticks + "|" + fs.Length;
            }
            catch { return null; }
        }

        static string GeoFile(string key)
        {
            uint h = 2166136261; foreach (char c in key) h = (h ^ c) * 16777619;
            uint h2 = 5381; foreach (char c in key) h2 = h2 * 33 + c;
            return Path.Combine(PreviewCacheDir, "geo_" + h.ToString("x8") + h2.ToString("x8") + ".bin");
        }

        static void SaveGeo(string key, ShapeGeom g)
        {
            try
            {
                Directory.CreateDirectory(PreviewCacheDir);
                string f = GeoFile(key), tmp = f + ".tmp";
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
                using (var z = new DeflateStream(fs, CompressionLevel.Fastest))
                using (var w = new BinaryWriter(z))
                {
                    w.Write(key);
                    W(w, g.Min); W(w, g.Max);
                    w.Write(g.Groups.Count);
                    foreach (var gr in g.Groups)
                    {
                        w.Write(gr.TexPath ?? ""); w.Write(gr.Shader); w.Write(gr.AlphaTest);
                        w.Write(gr.Verts.Length); w.Write(MemoryMarshal.AsBytes(gr.Verts.AsSpan()));
                        w.Write(gr.Indices.Length); w.Write(MemoryMarshal.AsBytes(gr.Indices.AsSpan()));
                    }
                }
                File.Move(tmp, f, true);
                TrimGeoDisk();
            }
            catch { }
        }

        static ShapeGeom LoadGeo(string key)
        {
            try
            {
                string f = GeoFile(key);
                if (!File.Exists(f)) return null;
                using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
                using var z = new DeflateStream(fs, CompressionMode.Decompress);
                using var r = new BinaryReader(z);
                if (r.ReadString() != key) return null;
                var g = new ShapeGeom { Min = V(r), Max = V(r) };
                int n = r.ReadInt32();
                if (n < 0 || n > 10000) return null;
                for (int i = 0; i < n; i++)
                {
                    var gr = new ShapeGroup { TexPath = r.ReadString(), Shader = r.ReadInt32(), AlphaTest = r.ReadBoolean() };
                    int nv = r.ReadInt32(); if (nv < 0 || nv > 50_000_000) return null;
                    gr.Verts = new VertexPositionNormalTexture[nv]; Fill(r, MemoryMarshal.AsBytes(gr.Verts.AsSpan()));
                    int ni = r.ReadInt32(); if (ni < 0 || ni > 150_000_000) return null;
                    gr.Indices = new int[ni]; Fill(r, MemoryMarshal.AsBytes(gr.Indices.AsSpan()));

                    g.Groups.Add(gr);
                }
                try { File.SetLastAccessTimeUtc(f, DateTime.UtcNow); File.SetLastWriteTimeUtc(f, DateTime.UtcNow); } catch { }
                return g.IsEmpty ? null : g;
            }
            catch { return null; }
        }

        static void Fill(BinaryReader r, Span<byte> dst)
        {
            while (dst.Length > 0)
            {
                int k = r.Read(dst);
                if (k <= 0) throw new EndOfStreamException();
                dst = dst.Slice(k);
            }
        }

        static void W(BinaryWriter w, Vector3 v) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }
        static Vector3 V(BinaryReader r) => new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

        static void TrimGeoDisk()
        {
            try
            {
                var files = new DirectoryInfo(PreviewCacheDir).GetFiles("geo_*.bin").OrderByDescending(x => x.LastWriteTimeUtc).ToList();
                long sum = 0;
                foreach (var x in files) { sum += x.Length; if (sum > GeoDiskMax) try { x.Delete(); } catch { } }
            }
            catch { }
        }

        // ---- mipmaps rápidos: media de 2×2 (con el último píxel repetido si el lado es impar) ----
        static Color[] Half(Color[] src, int w, int h, int nw, int nh)
        {
            var dst = new Color[nw * nh];
            for (int y = 0; y < nh; y++)
            {
                int y0 = Math.Min(h - 1, y * 2), y1 = Math.Min(h - 1, y * 2 + 1);
                for (int x = 0; x < nw; x++)
                {
                    int x0 = Math.Min(w - 1, x * 2), x1 = Math.Min(w - 1, x * 2 + 1);
                    Color a = src[y0 * w + x0], b = src[y0 * w + x1], c = src[y1 * w + x0], d = src[y1 * w + x1];
                    dst[y * nw + x] = new Color((a.R + b.R + c.R + d.R + 2) >> 2, (a.G + b.G + c.G + d.G + 2) >> 2,
                                                (a.B + b.B + c.B + d.B + 2) >> 2, (a.A + b.A + c.A + d.A + 2) >> 2);
                }
            }
            return dst;
        }
    }
}
