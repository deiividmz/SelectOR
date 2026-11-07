// (1.2.91, petición 2026-11-07) Los accesos de la casilla de la sala (junto a CONDUCIR) como BOTONES con icono y texto, en
// vez de iconos sueltos: «🖥 Puesto de mando» y «🚆 Trenes IA» (todos, uno encima del otro) y, para el administrador,
// «⛅ Clima» en una segunda columna. SalaButtonsLayout los coloca a la derecha del texto de la casilla y ensancha la casilla
// lo que ocupan (SalaHostBase: el texto).

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace SelectOR
{
    // botón pequeño redondeado: emoji a color + texto; se resalta al pasar el ratón
    public sealed class SalaButton : Label
    {
        bool _hover;
        public SalaButton(string emoji, string text)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            AutoSize = false; Cursor = Cursors.Hand; Font = Theme.Font(8.8f, FontStyle.Bold); ForeColor = Theme.Text; BackColor = Theme.Surface;
            Emoji = emoji; Text = text;
            Height = Theme.Px(28);
            Width = Theme.Px(38) + TextRenderer.MeasureText(text, Font).Width;
        }
        public string Emoji;
        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(BackColor); g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            Theme.FillRound(g, r, Theme.Px(8), _hover ? Theme.SurfaceHi : Theme.Surface2);
            using (var p = new Pen(_hover ? Theme.AccentHi : Theme.Border)) using (var path = Theme.Round(r, Theme.Px(8))) g.DrawPath(p, path);
            int ex = Theme.Px(8), ew = Theme.Px(22);
            using (var fe = Theme.Font(10.5f)) ColorText.Text(g, Emoji, fe, new Rectangle(ex, 0, ew, Height), ForeColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, Text, Font, new Rectangle(ex + ew + Theme.Px(4), 0, Width - ex - ew - Theme.Px(8), Height), _hover ? Color.White : ForeColor,
                                  TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    public partial class MainMenuForm
    {
        bool _salaWxOn, _salaAiOn;   // (SalaWeatherButtonUpdate) cuáles se ven: Visible del hijo depende también del padre

        void SalaButtonsLayout()
        {
            if (_salaHost == null || _salaDispBtn == null) return;
            int H = _salaHost.ClientSize.Height, gap = Theme.Px(6), x0 = SalaHostBase + Theme.Px(4);
            // columna 1: puesto de mando y, debajo, trenes IA; columna 2: el clima (administrador)
            var col1 = new Control[] { _salaDispBtn, _salaAiOn ? _salaAiBtn : null }.Where(c => c != null).ToList();
            int w1 = col1.Max(c => c.Width);
            int th = col1.Count * _salaDispBtn.Height + (col1.Count - 1) * Theme.Px(5), y = Math.Max(0, (H - th) / 2);
            foreach (var c in col1) { c.Width = w1; c.Location = new Point(x0, y); y += c.Height + Theme.Px(5); }
            int right = x0 + w1;
            if (_salaWxOn && _salaWxBtn != null)
            {
                _salaWxBtn.Location = new Point(right + gap, col1.Count > 1 ? col1[0].Top : Math.Max(0, (H - _salaWxBtn.Height) / 2));
                right = _salaWxBtn.Right;
            }
            int want = right + Theme.Px(10);
            if (_salaHost.Width != want) _salaHost.Width = want;
            foreach (var c in new Control[] { _salaDispBtn, _salaAiBtn, _salaWxBtn }) c?.BringToFront();
        }
    }
}
