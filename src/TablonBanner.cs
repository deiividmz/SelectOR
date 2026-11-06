// (1.2.90) La cabecera del Tablón: un teleindicador de estación. A la izquierda el icono y el color de la página, su
// nombre y qué hay en ella (sin hora, a petición); abajo, un letrero que corre con lo último del tablón
// (anuncios, trenes en el mercado, ofertas de empleo, la Liga…). Solo se mueve mientras se ve.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SelectOR
{
    public sealed class TablonBanner : Control
    {
        public static readonly Color Amber = Color.FromArgb(255, 190, 70);
        string _kicker = "", _title = "", _intro = "", _icon = "";
        Color _accent = Theme.AccentHi;
        string _ticker = ""; int _tickW, _tickX;
        readonly Timer _timer = new Timer { Interval = 33 };
        readonly Font _fKicker = Theme.Font(8f, FontStyle.Bold), _fTitle = Theme.Font(17f, FontStyle.Bold), _fIntro = Theme.Font(9.25f),
                      _fIcon = Theme.Font(20f), _fTick = Theme.Font(9f, FontStyle.Bold);

        // En ventanas bajas, más baja: sin la explicación de la página (la lista necesita el sitio)
        bool _compact;
        public bool Compact
        {
            get => _compact;
            set { if (_compact == value) return; _compact = value; Height = value ? Theme.Px(80) : Theme.Px(118); DropBg(); Invalidate(); }
        }

        public TablonBanner()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg; Height = Theme.Px(118);
            _timer.Tick += (s, e) => Step();
        }

        int TickH => Theme.Px(24);
        Rectangle Board => new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - Theme.Px(6)));
        Rectangle TickRect { get { var b = Board; return new Rectangle(b.X + 1, b.Bottom - TickH, b.Width - 2, TickH - 1); } }

        public void SetPage(string icon, string kicker, string title, string intro, Color accent)
        {
            _icon = icon ?? ""; _kicker = kicker ?? ""; _title = title ?? ""; _intro = intro ?? ""; _accent = accent;
            DropBg(); Invalidate();
        }

        public void SetTicker(IEnumerable<string> items)
        {
            string t = string.Join("      ◆      ", items);
            if (t == _ticker) return;
            _ticker = t;
            _tickW = string.IsNullOrEmpty(t) ? 0 : TextRenderer.MeasureText(t, _fTick).Width;
            if (_tickX == 0) _tickX = Width;
            Invalidate(TickRect);
        }

        protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); Sync(); }
        protected override void OnParentChanged(EventArgs e) { base.OnParentChanged(e); Sync(); }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Sync(); }
        void Sync() { bool on = Visible && IsHandleCreated; if (on != _timer.Enabled) _timer.Enabled = on; }

        void Step()
        {
            if (!Visible || FindForm() is not Form f || !f.Visible || f.WindowState == FormWindowState.Minimized) return;
            if (_tickW > 0)
            {
                _tickX -= Math.Max(1, Theme.Px(1));
                if (_tickX < -_tickW) _tickX = TickRect.Width;
                Invalidate(TickRect);
            }
        }

        // lo que no se mueve (todo menos el letrero), en una imagen: el letrero se redibuja 30 veces por segundo
        Bitmap _bg;
        void DropBg() { _bg?.Dispose(); _bg = null; }
        protected override void OnResize(EventArgs e) { DropBg(); base.OnResize(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (Width < 2 || Height < 2) return;
            if (_bg == null || _bg.Width != Width || _bg.Height != Height)
            {
                DropBg(); _bg = new Bitmap(Width, Height);
                using (var bg = Graphics.FromImage(_bg)) PaintStatic(bg);
            }
            var g = e.Graphics;
            g.DrawImageUnscaled(_bg, 0, 0);
            PaintLive(g);
        }

        void PaintStatic(Graphics g)
        {
            g.Clear(BackColor);
            var b = Board; int rad = Theme.Px(12);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Round(b, rad))
            {
                using (var lg = new LinearGradientBrush(b, Color.FromArgb(30, 33, 37), Color.FromArgb(18, 20, 23), LinearGradientMode.Vertical)) g.FillPath(lg, path);
                var st = g.Save();
                g.SetClip(path);
                // la página: un velo de su color desde la izquierda y su franja
                using (var lg2 = new LinearGradientBrush(new Rectangle(b.X, b.Y, Math.Max(1, b.Width / 2), b.Height), Color.FromArgb(46, _accent), Color.FromArgb(0, _accent), LinearGradientMode.Horizontal))
                    g.FillRectangle(lg2, b.X, b.Y, b.Width / 2, b.Height);
                using (var ab = new SolidBrush(_accent)) g.FillRectangle(ab, b.X, b.Y, Theme.Px(6), b.Height);
                // la rejilla de puntos del teleindicador, a la derecha
                using (var dot = new SolidBrush(Color.FromArgb(14, 255, 255, 255)))
                {
                    int step = Theme.Px(6), x0 = b.X + b.Width * 55 / 100;
                    for (int y = b.Y + Theme.Px(6); y < b.Bottom - TickH; y += step)
                        for (int x = x0; x < b.Right; x += step) g.FillRectangle(dot, x, y, Math.Max(1, Theme.Px(1.6)), Math.Max(1, Theme.Px(1.6)));
                }
                // el letrero de abajo
                var tr = TickRect;
                using (var tb = new SolidBrush(Color.FromArgb(11, 12, 14))) g.FillRectangle(tb, b.X, tr.Y, b.Width, b.Bottom - tr.Y);
                using (var pen = new Pen(Color.FromArgb(54, 58, 64))) g.DrawLine(pen, b.X, tr.Y, b.Right, tr.Y);
                g.Restore(st);
                using (var pen = new Pen(Color.FromArgb(62, 68, 74))) g.DrawPath(pen, path);
            }

            // el icono de la página en su círculo
            int pad = Theme.Px(18), ic = Theme.Px(_compact ? 38 : 52);
            var icr = new Rectangle(b.X + pad, b.Y + (b.Height - TickH - ic) / 2, ic, ic);
            using (var cb = new SolidBrush(Color.FromArgb(60, _accent))) g.FillEllipse(cb, icr);
            using (var cp = new Pen(Color.FromArgb(150, _accent), Math.Max(1f, Theme.Px(1.5)))) g.DrawEllipse(cp, icr);
            g.SmoothingMode = SmoothingMode.None;
            ColorText.Text(g, _icon, _fIcon, icr, Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            // los textos
            int tx = icr.Right + Theme.Px(16), tw = Math.Max(Theme.Px(80), b.Right - tx - Theme.Px(20));
            int ty = b.Y + Theme.Px(12);
            const TextFormatFlags L1 = TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
            int kh = TextRenderer.MeasureText("A", _fKicker).Height;
            ColorText.Text(g, _kicker.ToUpperInvariant(), _fKicker, new Rectangle(tx, ty, tw, kh), Amber, L1);
            ty += kh + Theme.Px(1);
            int th = TextRenderer.MeasureText("A", _fTitle).Height;
            ColorText.Text(g, _title, _fTitle, new Rectangle(tx, ty, tw, th), Theme.Text, L1);
            ty += th + Theme.Px(1);
            int ih = b.Bottom - TickH - Theme.Px(6) - ty;
            if (ih > 8 && !_compact) ColorText.Text(g, _intro, _fIntro, new Rectangle(tx, ty, tw, ih), Theme.Subtle,
                TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl);

        }

        void PaintLive(Graphics g)
        {
            // el letrero que corre
            if (_tickW > 0)
            {
                var tr = TickRect;
                var st = g.Save();
                g.SetClip(new Rectangle(tr.X + Theme.Px(8), tr.Y, tr.Width - Theme.Px(16), tr.Height));
                for (int x = tr.X + _tickX; x < tr.Right; x += _tickW + Theme.Px(80))
                    ColorText.Text(g, _ticker, _fTick, new Rectangle(x, tr.Y, _tickW + 4, tr.Height), Amber,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
                g.Restore(st);
            }
        }

        static GraphicsPath Round(Rectangle r, int rad)
        {
            var p = new GraphicsPath(); int d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure(); return p;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _timer.Dispose(); DropBg(); foreach (var f in new[] { _fKicker, _fTitle, _fIntro, _fIcon, _fTick }) f.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
