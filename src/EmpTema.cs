// (2026-11-09) Empresas con aire ferroviario (vista previa aprobada por el usuario):
//  · NavLineHeader: el encabezado de cada grupo del menú lateral como una línea de un plano («L1 OPERACIÓN»), con su color.
//  · StationSign: el título de la sección como un cartel de estación (recuadro azul oscuro con borde claro) y, al lado, la
//    hora en un indicador ámbar.
//  · Hazard: la franja amarilla y negra de la tarjeta de la empresa.

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SelectOR
{
    public sealed class NavLineHeader : Control
    {
        public string Code = "L1";
        public Color LineColor = Theme.Accent;
        public NavLineHeader()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.BgSidebar;
        }
        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(BackColor);
            using var fc = Theme.Font(7f, FontStyle.Bold);
            using var ft = Theme.Font(7.75f, FontStyle.Bold);
            var csz = TextRenderer.MeasureText(g, Code, fc, Size.Empty, TextFormatFlags.NoPadding);
            int h = Math.Min(Height - 4, csz.Height + 4), w = csz.Width + 8;
            var chip = new Rectangle(4, Height - h - 4, w, h);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Theme.FillRound(g, chip, 3, LineColor);
            g.SmoothingMode = SmoothingMode.None;
            TextRenderer.DrawText(g, Code, fc, chip, Color.FromArgb(18, 21, 26), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, (Text ?? "").ToUpperInvariant(), ft, new Rectangle(chip.Right + 7, chip.Y - 2, Width - chip.Right - 8, chip.Height + 4), Color.FromArgb(200, 206, 212),
                                  TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    public sealed class StationSign : Control
    {
        static readonly Color SignFill = Color.FromArgb(22, 40, 70), SignBorder = Color.FromArgb(225, 232, 240), Amber = Color.FromArgb(255, 184, 77);
        readonly Timer _clock;
        public bool ShowClock = true;
        public StationSign()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg;
            _clock = new Timer { Interval = 10_000 };
            _clock.Tick += (s, e) => { if (ShowClock) Invalidate(); };
            _clock.Start();
        }
        protected override void Dispose(bool disposing) { if (disposing) _clock.Dispose(); base.Dispose(disposing); }
        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(BackColor);
            if (string.IsNullOrEmpty(Text)) return;
            using var f = Theme.Font(13.5f, FontStyle.Bold);
            var sz = TextRenderer.MeasureText(g, Text, f, Size.Empty, TextFormatFlags.NoPadding);
            int h = Math.Min(Height - 4, sz.Height + 12), w = sz.Width + 26;
            var box = new Rectangle(1, (Height - h) / 2, Math.Min(w, Width - 2), h);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Theme.FillRound(g, box, 5, SignFill);
            Theme.DrawRoundBorder(g, box, 5, SignBorder, 2f);
            g.SmoothingMode = SmoothingMode.None;
            TextRenderer.DrawText(g, Text, f, box, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            if (!ShowClock) return;
            string t = DateTime.Now.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
            using var fm = new Font("Consolas", 11f, FontStyle.Bold);
            var tsz = TextRenderer.MeasureText(g, t, fm, Size.Empty, TextFormatFlags.NoPadding);
            var cr = new Rectangle(box.Right + 14, box.Y + (box.Height - (tsz.Height + 8)) / 2, tsz.Width + 18, tsz.Height + 8);
            if (cr.Right > Width) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Theme.FillRound(g, cr, 4, Color.FromArgb(14, 16, 19));
            g.SmoothingMode = SmoothingMode.None;
            TextRenderer.DrawText(g, t, fm, cr, Amber, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    public static class Hazard
    {
        // franja de obras (amarillo y negro en diagonal) en lo alto de un rectángulo, recortada a sus esquinas redondeadas
        public static void Draw(Graphics g, Rectangle card, int radius, int height)
        {
            var st = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Theme.Round(card, radius)) g.SetClip(path, CombineMode.Intersect);
            g.SetClip(new Rectangle(card.X, card.Y, card.Width, height), CombineMode.Intersect);
            using (var y = new SolidBrush(Color.FromArgb(250, 200, 40))) g.FillRectangle(y, card.X, card.Y, card.Width, height);
            using (var k = new SolidBrush(Color.FromArgb(24, 24, 24)))
                for (int x = card.X - height; x < card.Right + height; x += height * 2)
                    g.FillPolygon(k, new[] { new Point(x, card.Y + height), new Point(x + height, card.Y), new Point(x + 2 * height, card.Y), new Point(x + height, card.Y + height) });
            g.Restore(st);
        }
    }
}
