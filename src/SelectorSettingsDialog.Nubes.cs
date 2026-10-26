// (1.2.77) Ajustes → Clima: varias texturas de NUBES para elegir. El administrador las sube con «Añadir nubes…» en
// «Gestionar el clima» (el nombre del archivo es el nombre que se ve) y se publican firmadas con el resto del clima
// (ClimaFiles: Manifest.clouds). Cada usuario elige la suya en la tira de miniaturas, debajo de los grupos; al conducir se
// pone esa en vez del Clouds01.png del clima (AppPrefs.ClimaClouds; «De serie» = la del clima). Cada textura se descarga
// al verla en la tira (pesan varios MB).

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
    public sealed partial class SelectorSettingsDialog
    {
        List<ClimaFiles.Cloud> _climaDraftClouds = new();   // las nubes que va a publicar el administrador
        ClimaCloudPicker _climaClouds;
        readonly HashSet<string> _cloudsLoading = new();

        // Las nubes que se ven: las del borrador del administrador o las publicadas.
        List<ClimaFiles.Cloud> ClimaCloudsShown() => Supa.IsSuperadmin ? _climaDraftClouds : _climaPub?.clouds ?? new List<ClimaFiles.Cloud>();

        // Lo que se ve en la vista previa: con la textura de nubes elegida (si está descargada)
        List<ClimaFiles.Item> ClimaShownEffective()
        {
            var list = ClimaShown().ToList();
            var pick = _prefs.ClimaClouds == null ? null : ClimaCloudsShown().FirstOrDefault(c => c.sha == _prefs.ClimaClouds);
            if (pick != null && ClimaFiles.IsCached(ClimaFiles.AsItem(pick)))
            {
                list.RemoveAll(f => string.Equals(f.name, ClimaFiles.CloudsFile, StringComparison.OrdinalIgnoreCase));
                list.Add(ClimaFiles.AsItem(pick));
            }
            return list;
        }

        bool ClimaCloudsDirty()
        {
            var a = (_climaPub?.clouds ?? new List<ClimaFiles.Cloud>()).Select(c => c.label + ":" + c.sha);
            var b = _climaDraftClouds.Select(c => c.label + ":" + c.sha);
            return !a.SequenceEqual(b);
        }

        Control BuildClimaCloudPicker()
        {
            _climaClouds = new ClimaCloudPicker { Dock = DockStyle.Top, Height = Theme.Px(150), Visible = false };
            _climaClouds.Picked += async sha =>
            {
                _prefs.ClimaClouds = sha; Save();
                await ClimaCloudEnsure(sha);
                ClimaCloudRefresh();
                _climaPreview.ShowGroup("clouds", ClimaShownEffective());
            };
            _climaClouds.NeedImage += sha => { _ = ClimaCloudEnsure(sha); };
            return _climaClouds;
        }

        // la textura, a la caché (si no está ya); luego se repinta la tira
        async Task ClimaCloudEnsure(string sha)
        {
            if (sha == null || _cloudsLoading.Contains(sha)) return;
            var c = ClimaCloudsShown().FirstOrDefault(x => x.sha == sha); if (c == null) return;
            var it = ClimaFiles.AsItem(c); if (ClimaFiles.IsCached(it)) return;
            _cloudsLoading.Add(sha);
            try
            {
                var err = await ClimaFiles.DownloadAsync(it);
                if (IsDisposed) return;
                if (err != null) { _lblClimaStatus.ForeColor = Color.FromArgb(229, 115, 115); _lblClimaStatus.Text = err; }
            }
            finally { _cloudsLoading.Remove(sha); }
            if (!IsDisposed) { ClimaCloudRefresh(); if (_climaPreview.Group == "clouds") _climaPreview.ShowGroup("clouds", ClimaShownEffective()); }
        }

        void ClimaCloudRefresh()
        {
            if (_climaClouds == null) return;
            var cs = ClimaCloudsShown();
            var def = ClimaShown().FirstOrDefault(f => string.Equals(f.name, ClimaFiles.CloudsFile, StringComparison.OrdinalIgnoreCase));
            var tiles = new List<ClimaCloudPicker.Tile>();
            if (def != null) tiles.Add(new ClimaCloudPicker.Tile { Sha = null, Label = I18n.T("De serie"), Path = ClimaFiles.CachedPath(def) });
            foreach (var c in cs) tiles.Add(new ClimaCloudPicker.Tile { Sha = c.sha, Label = c.label, Path = ClimaFiles.CachedPath(ClimaFiles.AsItem(c)), Loading = _cloudsLoading.Contains(c.sha) });
            // (la elegida que ya no está en el clima: de serie)
            string sel = _prefs.ClimaClouds != null && cs.Any(c => c.sha == _prefs.ClimaClouds) ? _prefs.ClimaClouds : null;
            _climaClouds.Set(tiles, sel, _prefs.ClimaEnabled && _prefs.ClimaGroups?.Contains("clouds") == true);
            _climaClouds.Visible = cs.Count > 0;
        }

        // Administrador: añadir texturas de nubes (PNG; el nombre del archivo es el nombre que se ve)
        async void ClimaAddClouds()
        {
            if (_climaBusy || !Supa.IsSuperadmin) return;
            using var od = new OpenFileDialog { Multiselect = true, Filter = I18n.T("Nubes") + " (*.png)|*.png", Title = I18n.T("Texturas de nubes (el nombre del archivo es el nombre que verán los usuarios)") };
            if (od.ShowDialog(this) != DialogResult.OK) return;
            await ClimaAddCloudPaths(od.FileNames.ToList());
        }

        public async Task ClimaAddCloudPaths(List<string> paths)
        {
            if (_climaBusy || !Supa.IsSuperadmin) return;
            _climaBusy = true;
            int added = 0; var bad = new List<string>();
            try
            {
                foreach (var p in paths)
                {
                    string label = Path.GetFileNameWithoutExtension(p).Replace('_', ' ').Trim();
                    if (label.Length > 40) label = label.Substring(0, 40).Trim();
                    if (label.Length == 0) label = I18n.T("Nubes");
                    _lblClimaAdmin.Text = string.Format(I18n.T("Subiendo {0}…"), Path.GetFileName(p));
                    byte[] data;
                    try { data = await File.ReadAllBytesAsync(p); } catch { bad.Add(Path.GetFileName(p)); continue; }
                    var (item, err) = await ClimaFiles.UploadAsync(ClimaFiles.CloudsFile, data);
                    if (err != null) { bad.Add(Path.GetFileName(p) + " (" + err + ")"); continue; }
                    _climaDraftClouds.RemoveAll(c => c.sha == item.sha);
                    _climaDraftClouds.Add(new ClimaFiles.Cloud { label = label, sha = item.sha, size = item.size });
                    added++;
                }
            }
            finally { _climaBusy = false; }
            ClimaRefresh();
            var msg = string.Format(I18n.T("{0} textura(s) de nubes añadida(s)."), added);
            if (bad.Count > 0) msg += " " + I18n.T("No valen:") + " " + string.Join(", ", bad.Take(4)) + (bad.Count > 4 ? "…" : "");
            _lblClimaAdmin.Text = msg;
        }

        // ---------------------------------------------------------------- la tira de miniaturas
        sealed class ClimaCloudPicker : Control
        {
            public sealed class Tile { public string Sha, Label, Path; public bool Loading; }
            List<Tile> _tiles = new();
            string _sel; bool _on;
            int _hover = -1, _scroll;
            readonly Dictionary<string, Bitmap> _thumbs = new();
            public event Action<string> Picked, NeedImage;
            public string Selected => _sel;
            public IReadOnlyList<Tile> Tiles => _tiles;   // (pruebas)
            public ClimaCloudPicker() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); BackColor = Theme.Bg; }
            public void Set(List<Tile> tiles, string sel, bool on) { _tiles = tiles; _sel = sel; _on = on; Invalidate(); }
            int TileW => Theme.Px(112);
            Rectangle TileRect(int i) => new Rectangle(i * (TileW + Theme.Px(8)) - _scroll, Theme.Px(26), TileW, Height - Theme.Px(30));
            Bitmap Thumb(string path)
            {
                if (path == null) return null;
                if (_thumbs.TryGetValue(path, out var b)) return b;
                try
                {
                    using var img = Image.FromStream(new MemoryStream(File.ReadAllBytes(path)));
                    double k = Math.Min(1, 200.0 / Math.Max(img.Width, img.Height));
                    var bmp = new Bitmap(Math.Max(1, (int)(img.Width * k)), Math.Max(1, (int)(img.Height * k)));
                    using (var g = Graphics.FromImage(bmp)) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.DrawImage(img, 0, 0, bmp.Width, bmp.Height); }
                    return _thumbs[path] = bmp;
                }
                catch { return _thumbs[path] = null; }
            }
            protected override void Dispose(bool disposing) { if (disposing) foreach (var b in _thumbs.Values) b?.Dispose(); base.Dispose(disposing); }
            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics; g.Clear(Theme.Bg); g.SmoothingMode = SmoothingMode.AntiAlias;
                using var fh = Theme.Font(8.5f, FontStyle.Bold); using var f = Theme.Font(8.5f);
                TextRenderer.DrawText(g, I18n.T("TEXTURA DE LAS NUBES") + (_on ? "" : "  ·  " + I18n.T("(con el grupo Nubes activado)")), fh, new Rectangle(0, 0, Width, Theme.Px(22)), Theme.Subtle, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                for (int i = 0; i < _tiles.Count; i++)
                {
                    var r = TileRect(i); if (r.Right < 0 || r.Left > Width) continue;
                    var t = _tiles[i]; bool sel = t.Sha == _sel;
                    using (var p = Theme.Round(r, 8)) using (var b = new SolidBrush(sel ? Color.FromArgb(40, 60, 46) : i == _hover ? Theme.Surface2 : Theme.Surface)) g.FillPath(b, p);
                    if (sel) Theme.DrawRoundBorder(g, Rectangle.Inflate(r, -1, -1), 8, Theme.Accent, 1.6f);
                    var ir = new Rectangle(r.X + Theme.Px(6), r.Y + Theme.Px(6), r.Width - Theme.Px(12), r.Height - Theme.Px(30));
                    // tablero de fondo (las nubes llevan transparencia)
                    int c = Theme.Px(8);
                    using (var b1 = new SolidBrush(Color.FromArgb(42, 46, 50))) using (var b2 = new SolidBrush(Color.FromArgb(58, 62, 66)))
                        for (int y = ir.Y; y < ir.Bottom; y += c) for (int x = ir.X; x < ir.Right; x += c)
                            g.FillRectangle(((x - ir.X) / c + (y - ir.Y) / c) % 2 == 0 ? b1 : b2, x, y, Math.Min(c, ir.Right - x), Math.Min(c, ir.Bottom - y));
                    var img = Thumb(t.Path);
                    if (img != null)
                    {
                        double k = Math.Min(ir.Width / (double)img.Width, ir.Height / (double)img.Height);
                        int w = Math.Max(1, (int)(img.Width * k)), h = Math.Max(1, (int)(img.Height * k));
                        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                        g.DrawImage(img, ir.X + (ir.Width - w) / 2, ir.Y + (ir.Height - h) / 2, w, h);
                    }
                    else
                    {
                        TextRenderer.DrawText(g, t.Loading ? I18n.T("descargando…") : "☁", f, ir, Theme.Subtle, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                        if (!t.Loading && t.Sha != null) NeedImage?.Invoke(t.Sha);
                    }
                    TextRenderer.DrawText(g, t.Label, sel ? fh : f, new Rectangle(r.X + 4, ir.Bottom + 2, r.Width - 8, Theme.Px(22)), sel ? Theme.AccentHi : Theme.Text,
                                          TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                }
            }
            int At(Point p) { for (int i = 0; i < _tiles.Count; i++) if (TileRect(i).Contains(p)) return i; return -1; }
            protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); int h = At(e.Location); Cursor = h >= 0 ? Cursors.Hand : Cursors.Default; if (h != _hover) { _hover = h; Invalidate(); } }
            protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = -1; Invalidate(); }
            protected override void OnMouseWheel(MouseEventArgs e)
            {
                base.OnMouseWheel(e);
                int max = Math.Max(0, _tiles.Count * (TileW + Theme.Px(8)) - Width);
                _scroll = Math.Max(0, Math.Min(max, _scroll - Math.Sign(e.Delta) * (TileW + Theme.Px(8)))); Invalidate();
            }
            protected override void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e);
                int i = At(e.Location); if (i < 0) return;
                _sel = _tiles[i].Sha; Invalidate(); Picked?.Invoke(_sel);
            }
            public void Pick(string sha) { _sel = sha; Invalidate(); Picked?.Invoke(sha); }   // (pruebas)
        }
    }
}
