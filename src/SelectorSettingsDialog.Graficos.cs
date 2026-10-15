// Ajustes de SelectOR → Gráficos: la mejora de imagen con ReShade (ReShadeFiles.cs): activarla, un preajuste y los
// deslizadores (nitidez, saturación, contraste, brillo, calidez, viñeta, suavizado de bordes), con una vista previa
// «antes / después» sobre una imagen de ejemplo de Open Rails (ReShade\ejemplo.jpg, siempre la misma). Se guarda al momento y vale desde la próxima vez que se
// conduzca; dentro de Open Rails se puede afinar con la tecla Inicio (menú de ReShade).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace SelectOR
{
    public sealed partial class SelectorSettingsDialog
    {
        ThemeCheck _chkGfx, _chkAA;
        readonly List<RoundButton> _presetBtns = new();
        readonly List<(GfxSlider s, Func<double> get, Action<double> set)> _sliders = new();
        PreviewBox _preview;
        Label _lblGfxWarn;
        Timer _previewTimer;
        bool _gfxLoading;

        Panel BuildGfxPage()
        {
            var page = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(Theme.Px(20), Theme.Px(12), Theme.Px(20), Theme.Px(8)), Visible = false };

            var top = new Panel { Dock = DockStyle.Top, Height = Theme.Px(40), BackColor = Theme.Bg };
            _chkGfx = new ThemeCheck { Text = I18n.T("Mejorar los gráficos de Open Rails con ReShade"), Checked = _prefs.GfxEnabled, AutoSize = false, Location = new Point(0, Theme.Px(6)), Font = Theme.Font(10f, FontStyle.Bold) };
            _chkGfx.Size = new Size(TextRenderer.MeasureText(_chkGfx.Text, _chkGfx.Font).Width + Theme.Px(40), Theme.Px(28));
            _chkGfx.CheckedChanged += (s, e) => { if (_gfxLoading) return; _prefs.GfxEnabled = _chkGfx.Checked; Save(); UpdateGfxState(); };
            var ver = new Label { Text = "ReShade " + ReShadeFiles.Version + " · " + I18n.T("incluido en SelectOR"), AutoSize = true, ForeColor = Theme.Subtle, BackColor = Theme.Bg, Font = Theme.Font(8.5f), Anchor = AnchorStyles.Top | AnchorStyles.Right };
            top.Controls.Add(_chkGfx); top.Controls.Add(ver);
            top.Layout += (s, e) => ver.Location = new Point(top.ClientSize.Width - ver.PreferredWidth, Theme.Px(12));

            var info = new Label
            {
                Text = I18n.T("Se pone junto a Open Rails solo mientras conduces desde SelectOR y se quita al volver. Vale desde la próxima vez que conduzcas. Dentro del juego: Inicio abre el menú de ReShade y Bloq Despl activa o quita los efectos."),
                Dock = DockStyle.Top, Height = Theme.Px(40), ForeColor = Theme.Subtle, BackColor = Theme.Bg, Font = Theme.Font(8.75f)
            };
            _lblGfxWarn = new Label { Dock = DockStyle.Top, Height = Theme.Px(24), ForeColor = Color.FromArgb(255, 183, 77), BackColor = Theme.Bg, Font = Theme.Font(9f, FontStyle.Bold), Visible = false,
                                      Text = I18n.T("Ya hay otro dxgi.dll junto a Open Rails (¿tu propio ReShade?): SelectOR no lo toca y no pondrá el suyo.") };

            // preajustes
            var presets = new FlowLayoutPanel { Dock = DockStyle.Top, Height = Theme.Px(46), BackColor = Theme.Bg, WrapContents = false, Padding = new Padding(0, Theme.Px(6), 0, 0) };
            foreach (var (id, label) in ReShadeFiles.Presets.Select(x => (x.id, x.label)).Append(("custom", "Personalizado")))
            {
                var b = DialogKit.Fit(DialogKit.Secondary(I18n.T(label)), 110); b.Height = Theme.Px(32); b.Tag = id; b.Margin = new Padding(0, 0, Theme.Px(8), 0);
                b.Click += (s, e) =>
                {
                    if ((string)b.Tag == "custom") { _prefs.GfxPreset = "custom"; }
                    else ReShadeFiles.ApplyPreset(_prefs, (string)b.Tag);
                    Save(); LoadGfx(); SchedulePreview();
                };
                _presetBtns.Add(b); presets.Controls.Add(b);
            }

            // deslizadores (izquierda) y vista previa (derecha)
            var cols = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Theme.Bg, ColumnCount = 2, RowCount = 1, Padding = new Padding(0, Theme.Px(8), 0, 0) };
            cols.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42)); cols.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
            var left = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Margin = new Padding(0, 0, Theme.Px(12), 0) };
            void Slider(string label, double min, double max, Func<double> get, Action<double> set, bool signed)
            {
                var s = new GfxSlider { Dock = DockStyle.Top, Height = Theme.Px(46), Label = I18n.T(label), Min = min, Max = max, Signed = signed };
                s.ValueChanged += () => { if (_gfxLoading) return; set(s.Value); _prefs.GfxPreset = "custom"; Save(); MarkPreset(); SchedulePreview(); };
                _sliders.Add((s, get, set));
                left.Controls.Add(s); s.BringToFront();
            }
            Slider("Nitidez", 0, 1, () => _prefs.GfxSharpen, v => _prefs.GfxSharpen = v, false);
            Slider("Viveza del color", -0.5, 0.5, () => _prefs.GfxVibrance, v => _prefs.GfxVibrance = v, true);
            Slider("Saturación", -0.5, 0.5, () => _prefs.GfxSaturation, v => _prefs.GfxSaturation = v, true);
            Slider("Contraste", -0.5, 0.5, () => _prefs.GfxContrast, v => _prefs.GfxContrast = v, true);
            Slider("Brillo", -0.5, 0.5, () => _prefs.GfxBrightness, v => _prefs.GfxBrightness = v, true);
            Slider("Calidez", -0.5, 0.5, () => _prefs.GfxWarmth, v => _prefs.GfxWarmth = v, true);
            Slider("Viñeta", 0, 1, () => _prefs.GfxVignette, v => _prefs.GfxVignette = v, false);
            _chkAA = new ThemeCheck { Text = I18n.T("Suavizado de bordes SMAA (dientes de sierra)"), Dock = DockStyle.Top, Height = Theme.Px(32), Font = Theme.Font(9.5f) };
            _chkAA.CheckedChanged += (s, e) => { if (_gfxLoading) return; _prefs.GfxEdgeAA = _chkAA.Checked; Save(); };
            left.Controls.Add(_chkAA); _chkAA.BringToFront();

            _preview = new PreviewBox { Dock = DockStyle.Fill, Margin = new Padding(Theme.Px(12), 0, 0, 0) };
            cols.Controls.Add(left, 0, 0); cols.Controls.Add(_preview, 1, 0);

            page.Controls.Add(cols); page.Controls.Add(presets); page.Controls.Add(_lblGfxWarn); page.Controls.Add(info); page.Controls.Add(top);

            _previewTimer = new Timer { Interval = 140 };
            _previewTimer.Tick += (s, e) => { _previewTimer.Stop(); RenderPreview(); };
            LoadPreviewImage();
            LoadGfx();
            return page;
        }

        void LoadGfx()
        {
            _gfxLoading = true;
            try
            {
                _chkGfx.Checked = _prefs.GfxEnabled; _chkAA.Checked = _prefs.GfxEdgeAA;
                foreach (var (s, get, _) in _sliders) s.Value = get();
                MarkPreset(); UpdateGfxState();
            }
            finally { _gfxLoading = false; }
        }

        void MarkPreset()
        {
            foreach (var b in _presetBtns)
            {
                bool on = (string)b.Tag == (_prefs.GfxPreset ?? "selector");
                b.BaseColor = on ? Theme.Accent : Theme.Surface2; b.HoverColor = on ? Theme.AccentHi : Theme.SurfaceHi; b.TextColor = on ? Color.White : Theme.Text;
                b.Invalidate();
            }
        }

        void UpdateGfxState()
        {
            _lblGfxWarn.Visible = ReShadeFiles.Foreign;
            foreach (var (s, _, _) in _sliders) s.Enabled = _prefs.GfxEnabled;
            foreach (var b in _presetBtns) b.Enabled = _prefs.GfxEnabled;
            _chkAA.Enabled = _prefs.GfxEnabled;
            _preview.Dim = !_prefs.GfxEnabled;
            _preview.Invalidate();
        }

        // La imagen de ejemplo (una vía electrificada con señales, catenaria y vegetación: se ven bien los bordes, la
        // nitidez y el color), siempre la misma para comparar.
        void LoadPreviewImage()
        {
            try
            {
                using var s = typeof(SelectorSettingsDialog).Assembly.GetManifestResourceStream("gfx.ejemplo.jpg");
                if (s == null) return;
                using var img = Image.FromStream(s);
                int w = 640, h = Math.Max(1, img.Height * w / Math.Max(1, img.Width));
                var small = new Bitmap(w, h);
                using (var g = Graphics.FromImage(small)) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.DrawImage(img, 0, 0, w, h); }
                _preview.Original = small;
                RenderPreview();
            }
            catch { }
        }

        // (las pruebas la usan con una imagen suya)
        public void SetPreviewImage(Bitmap b) { _preview.Original = b; _preview.Hint = null; RenderPreview(); }
        public Bitmap PreviewAfter => _preview.After;

        void SchedulePreview() { _previewTimer.Stop(); _previewTimer.Start(); }
        void RenderPreview()
        {
            if (_preview.Original == null) return;
            var old = _preview.After;
            _preview.After = ReShadeFiles.Preview(_preview.Original, _prefs);
            old?.Dispose();
            _preview.Invalidate();
        }

        // ---------------- controles ----------------
        public sealed class GfxSlider : Control
        {
            public string Label; public double Min, Max; public bool Signed;
            double _v; bool _drag;
            public event Action ValueChanged;
            public double Value { get => _v; set { double n = Math.Max(Min, Math.Min(Max, value)); if (Math.Abs(n - _v) < 1e-9) return; _v = n; Invalidate(); } }
            public GfxSlider() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); BackColor = Theme.Bg; Cursor = Cursors.Hand; }
            Rectangle Track => new Rectangle(Theme.Px(8), Height - Theme.Px(18), Width - Theme.Px(16), Theme.Px(6));
            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics; g.Clear(Theme.Bg); g.SmoothingMode = SmoothingMode.AntiAlias;
                using var f = Theme.Font(9.5f, FontStyle.Bold); using var fv = Theme.Font(9f);
                var col = Enabled ? Theme.Text : Theme.Subtle;
                TextRenderer.DrawText(g, Label, f, new Point(0, Theme.Px(2)), col);
                int pct = (int)Math.Round(Signed ? _v * 200 : _v * 100);
                string vt = Signed && pct > 0 ? "+" + pct : pct.ToString();
                var sz = TextRenderer.MeasureText(vt, fv);
                TextRenderer.DrawText(g, vt, fv, new Point(Width - sz.Width, Theme.Px(3)), Theme.Subtle);
                var t = Track;
                using (var p = Theme.Round(t, t.Height / 2)) using (var b = new SolidBrush(Theme.Surface2)) g.FillPath(b, p);
                double k = (Max - Min) > 0 ? (_v - Min) / (Max - Min) : 0;
                int x = t.X + (int)(t.Width * k), x0 = Signed ? t.X + t.Width / 2 : t.X;
                var fill = new Rectangle(Math.Min(x, x0), t.Y, Math.Max(2, Math.Abs(x - x0)), t.Height);
                using (var p = Theme.Round(fill, t.Height / 2)) using (var b = new SolidBrush(Enabled ? Theme.Accent : Theme.Surface2)) g.FillPath(b, p);
                int r = Theme.Px(8);
                using (var b = new SolidBrush(Enabled ? Color.White : Theme.Subtle)) g.FillEllipse(b, x - r, t.Y + t.Height / 2 - r, 2 * r, 2 * r);
            }
            void SetFrom(int px) { var t = Track; double k = Math.Max(0, Math.Min(1, (px - t.X) / (double)Math.Max(1, t.Width))); double v = Min + k * (Max - Min); v = Math.Round(v * 100) / 100; if (Math.Abs(v - _v) > 1e-9) { _v = v; Invalidate(); ValueChanged?.Invoke(); } }
            protected override void OnMouseDown(MouseEventArgs e) { if (!Enabled) return; _drag = true; SetFrom(e.X); base.OnMouseDown(e); }
            protected override void OnMouseMove(MouseEventArgs e) { if (_drag) SetFrom(e.X); base.OnMouseMove(e); }
            protected override void OnMouseUp(MouseEventArgs e) { _drag = false; base.OnMouseUp(e); }
            protected override void OnMouseWheel(MouseEventArgs e) { if (!Enabled) return; double v = Math.Round((_v + Math.Sign(e.Delta) * (Max - Min) / 50) * 100) / 100; v = Math.Max(Min, Math.Min(Max, v)); if (Math.Abs(v - _v) > 1e-9) { _v = v; Invalidate(); ValueChanged?.Invoke(); } base.OnMouseWheel(e); }
            protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
            public void Set(double v) { _v = Math.Max(Min, Math.Min(Max, v)); Invalidate(); ValueChanged?.Invoke(); }   // (pruebas)
        }

        // Antes (izquierda) / después (derecha), con la raya en medio.
        sealed class PreviewBox : Control
        {
            public Bitmap Original, After; public string Hint; public bool Dim;
            public PreviewBox() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); BackColor = Theme.Bg; }
            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics; g.Clear(Theme.Bg); g.SmoothingMode = SmoothingMode.AntiAlias;
                var box = new Rectangle(0, 0, Width - 1, Height - 1);
                using (var p = Theme.Round(box, 10)) using (var b = new SolidBrush(Theme.Surface)) g.FillPath(b, p);
                using var f = Theme.Font(9f); using var fb = Theme.Font(8.5f, FontStyle.Bold);
                if (Original == null)
                {
                    TextRenderer.DrawText(g, Hint ?? "", f, Rectangle.Inflate(box, -24, -24), Theme.Subtle, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
                    return;
                }
                // la imagen, encajada
                double k = Math.Min((box.Width - 16) / (double)Original.Width, (box.Height - 40) / (double)Original.Height);
                int w = (int)(Original.Width * k), h = (int)(Original.Height * k), x = box.X + (box.Width - w) / 2, y = box.Y + 8;
                var dst = new Rectangle(x, y, w, h);
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.DrawImage(Original, dst);
                if (After != null && !Dim)
                {
                    var half = new Rectangle(x + w / 2, y, w - w / 2, h);
                    g.DrawImage(After, half, new Rectangle(After.Width / 2, 0, After.Width - After.Width / 2, After.Height), GraphicsUnit.Pixel);
                    using var pen = new Pen(Color.White, 2); g.DrawLine(pen, x + w / 2, y, x + w / 2, y + h);
                }
                TextRenderer.DrawText(g, I18n.T("Antes"), fb, new Rectangle(x, y + h + 6, w / 2, 20), Theme.Subtle, TextFormatFlags.HorizontalCenter);
                TextRenderer.DrawText(g, Dim ? I18n.T("(ReShade desactivado)") : I18n.T("Después"), fb, new Rectangle(x + w / 2, y + h + 6, w - w / 2, 20), Dim ? Theme.Subtle : Theme.AccentHi, TextFormatFlags.HorizontalCenter);
            }
        }
    }
}
