// Tira de indicadores de la empresa (arriba de cada sección de Empresas). Cada tarjeta: icono a color en
// su pastilla, rótulo, valor y una línea de contexto. «Fill» reparte el ancho entre todas; si no, cada
// tarjeta mide lo que su contenido (para cuando solo hay una y no tiene sentido estirarla).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SelectOR
{
    public class EmpKpiStrip : Control
    {
        public sealed class Kpi
        {
            public string Icon = "", Caption = "", Value = "", Sub = "";
            public Color Tint = Theme.Accent, ValueColor = Theme.Text;
            public List<(string text, Color col)> Chips;   // opcional: pastillas en vez de la línea de contexto
        }
        public List<Kpi> Items = new List<Kpi>();
        public bool Fill = true;
        readonly Font _fCap = Theme.Font(7.75f, FontStyle.Bold), _fVal = Theme.Font(15f, FontStyle.Bold), _fSub = Theme.Font(8.25f), _fChip = Theme.Font(8.25f, FontStyle.Bold), _fIc = EmojiPicker.EmojiFont(19f);
        const TextFormatFlags L1 = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;

        public EmpKpiStrip()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg;
        }
        protected override void Dispose(bool disposing) { if (disposing) { _fCap.Dispose(); _fVal.Dispose(); _fSub.Dispose(); _fChip.Dispose(); _fIc.Dispose(); } base.Dispose(disposing); }

        public int PreferredHeight => Theme.Px(14) * 2 + _fCap.Height + Theme.Px(2) + _fVal.Height + Theme.Px(2) + Math.Max(_fSub.Height, Theme.Px(20));

        static int TW(string t, Font f) => string.IsNullOrEmpty(t) ? 0 : TextRenderer.MeasureText(t, f, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;

        int ChipW(string t) => TW(t, _fChip) + Theme.Px(18);

        // Ancho que pide el contenido de una tarjeta.
        int ContentW(Kpi k)
        {
            int w = Math.Max(TW(k.Caption, _fCap), TW(k.Value, _fVal));
            if (k.Chips != null) { int cw = 0; foreach (var c in k.Chips) cw += ChipW(c.text) + Theme.Px(6); w = Math.Max(w, cw); }
            else w = Math.Max(w, TW(k.Sub, _fSub));
            return Theme.Px(16) + Theme.Px(42) + Theme.Px(14) + w + Theme.Px(22);
        }

        List<Rectangle> Geo()
        {
            var r = new List<Rectangle>();
            int gap = Theme.Px(10), n = Items.Count, h = Height - 1;
            if (n == 0) return r;
            if (Fill)
            {
                int cw = (Width - 1 - gap * (n - 1)) / n;
                for (int i = 0; i < n; i++) r.Add(new Rectangle(i * (cw + gap), 0, cw, h));
            }
            else
            {
                int x = 0;
                foreach (var k in Items) { int cw = Math.Min(ContentW(k), Width - 1 - x); r.Add(new Rectangle(x, 0, cw, h)); x += cw + gap; }
            }
            return r;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            var geo = Geo();
            var icons = new List<(string, Rectangle)>();
            for (int i = 0; i < geo.Count; i++)
            {
                var k = Items[i]; var r = geo[i];
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var path = Theme.Round(r, Theme.Px(12)))
                {
                    using (var b = new LinearGradientBrush(r, Theme.Mix(Theme.Surface, k.Tint, 0.10f), Theme.Surface, LinearGradientMode.Horizontal)) g.FillPath(b, path);
                    using (var p = new Pen(Color.FromArgb(26, 255, 255, 255))) g.DrawPath(p, path);
                }
                var ic = new Rectangle(r.X + Theme.Px(16), r.Y + (r.Height - Theme.Px(42)) / 2, Theme.Px(42), Theme.Px(42));
                using (var path = Theme.Round(ic, Theme.Px(11))) using (var b = new SolidBrush(Color.FromArgb(46, k.Tint))) g.FillPath(b, path);
                g.SmoothingMode = SmoothingMode.None;
                icons.Add((k.Icon, ic));

                int x = ic.Right + Theme.Px(14), w = r.Right - Theme.Px(12) - x;
                int blockH = _fCap.Height + Theme.Px(2) + _fVal.Height + Theme.Px(2) + Math.Max(_fSub.Height, k.Chips != null ? Theme.Px(20) : 0);
                int y = r.Y + (r.Height - blockH) / 2;
                TextRenderer.DrawText(g, k.Caption, _fCap, new Rectangle(x, y, w, _fCap.Height), Theme.Subtle, L1);
                y += _fCap.Height + Theme.Px(2);
                TextRenderer.DrawText(g, k.Value, _fVal, new Rectangle(x, y, w, _fVal.Height), k.ValueColor, L1);
                y += _fVal.Height + Theme.Px(2);
                if (k.Chips != null)
                {
                    int cx = x, ch = Theme.Px(20);
                    foreach (var (t, col) in k.Chips)
                    {
                        int cw = ChipW(t); if (cx + cw > r.Right - Theme.Px(8)) break;
                        var cr = new Rectangle(cx, y, cw, ch);
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        using (var path = Theme.Round(cr, ch / 2)) using (var b = new SolidBrush(Color.FromArgb(40, col))) g.FillPath(b, path);
                        g.SmoothingMode = SmoothingMode.None;
                        TextRenderer.DrawText(g, t, _fChip, cr, col, L1 | TextFormatFlags.HorizontalCenter);
                        cx += cw + Theme.Px(6);
                    }
                }
                else TextRenderer.DrawText(g, k.Sub, _fSub, new Rectangle(x, y, w, _fSub.Height), Theme.Subtle, L1);
            }
            ColorText.DrawCells(g, icons, _fIc, Theme.Text, ClientRectangle);
        }

        string _sig;
        public void SetItems(List<Kpi> items, bool fill)
        {
            // Lo mismo que ya se ve: no se repinta (se llama al abrir cada sección y al llegar cada dato).
            var sb = new System.Text.StringBuilder(fill ? "F" : "f");
            if (items != null) foreach (var k in items)
            {
                sb.Append('|').Append(k.Icon).Append('·').Append(k.Caption).Append('·').Append(k.Value).Append('·').Append(k.Sub)
                  .Append('·').Append(k.Tint.ToArgb()).Append('·').Append(k.ValueColor.ToArgb());
                if (k.Chips != null) foreach (var c in k.Chips) sb.Append('·').Append(c.Item1).Append(c.Item2.ToArgb());
            }
            string sig = sb.ToString();
            if (sig == _sig && Items != null) return;
            _sig = sig;
            Items = items; Fill = fill;
            int h = PreferredHeight; if (Height != h) Height = h;
            Invalidate();
        }
    }
}
