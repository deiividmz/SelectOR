// (1.2.77) Botón-icono con el emoji EN COLOR (como el resto de iconos de SelectOR: ColorText, DirectWrite). Para los
// botones pequeños de la barra de la sala (🖥 puesto de mando, 🚆 horario de la sala, ⛅ clima de la sala). Un emoji en
// color no cambia de color con el ratón, así que al pasar por encima se aclara el fondo. Si no se pueden pintar en color
// (ColorText.Available falso), se dibuja como una etiqueta normal.

using System;
using System.Drawing;
using System.Windows.Forms;

namespace SelectOR
{
    public sealed class EmojiIcon : Label
    {
        bool _hover;
        public EmojiIcon()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            AutoSize = false; TextAlign = ContentAlignment.MiddleCenter; Cursor = Cursors.Hand;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            if (_hover)
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                Theme.FillRound(g, r, Theme.Px(6), Theme.Surface2);
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
            }
            if (ColorText.Available && ColorText.HasEmoji(Text))
                ColorText.DrawCells(g, new[] { (Text, new Rectangle(0, 0, Width, Height)) }, Font, Theme.Text, new Rectangle(0, 0, Width, Height));
            else
                TextRenderer.DrawText(g, Text, Font, new Rectangle(0, 0, Width, Height), _hover ? Theme.AccentHi : ForeColor,
                                      TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }
    }
}
