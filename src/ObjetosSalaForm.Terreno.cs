// (1.2.91, petición 2026-11-07) Objetos de la sala · VISTA SATÉLITE DE LA RUTA bajo las vías: cada tesela vista desde arriba
// como en el 3D, con todo lo que tiene encima (ShapeRenderer.Sat.cs: terreno, vías, edificios, andenes, árboles…). Casilla
// «Vista satélite de la ruta» (de serie).
//  · De lejos (la ruta entera): la imagen pequeña (128 px) de la réplica si ya está hecha; si no, la del terreno solo
//    (TerrainPhoto.Render, que sale al momento). De cerca: la réplica a 512 o 2048 px (1 m por píxel).
//  · La réplica se hace una vez por tesela (la primera vez, de 1 a pocos segundos) y se guarda en la caché de disco.
//    Primero las teselas del centro de la vista; las que ya no se ven se dejan de pedir.
//  · Cada tesela se pone por sus cuatro esquinas en lat/lon (OrGeo), como un paralelogramo: casa con la vía del mapa.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SelectOR
{
    public sealed partial class ObjetosSalaForm
    {
        static bool _photo = true;
        List<(int tx, int tz)> _tiles;
        HashSet<(int, int)> _satDone;                                                   // teselas con la réplica en la caché
        readonly Dictionary<(int, int, int, bool), Image> _tileImg = new();             // (tx, tz, px, réplica)
        readonly LinkedList<(int, int, int, bool)> _bigOrder = new();                   // las de 512 y 2048 px, por uso
        readonly Dictionary<(int, int, int, bool), double> _pending = new();            // pedidas, con su prioridad (distancia al centro)
        readonly HashSet<(int, int, int, bool)> _tileAsked = new();
        bool _tileWorking;

        ThemeCheck BuildPhotoCheck()
        {
            var chk = new ThemeCheck { Text = Tr("Vista satélite de la ruta"), Checked = _photo, Dock = DockStyle.Top, Height = Theme.Px(30), ForeColor = Theme.Text, BackColor = Theme.BgSidebar };
            chk.CheckedChanged += (s, e) => { _photo = chk.Checked; ApplyPhoto(); };
            new ToolTip().SetToolTip(chk, Tr("La ruta vista desde arriba, como en el 3D: el terreno con su relieve, las vías, los edificios, los andenes y los árboles. La primera vez tarda un poco en prepararse."));
            return chk;
        }

        void ApplyPhoto()
        {
            if (_photo && _tiles == null)
            {
                var dir = _routeDir;
                Task.Run(() => (TerrainPhoto.Tiles(dir), TerrainPhoto.SatOnDisk(dir))).ContinueWith(t =>
                {
                    try { BeginInvoke((Action)(() => { _tiles = t.Result.Item1; _satDone = t.Result.Item2; _map.Invalidate(); })); } catch { }
                });
            }
            _map.Underlay = _photo ? DrawTerrain : null;
            _map.RedrawLayer();
        }

        void DrawTerrain(Graphics g, Func<double, double, PointF> scr)
        {
            var tiles = _tiles; if (tiles == null) return;
            var view = RectangleF.Inflate(_map.ClientRectangle, 40, 40);
            var mid = new PointF(_map.ClientSize.Width / 2f, _map.ClientSize.Height / 2f);
            var seen = new HashSet<(int, int, int, bool)>();
            var st = g.Save();
            g.InterpolationMode = InterpolationMode.Bilinear; g.PixelOffsetMode = PixelOffsetMode.Half;
            using var ia = new System.Drawing.Imaging.ImageAttributes(); ia.SetWrapMode(WrapMode.TileFlipXY);   // (el borde, sin fundirse a transparente: sin rayas entre teselas)
            foreach (var (tx, tz) in tiles)
            {
                if (!Corner(tx * 2048.0 - 1024, tz * 2048.0 + 1024, scr, out var nw) || !Corner(tx * 2048.0 + 1024, tz * 2048.0 + 1024, scr, out var ne)
                    || !Corner(tx * 2048.0 - 1024, tz * 2048.0 - 1024, scr, out var sw)) continue;
                var se = new PointF(ne.X + sw.X - nw.X, ne.Y + sw.Y - nw.Y);
                float minX = Math.Min(Math.Min(nw.X, ne.X), Math.Min(sw.X, se.X)), maxX = Math.Max(Math.Max(nw.X, ne.X), Math.Max(sw.X, se.X));
                float minY = Math.Min(Math.Min(nw.Y, ne.Y), Math.Min(sw.Y, se.Y)), maxY = Math.Max(Math.Max(nw.Y, ne.Y), Math.Max(sw.Y, se.Y));
                if (!view.IntersectsWith(RectangleF.FromLTRB(minX, minY, maxX, maxY))) continue;
                float sizePx = (float)Math.Sqrt((ne.X - nw.X) * (ne.X - nw.X) + (ne.Y - nw.Y) * (ne.Y - nw.Y));
                double prio = Math.Sqrt(Math.Pow((minX + maxX) / 2 - mid.X, 2) + Math.Pow((minY + maxY) / 2 - mid.Y, 2));
                Image img;
                if (sizePx > 180)
                {
                    int want = sizePx > 700 ? TerrainPhoto.SatPx : TerrainPhoto.MidPx;
                    img = Img(tx, tz, want, true, prio, seen)
                        ?? Have(tx, tz, want == TerrainPhoto.SatPx ? TerrainPhoto.MidPx : TerrainPhoto.SatPx, true)
                        ?? Have(tx, tz, TerrainPhoto.SmallPx, true)
                        ?? Img(tx, tz, TerrainPhoto.SmallPx, false, prio - 1e6, seen);   // (mientras tanto, el terreno solo)
                }
                else if (_satDone != null && _satDone.Contains((tx, tz)))
                    img = Img(tx, tz, TerrainPhoto.SmallPx, true, prio, seen) ?? Have(tx, tz, TerrainPhoto.SmallPx, false);
                else
                    img = Img(tx, tz, TerrainPhoto.SmallPx, false, prio, seen);
                if (img == null) continue;
                // un píxel más por cada lado, para que se solapen
                float lx = (float)Math.Max(1, sizePx), ly = (float)Math.Max(1, Math.Sqrt((sw.X - nw.X) * (sw.X - nw.X) + (sw.Y - nw.Y) * (sw.Y - nw.Y)));
                float ux = (ne.X - nw.X) / lx * 0.8f, uy = (ne.Y - nw.Y) / lx * 0.8f, vx = (sw.X - nw.X) / ly * 0.8f, vy = (sw.Y - nw.Y) / ly * 0.8f;
                var pts = new[] { new PointF(nw.X - ux - vx, nw.Y - uy - vy), new PointF(ne.X + ux - vx, ne.Y + uy - vy), new PointF(sw.X - ux + vx, sw.Y - uy + vy) };
                try { g.DrawImage(img, pts, new RectangleF(0, 0, img.Width, img.Height), GraphicsUnit.Pixel, ia); } catch { }
            }
            g.Restore(st);
            // lo pedido que ya no se ve, fuera de la cola
            foreach (var k in _pending.Keys.Where(k => !seen.Contains(k)).ToList()) { _pending.Remove(k); _tileAsked.Remove(k); }
        }

        static bool Corner(double wx, double wz, Func<double, double, PointF> scr, out PointF p)
        {
            p = PointF.Empty;
            if (!OrGeo.TryLatLon(wx, wz, out double la, out double lo)) return false;
            p = scr(la, lo); return true;
        }

        Image Have(int tx, int tz, int px, bool sat)
        {
            var k = (tx, tz, px, sat);
            if (!_tileImg.TryGetValue(k, out var im)) return null;
            if (px != TerrainPhoto.SmallPx) { _bigOrder.Remove(k); _bigOrder.AddFirst(k); }
            return im;
        }

        // la imagen, si ya está; si no, se pide (y se devuelve null)
        Image Img(int tx, int tz, int px, bool sat, double prio, HashSet<(int, int, int, bool)> seen)
        {
            var im = Have(tx, tz, px, sat);
            if (im != null) return im;
            var k = (tx, tz, px, sat);
            seen.Add(k);
            if (_pending.ContainsKey(k)) _pending[k] = prio;
            else if (_tileAsked.Add(k)) { _pending[k] = prio; if (!_tileWorking) _ = Work(); }
            return null;
        }

        async Task Work()
        {
            _tileWorking = true;
            try
            {
                string dir = _routeDir;
                while (_pending.Count > 0)
                {
                    if (IsDisposed) return;
                    var k = _pending.OrderBy(kv => kv.Value).First().Key;
                    _pending.Remove(k);
                    var (tx, tz, px, sat) = k;
                    string f;
                    if (!sat) f = await Task.Run(() => TerrainPhoto.Get(dir, tx, tz, px));
                    else
                    {
                        f = TerrainPhoto.SatFile(dir, tx, tz, px);
                        if (_satDone == null || !_satDone.Contains((tx, tz)) || !File.Exists(f))
                        {
                            f = null;
                            var scene = await Task.Run(() => { try { return ShapeRenderer.SatPrepare(dir, tx, tz); } catch { return null; } });
                            if (IsDisposed) return;
                            var big = ShapeRenderer.SatDraw(scene, TerrainPhoto.SatPx);   // (la tarjeta, en este hilo)
                            if (big != null)
                            {
                                bool ok = await Task.Run(() => { using (big) return TerrainPhoto.SatSave(dir, tx, tz, big); });
                                if (ok) { _satDone ??= new HashSet<(int, int)>(); _satDone.Add((tx, tz)); f = TerrainPhoto.SatFile(dir, tx, tz, px); }
                            }
                        }
                    }
                    Image img = f == null ? null : await Task.Run(() =>
                    {
                        try { using var ms = new MemoryStream(File.ReadAllBytes(f)); return (Image)new Bitmap(Image.FromStream(ms)); } catch { return null; }
                    });
                    if (IsDisposed) { img?.Dispose(); return; }
                    if (img == null) continue;   // (sin .t o sin texturas: se queda sin pedir otra vez)
                    _tileImg[k] = img;
                    if (px != TerrainPhoto.SmallPx)
                    {
                        _bigOrder.AddFirst(k);
                        // en memoria, como mucho 8 de 2048 px y 40 de 512
                        foreach (int size in new[] { TerrainPhoto.SatPx, TerrainPhoto.MidPx })
                        {
                            int max = size == TerrainPhoto.SatPx ? 8 : 40, n = 0;
                            for (var node = _bigOrder.First; node != null;)
                            {
                                var next = node.Next;
                                if (node.Value.Item3 == size && ++n > max)
                                {
                                    var old = node.Value; _bigOrder.Remove(node);
                                    if (_tileImg.Remove(old, out var oi)) oi.Dispose();
                                    _tileAsked.Remove(old);
                                }
                                node = next;
                            }
                        }
                    }
                    _map.Invalidate();
                }
            }
            finally { _tileWorking = false; }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            foreach (var im in _tileImg.Values) im.Dispose();
            _tileImg.Clear();
            base.OnFormClosed(e);
        }
    }
}
