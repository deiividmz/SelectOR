// Superposición para las listas de rutas/trenes: muestra "Cargando…" con spinner animado,
// o "Sin rutas / Sin trenes" cuando la carpeta no tiene contenido. Se pone encima de la lista.

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SelectOR
{
    public class ListStatePanel : Control
    {
        public enum Mode { Hidden, Loading, Empty }

        Mode _mode = Mode.Hidden;
        string _text = "";
        readonly Timer _timer;
        float _angle;

        public Color Bg = Theme.Surface;

        public ListStatePanel()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
            _timer = new Timer { Interval = 33 };  // ~30 fps
            _timer.Tick += (s, e) => { _angle = (_angle + 9f) % 360f; Invalidate(); };
            Visible = false;
            TabStop = false;
        }

        public string Icon = "🗂️";        // icono del aviso de «vacío»
        public string Hint = "";           // explicación debajo del aviso
        public void SetState(Mode m, string text, string hint)
        {
            Hint = hint ?? "";
            SetState(m, text);
        }

        public void SetState(Mode m, string text)
        {
            _text = text ?? "";
            _mode = m;
            if (m == Mode.Hidden) { _timer.Stop(); Visible = false; return; }
            Visible = true; BringToFront();
            if (m == Mode.Loading) _timer.Start(); else _timer.Stop();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            using (var b = new SolidBrush(Bg)) g.FillRectangle(b, ClientRectangle);
            int pad = 6;
            if (_mode == Mode.Loading)
            {
                // Tarjetas «fantasma» con un brillo que pasa: se ve que algo está llegando y dónde.
                using (var f = Theme.Font(9f, FontStyle.Bold))
                    TextRenderer.DrawText(g, _text, f, new Rectangle(pad, pad, Width - pad * 2, 20), Theme.Subtle, TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
                int y = pad + 28, h = 64, gap = 10;
                float phase = _angle / 360f;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                for (int k = 0; y + h <= Height - pad && k < 8; k++, y += h + gap)
                {
                    var r = new Rectangle(pad, y, Width - pad * 2 - 1, h);
                    Theme.FillRound(g, r, 10, Color.FromArgb(46, 50, 54));
                    var bar1 = new Rectangle(r.X + 12, r.Y + 14, (int)(r.Width * 0.55), 12);
                    var bar2 = new Rectangle(r.X + 12, r.Y + 36, (int)(r.Width * 0.35), 10);
                    foreach (var bar in new[] { bar1, bar2 })
                    {
                        if (bar.Width <= 4) continue;
                        using var lg = new LinearGradientBrush(new Rectangle(r.X - r.Width + (int)(phase * r.Width * 3), r.Y, r.Width, r.Height),
                                                               Color.FromArgb(60, 64, 68), Color.FromArgb(84, 89, 94), LinearGradientMode.Horizontal)
                        { WrapMode = WrapMode.TileFlipX };
                        using var path = Theme.Round(bar, bar.Height / 2);
                        g.FillPath(lg, path);
                    }
                }
                return;
            }
            // Vacío: icono, aviso y, si la hay, la explicación.
            int cy = Height / 2;
            using (var fi = EmojiPicker.EmojiFont(24f))
                ColorText.DrawCells(g, new[] { (Icon, new Rectangle(0, cy - 64, Width, 44)) }, fi, Theme.Subtle, ClientRectangle);
            using (var f = Theme.Font(10.5f, FontStyle.Bold))
                TextRenderer.DrawText(g, _text, f, new Rectangle(8, cy - 14, Width - 16, 24), Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis);
            if (Hint.Length > 0)
                using (var f2 = Theme.Font(9f))
                    TextRenderer.DrawText(g, Hint, f2, new Rectangle(16, cy + 14, Width - 32, Height - cy - 14), Theme.Subtle, TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak);
        }
    }
}
