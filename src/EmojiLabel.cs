// (1.2.90) Etiqueta con los emojis EN COLOR (ColorText, DirectWrite). Sin emojis, o si no se puede, una Label normal.

using System.Drawing;
using System.Windows.Forms;

namespace SelectOR
{
    public class EmojiLabel : Label
    {
        public EmojiLabel() { SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true); }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (!ColorText.HasEmoji(Text) || !ColorText.Available) { base.OnPaint(e); return; }
            using (var b = new SolidBrush(BackColor.A == 0 ? Theme.ResolveBg(this) : BackColor)) e.Graphics.FillRectangle(b, ClientRectangle);
            var a = TextAlign;
            var f = TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis
                  | (a is ContentAlignment.TopCenter or ContentAlignment.MiddleCenter or ContentAlignment.BottomCenter ? TextFormatFlags.HorizontalCenter
                     : a is ContentAlignment.TopRight or ContentAlignment.MiddleRight or ContentAlignment.BottomRight ? TextFormatFlags.Right : TextFormatFlags.Left)
                  | (a is ContentAlignment.MiddleLeft or ContentAlignment.MiddleCenter or ContentAlignment.MiddleRight ? TextFormatFlags.VerticalCenter
                     : a is ContentAlignment.BottomLeft or ContentAlignment.BottomCenter or ContentAlignment.BottomRight ? TextFormatFlags.Bottom : 0);
            if (!AutoSize && Height > Font.Height * 1.8) f |= TextFormatFlags.WordBreak;
            var r = new Rectangle(Padding.Left, Padding.Top, ClientSize.Width - Padding.Horizontal, ClientSize.Height - Padding.Vertical);
            ColorText.Text(e.Graphics, Text, Font, r, Enabled ? ForeColor : SystemColors.GrayText, f);
        }
    }
}
