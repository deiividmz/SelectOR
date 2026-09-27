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
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int cx = Width / 2, cy = Height / 2;
            bool loading = _mode == Mode.Loading;

            if (loading)
            {
                int r = 15;
                var rect = new Rectangle(cx - r, cy - 30 - r, r * 2, r * 2);
                // pista tenue + arco de acento girando
                using (var track = new Pen(Color.FromArgb(60, Theme.Subtle), 3f))
                    g.DrawArc(track, rect, 0, 360);
                using (var p = new Pen(Theme.Accent, 3f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawArc(p, rect, _angle, 300);
            }

            using (var f = Theme.Font(10.5f, FontStyle.Bold))
            {
                var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.WordBreak;
                int ty = loading ? cy - 6 : cy - 12;
                var area = new Rectangle(8, ty, Width - 16, Height - ty);
                TextRenderer.DrawText(g, _text, f, area, Theme.Subtle, flags);
            }
        }
    }
}
