// Tira de indicadores de la empresa (arriba de cada sección de Empresas). Cada tarjeta: icono a color en
// su pastilla, rótulo, valor y una línea de contexto. «Fill» reparte el ancho entre todas; si no, cada
// tarjeta mide lo que su contenido (para cuando solo hay una y no tiene sentido estirarla).
// (2026-11-09) Como billetes de tren: muescas a los lados, troquel con su número a la derecha, icono de línea (LineIcons) y
// la cifra en ámbar con letra de máquina (si no lleva un color propio: rojo en negativo, etc.).

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
        static readonly Color Amber = Color.FromArgb(255, 184, 77);
        readonly Font _fStub = new Font("Consolas", 7f), _fCap = Theme.Font(7.75f, FontStyle.Bold), _fVal = new Font("Consolas", 15f, FontStyle.Bold), _fSub = Theme.Font(8.25f), _fChip = Theme.Font(8.25f, FontStyle.Bold), _fIc = EmojiPicker.EmojiFont(19f);
        const TextFormatFlags L1 = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;

        public EmpKpiStrip()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg;
        }
        protected override void Dispose(bool disposing) { if (disposing) { _fStub.Dispose(); _fCap.Dispose(); _fVal.Dispose(); _fSub.Dispose(); _fChip.Dispose(); _fIc.Dispose(); } base.Dispose(disposing); }

        public int PreferredHeight => Theme.Px(14) * 2 + _fCap.Height + Theme.Px(2) + _fVal.Height + Theme.Px(2) + Math.Max(_fSub.Height, Theme.Px(20));

        static int TW(string t, Font f) => string.IsNullOrEmpty(t) ? 0 : ColorText.LineWidth(t, f);

        int ChipW(string t) => TW(t, _fChip) + Theme.Px(18);

        // Ancho que pide el contenido de una tarjeta.
        int ContentW(Kpi k)
        {
            int w = Math.Max(TW(k.Caption, _fCap), TW(k.Value, _fVal));
            if (k.Chips != null) { int cw = 0; foreach (var c in k.Chips) cw += ChipW(c.text) + Theme.Px(6); w = Math.Max(w, cw); }
            else w = Math.Max(w, TW(k.Sub, _fSub));
            return Theme.Px(16) + Theme.Px(42) + Theme.Px(14) + w + Theme.Px(22) + StubW;
        }

        int StubW => Theme.Px(30);   // el troquel con su número

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
                using (var path = Theme.Round(r, Theme.Px(8)))
                {
                    using (var b = new LinearGradientBrush(r, Theme.Mix(Theme.Surface, k.Tint, 0.08f), Theme.Surface, LinearGradientMode.Horizontal)) g.FillPath(b, path);
                    using (var p = new Pen(Color.FromArgb(26, 255, 255, 255))) g.DrawPath(p, path);
                }
                // muescas (a los lados, en medio) y el troquel: línea de puntos con su muesca arriba y abajo
                int nr = Theme.Px(6), sx = r.Right - StubW;
                using (var hole = new SolidBrush(BackColor))
                {
                    g.FillEllipse(hole, r.X - nr, r.Y + r.Height / 2 - nr, 2 * nr, 2 * nr);
                    g.FillEllipse(hole, r.Right - nr, r.Y + r.Height / 2 - nr, 2 * nr, 2 * nr);
                    g.FillEllipse(hole, sx - Theme.Px(4), r.Y - Theme.Px(4), Theme.Px(8), Theme.Px(8));
                    g.FillEllipse(hole, sx - Theme.Px(4), r.Bottom - Theme.Px(4), Theme.Px(8), Theme.Px(8));
                }
                using (var dp = new Pen(Color.FromArgb(70, 255, 255, 255), 1f) { DashStyle = DashStyle.Dot }) g.DrawLine(dp, sx, r.Y + Theme.Px(6), sx, r.Bottom - Theme.Px(6));
                // el número del billete, en vertical
                {
                    var st = g.Save();
                    g.TranslateTransform(sx + StubW / 2f, r.Y + r.Height / 2f); g.RotateTransform(-90);
                    using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                    using var sb = new SolidBrush(Color.FromArgb(120, 200, 206, 212));
                    g.DrawString("Nº " + (417 + i).ToString("000000"), _fStub, sb, new RectangleF(-r.Height / 2f, -StubW / 2f, r.Height, StubW), sf);
                    g.Restore(st);
                }
                var ic = new Rectangle(r.X + Theme.Px(16), r.Y + (r.Height - Theme.Px(42)) / 2, Theme.Px(42), Theme.Px(42));
                if (LineIcons.Has(k.Icon))
                    LineIcons.Draw(g, k.Icon, new RectangleF(ic.X + Theme.Px(4), ic.Y + Theme.Px(4), ic.Width - Theme.Px(8), ic.Height - Theme.Px(8)), k.Tint);
                else
                {
                    using (var path = Theme.Round(ic, Theme.Px(11))) using (var b = new SolidBrush(Color.FromArgb(46, k.Tint))) g.FillPath(b, path);
                    icons.Add((k.Icon, ic));
                }
                g.SmoothingMode = SmoothingMode.None;

                int x = ic.Right + Theme.Px(14), w = sx - Theme.Px(10) - x;
                int blockH = _fCap.Height + Theme.Px(2) + _fVal.Height + Theme.Px(2) + Math.Max(_fSub.Height, k.Chips != null ? Theme.Px(20) : 0);
                int y = r.Y + (r.Height - blockH) / 2;
                ColorText.Text(g, (k.Caption ?? "").ToUpperInvariant(), _fCap, new Rectangle(x, y, w, _fCap.Height), Theme.Subtle, L1);
                y += _fCap.Height + Theme.Px(2);
                ColorText.Text(g, k.Value, _fVal, new Rectangle(x, y, w, _fVal.Height), k.ValueColor == Theme.Text ? Amber : k.ValueColor, L1);
                y += _fVal.Height + Theme.Px(2);
                if (k.Chips != null)
                {
                    int cx = x, ch = Theme.Px(20);
                    foreach (var (t, col) in k.Chips)
                    {
                        int cw = ChipW(t); if (cx + cw > sx - Theme.Px(8)) break;
                        var cr = new Rectangle(cx, y, cw, ch);
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        using (var path = Theme.Round(cr, ch / 2)) using (var b = new SolidBrush(Color.FromArgb(40, col))) g.FillPath(b, path);
                        g.SmoothingMode = SmoothingMode.None;
                        ColorText.Text(g, t, _fChip, cr, col, L1 | TextFormatFlags.HorizontalCenter);
                        cx += cw + Theme.Px(6);
                    }
                }
                else ColorText.Text(g, k.Sub, _fSub, new Rectangle(x, y, w, _fSub.Height), Theme.Subtle, L1);
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
