// Ventanas propias de SelectOR en lugar de los cuadros de Windows: sin marco, esquinas redondeadas,
// sombra, franja de color arriba, aparecen con un fundido, se arrastran desde cualquier zona vacía y se
// cierran con Esc (si en ese momento se puede). La usan el aviso de la Liga y el de actualizaciones.

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SelectOR
{
    public class FancyDialog : Form
    {
        protected Color AccentColor = Theme.Accent;
        protected const int Radius = 14, Band = 4;
        readonly Timer _fade;

        public FancyDialog()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            BackColor = Theme.Surface; ForeColor = Theme.Text;
            Font = Theme.Font(9.5f);
            KeyPreview = true;
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? AppContext.BaseDirectory); } catch { }
            Opacity = 0;
            _fade = new Timer { Interval = 15 };
            _fade.Tick += (s, e) => { Opacity = Math.Min(1, Opacity + 0.14); if (Opacity >= 1) _fade.Stop(); };
        }

        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ClassStyle |= 0x00020000; /* CS_DROPSHADOW */ return cp; }
        }

        protected override void OnShown(EventArgs e) { base.OnShown(e); _fade.Start(); }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            try { _fade.Stop(); _fade.Dispose(); } catch { }
            base.OnFormClosed(e);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (Width <= 0 || Height <= 0) return;
            using var p = Theme.Round(new Rectangle(0, 0, Width, Height), Radius);
            Region = new Region(p);
            Invalidate();
        }

        /// <summary>¿Se puede cerrar ahora con Esc? (p. ej. no a mitad de una instalación)</summary>
        protected virtual bool CanEscape => true;

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape && CanEscape) { DialogResult = DialogResult.Cancel; Close(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // Arrastrar la ventana desde cualquier zona sin controles.
        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            try { ReleaseCapture(); SendMessage(Handle, 0xA1 /*WM_NCLBUTTONDOWN*/, (IntPtr)2 /*HTCAPTION*/, IntPtr.Zero); } catch { }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var b = new SolidBrush(Theme.Surface)) g.FillRectangle(b, ClientRectangle);
            using (var b = new SolidBrush(AccentColor)) g.FillRectangle(b, 0, 0, Width, Band);
            using (var p = Theme.Round(new Rectangle(0, 0, Width - 1, Height - 1), Radius))
            using (var pen = new Pen(Color.FromArgb(120, AccentColor), 1.2f)) g.DrawPath(pen, p);
            PaintContent(g);
        }

        protected virtual void PaintContent(Graphics g) { }

        // ---- piezas comunes ----

        public static RoundButton PrimaryButton(string text, int width = 200) => new RoundButton
        {
            Text = text, Width = width, Height = 38, Radius = 10, BaseColor = Theme.Accent, HoverColor = Theme.AccentHi,
            GradientTo = Theme.Accent2, TextColor = Color.White, FontSize = 10.5f, FontStyle = FontStyle.Bold
        };

        public static RoundButton SecondaryButton(string text, int width = 130) => new RoundButton
        {
            Text = text, Width = width, Height = 38, Radius = 10, BaseColor = Theme.Surface2, HoverColor = Theme.SurfaceHi,
            TextColor = Theme.Text, FontSize = 10.5f
        };

        // Icono (emoji) en una pastilla del color indicado.
        protected static void DrawIconPill(Graphics g, Rectangle r, string icon, Color c)
        {
            using (var p = Theme.Round(r, 12)) using (var b = new SolidBrush(Color.FromArgb(60, c))) g.FillPath(b, p);
            using (var p = Theme.Round(r, 12)) using (var pen = new Pen(Color.FromArgb(150, c), 1.2f)) g.DrawPath(pen, p);
            using var f = new Font("Segoe UI Emoji", r.Height * 0.42f, GraphicsUnit.Pixel);
            TextRenderer.DrawText(g, icon, f, r, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        // Aviso sencillo (en lugar de MessageBox): icono, título, texto y «Aceptar».
        public static void Info(IWin32Window owner, string title, string text, string icon = "ℹ️")
        {
            using var d = new InfoDialog(title, text, icon);
            if (owner != null) d.ShowDialog(owner); else d.ShowDialog();
        }

        sealed class InfoDialog : FancyDialog
        {
            readonly string _title, _text, _icon;
            static int W => Theme.Px(440); static int Pad => Theme.Px(22); static int IconS => Theme.Px(44);

            public InfoDialog(string title, string text, string icon)
            {
                _title = title ?? ""; _text = text ?? ""; _icon = icon;
                Text = _title;
                int tw = W - Pad * 2 - IconS - 14;
                int hT, hB;
                using (var fT = Theme.Font(12f, FontStyle.Bold)) hT = TextRenderer.MeasureText(_title, fT, new Size(tw, 999), TextFormatFlags.WordBreak).Height;
                using (var fB = Theme.Font(9.75f)) hB = TextRenderer.MeasureText(_text, fB, new Size(tw, 999), TextFormatFlags.WordBreak).Height;
                int h = Band + Pad + Math.Max(IconS, hT + 6 + hB) + 20 + 38 + Pad;
                ClientSize = new Size(W, h);
                var ok = PrimaryButton(I18n.T("Aceptar"), 130);
                ok.Location = new Point(W - Pad - ok.Width, h - Pad - ok.Height);
                ok.Click += (s, e) => { DialogResult = DialogResult.OK; Close(); };
                Controls.Add(ok);
                AcceptButton = null;
            }

            protected override void PaintContent(Graphics g)
            {
                int y = Band + Pad;
                DrawIconPill(g, new Rectangle(Pad, y, IconS, IconS), _icon, AccentColor);
                int x = Pad + IconS + 14, tw = W - x - Pad;
                var fl = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix;
                using var fT = Theme.Font(12f, FontStyle.Bold);
                using var fB = Theme.Font(9.75f);
                int hT = TextRenderer.MeasureText(_title, fT, new Size(tw, 999), fl).Height;
                TextRenderer.DrawText(g, _title, fT, new Rectangle(x, y, tw, hT), Theme.Text, fl);
                TextRenderer.DrawText(g, _text, fB, new Rectangle(x, y + hT + 6, tw, 999), Theme.Subtle, fl);
            }
        }
    }

    /// <summary>Barra de progreso fina y redondeada. Indeterminate = una franja que va y viene.</summary>
    public class SlimProgress : Control
    {
        double _value; bool _ind; float _phase;
        readonly Timer _anim = new Timer { Interval = 30 };
        public Color FillColor = Theme.Accent;

        public SlimProgress()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Height = 10;
            _anim.Tick += (s, e) => { _phase = (_phase + 0.018f) % 1f; Invalidate(); };
        }

        public double Value { get => _value; set { _value = Math.Max(0, Math.Min(1, value)); Invalidate(); } }

        public bool Indeterminate
        {
            get => _ind;
            set { _ind = value; if (value) _anim.Start(); else _anim.Stop(); Invalidate(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var b = new SolidBrush(Theme.ResolveBg(this))) g.FillRectangle(b, ClientRectangle);
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            int rad = Height / 2;
            Theme.FillRound(g, r, rad, Theme.Surface2);
            if (_ind)
            {
                int w = Math.Max(Height * 2, Width / 4);
                // va y viene (suave)
                double t = (1 - Math.Cos(_phase * 2 * Math.PI)) / 2;
                int x = (int)Math.Round(t * (Width - w));
                g.SetClip(r);
                Theme.FillRound(g, new Rectangle(x, 0, w, Height - 1), rad, FillColor);
                g.ResetClip();
            }
            else if (_value > 0)
            {
                int w = Math.Max(Height, (int)Math.Round(r.Width * _value));
                using var p = Theme.Round(new Rectangle(0, 0, w, Height - 1), rad);
                using var br = new LinearGradientBrush(new Rectangle(0, 0, Math.Max(1, w), Height), Theme.Accent2, Theme.AccentHi, LinearGradientMode.Horizontal);
                g.FillPath(br, p);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { try { _anim.Stop(); _anim.Dispose(); } catch { } }
            base.Dispose(disposing);
        }
    }
}
