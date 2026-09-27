// Gráfica de barras horizontal dibujada a mano (GDI+), temática oscura.
// Sin dependencias: WinForms .NET 8 no incluye System.Windows.Forms.DataVisualization.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SelectOR
{
    public class BarChart : Control
    {
        public List<KeyValuePair<string, double>> Data { get; set; } = new();
        public Func<double, string> Format = v => v.ToString("0");
        public string EmptyText = I18n.T("Sin datos");
        public Color BarA = Theme.Accent;
        public Color BarB = Theme.Accent2;

        public BarChart()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Surface;
        }

        public void SetData(IEnumerable<KeyValuePair<string, double>> data)
        {
            Data = new List<KeyValuePair<string, double>>(data);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = ClientRectangle;
            Theme.FillRound(g, r, 10, Theme.Surface);

            if (Data == null || Data.Count == 0)
            {
                using var f0 = Theme.Font(9.5f);
                TextRenderer.DrawText(g, EmptyText, f0, r, Theme.Subtle,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }

            double max = 0;
            foreach (var kv in Data) if (kv.Value > max) max = kv.Value;
            if (max <= 0) max = 1;

            int pad = 10;
            int n = Data.Count;
            int rowH = Math.Min(34, Math.Max(18, (r.Height - pad * 2) / n));
            int top = r.Y + pad + Math.Max(0, (r.Height - pad * 2 - rowH * n) / 2);

            using var f = Theme.Font(9f);
            using var fb = Theme.Font(9f, FontStyle.Bold);

            int labelW = Math.Min(180, (int)(r.Width * 0.42f));
            // El ancho del valor se adapta al importe más ancho para que NUNCA lo tape la barra.
            int valueW = 74;
            foreach (var kv in Data) valueW = Math.Max(valueW, TextRenderer.MeasureText(Format(kv.Value), fb).Width + 8);
            valueW = Math.Min(valueW, (int)(r.Width * 0.42f));
            int barX = r.X + pad + labelW + 6;
            int barMaxW = Math.Max(10, r.Right - pad - valueW - barX);

            for (int i = 0; i < n; i++)
            {
                var kv = Data[i];
                int y = top + i * rowH;
                var labelRect = new Rectangle(r.X + pad, y, labelW, rowH);
                TextRenderer.DrawText(g, kv.Key, f, labelRect, Theme.Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

                int bw = (int)Math.Round(barMaxW * (kv.Value / max));
                if (bw < 3 && kv.Value > 0) bw = 3;
                int barH = Math.Max(10, Math.Min(18, rowH - 12));
                // Pista de fondo: se ve de un vistazo cuánto le falta a cada barra para el máximo.
                var trackRect = new Rectangle(barX, y + (rowH - barH) / 2, Math.Max(2, barMaxW), barH);
                using (var tp = Theme.Round(trackRect, barH / 2))
                using (var tb = new SolidBrush(Theme.Mix(Theme.Surface, Theme.Bg, 0.5f)))
                    g.FillPath(tb, tp);
                var barRect = new Rectangle(barX, trackRect.Y, Math.Max(1, bw), barH);
                if (bw > 0)
                {
                    using var path = Theme.Round(barRect, barH / 2);
                    using var br = new LinearGradientBrush(new Rectangle(barRect.X, barRect.Y, Math.Max(2, barRect.Width), barRect.Height), BarA, BarB, LinearGradientMode.Horizontal);
                    g.FillPath(br, path);
                }
                var valRect = new Rectangle(r.Right - pad - valueW, y, valueW, rowH);
                TextRenderer.DrawText(g, Format(kv.Value), fb, valRect, Theme.Text,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }
    }

    // Anillo con leyenda: reparto de un total entre categorías (p. ej. el desglose de costes).
    // Se lee mucho mejor que seis barras cuando lo que importa es la PROPORCIÓN.
    public class DonutChart : Control
    {
        public List<KeyValuePair<string, double>> Data { get; set; } = new();
        public Func<double, string> Format = v => v.ToString("0");
        public string EmptyText = I18n.T("Sin datos");
        public string CenterCaption = I18n.T("TOTAL");

        static readonly Color[] Palette =
        {
            Color.FromArgb(76, 175, 80),    Color.FromArgb(96, 165, 250), Color.FromArgb(245, 158, 66),
            Color.FromArgb(167, 139, 250),  Color.FromArgb(94, 190, 155), Color.FromArgb(229, 115, 115),
            Color.FromArgb(240, 196, 90),   Color.FromArgb(129, 140, 248),
        };

        public DonutChart()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Surface;
        }

        public void SetData(IEnumerable<KeyValuePair<string, double>> data)
        {
            Data = new List<KeyValuePair<string, double>>(data);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            var r = ClientRectangle;
            Theme.FillRound(g, r, 10, Theme.Surface);

            double total = 0;
            if (Data != null) foreach (var kv in Data) if (kv.Value > 0) total += kv.Value;
            if (total <= 0)
            {
                using var f0 = Theme.Font(9.5f);
                TextRenderer.DrawText(g, EmptyText, f0, r, Theme.Subtle,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }

            int pad = Theme.Px(12);
            int side = Math.Max(Theme.Px(90), Math.Min(r.Height - pad * 2, (int)(r.Width * 0.44f)));
            var box = new Rectangle(r.X + pad, r.Y + (r.Height - side) / 2, side, side);
            float thick = Math.Max(Theme.Px(14), side * 0.20f);

            float start = -90f;
            int i = 0;
            foreach (var kv in Data)
            {
                if (kv.Value <= 0) { i++; continue; }
                float sweep = (float)(kv.Value / total * 360.0);
                using (var pen = new Pen(Palette[i % Palette.Length], thick) { StartCap = LineCap.Flat, EndCap = LineCap.Flat })
                {
                    var arc = new RectangleF(box.X + thick / 2, box.Y + thick / 2, box.Width - thick, box.Height - thick);
                    g.DrawArc(pen, arc, start, Math.Max(0.8f, sweep - 1.2f));   // hueco fino entre porciones
                }
                start += sweep;
                i++;
            }

            // Centro: total y rótulo.
            using (var fTot = Theme.Font(13.5f, FontStyle.Bold))
            using (var fCap = Theme.Font(7.5f, FontStyle.Bold))
            {
                string tot = Format(total);
                var c = new Rectangle(box.X + (int)thick, box.Y + (int)thick, box.Width - (int)thick * 2, box.Height - (int)thick * 2);
                int th = TextRenderer.MeasureText(g, tot, fTot).Height, ch = TextRenderer.MeasureText(g, CenterCaption, fCap).Height;
                int top = c.Y + (c.Height - th - ch - 2) / 2;
                TextRenderer.DrawText(g, CenterCaption, fCap, new Rectangle(c.X, top, c.Width, ch), Theme.Subtle,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, tot, fTot, new Rectangle(c.X, top + ch + 2, c.Width, th), Theme.Text,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
            }

            // Leyenda: punto de color, concepto, importe y porcentaje.
            int lx = box.Right + Theme.Px(16), lw = r.Right - pad - lx;
            if (lw < Theme.Px(80)) return;
            using var f = Theme.Font(8.5f);
            using var fb = Theme.Font(8.5f, FontStyle.Bold);
            int rows = Math.Min(Data.Count, Math.Max(1, (r.Height - pad * 2) / Theme.Px(22)));
            int rowH = Theme.Px(22);
            int ly = r.Y + (r.Height - rowH * rows) / 2;
            for (int k = 0; k < rows; k++)
            {
                var kv = Data[k];
                int dot = Theme.Px(9);
                using (var b = new SolidBrush(Palette[k % Palette.Length]))
                    g.FillEllipse(b, lx, ly + (rowH - dot) / 2, dot, dot);
                string pct = (kv.Value / total * 100).ToString("0.#") + " %";
                int pw = TextRenderer.MeasureText(g, pct, fb).Width + Theme.Px(6);
                int vw = TextRenderer.MeasureText(g, Format(kv.Value), fb).Width + Theme.Px(10);
                int nameW = Math.Max(Theme.Px(30), lw - dot - Theme.Px(8) - pw - vw);
                TextRenderer.DrawText(g, kv.Key, f, new Rectangle(lx + dot + Theme.Px(8), ly, nameW, rowH), Theme.Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, Format(kv.Value), fb, new Rectangle(lx + dot + Theme.Px(8) + nameW, ly, vw, rowH), Theme.Text,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, pct, fb, new Rectangle(lx + lw - pw, ly, pw, rowH), Theme.Subtle,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                ly += rowH;
            }
        }
    }

    // Evolución en el tiempo: línea con relleno degradado, rejilla y el último valor destacado.
    public class AreaChart : Control
    {
        public List<KeyValuePair<string, double>> Data { get; set; } = new();
        public Func<double, string> Format = v => v.ToString("0");
        public string EmptyText = I18n.T("Sin datos");
        public Color LineColor = Theme.Accent;

        public AreaChart()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Surface;
        }

        public void SetData(IEnumerable<KeyValuePair<string, double>> data)
        {
            Data = new List<KeyValuePair<string, double>>(data);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            var r = ClientRectangle;
            Theme.FillRound(g, r, 10, Theme.Surface);

            if (Data == null || Data.Count == 0)
            {
                using var f0 = Theme.Font(9.5f);
                TextRenderer.DrawText(g, EmptyText, f0, r, Theme.Subtle,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }

            double max = 0;
            foreach (var kv in Data) if (kv.Value > max) max = kv.Value;
            if (max <= 0) max = 1;

            using var f = Theme.Font(8f);
            using var fb = Theme.Font(8.5f, FontStyle.Bold);
            int padL = Theme.Px(10), padR = Theme.Px(12), padT = Theme.Px(14), padB = Theme.Px(22);
            int axisW = TextRenderer.MeasureText(g, Format(max), f).Width + Theme.Px(8);
            var plot = new Rectangle(r.X + padL + axisW, r.Y + padT, Math.Max(Theme.Px(30), r.Width - padL - padR - axisW), Math.Max(Theme.Px(30), r.Height - padT - padB));

            // Rejilla horizontal con sus valores.
            using (var grid = new Pen(Theme.Mix(Theme.Surface, Theme.Bg, 0.55f)))
                for (int i = 0; i <= 3; i++)
                {
                    int y = plot.Bottom - (int)(plot.Height * i / 3.0);
                    g.DrawLine(grid, plot.X, y, plot.Right, y);
                    string lab = Format(max * i / 3.0);
                    TextRenderer.DrawText(g, lab, f, new Rectangle(r.X + padL - Theme.Px(2), y - Theme.Px(8), axisW, Theme.Px(16)), Theme.Subtle,
                        TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                }

            int n = Data.Count;
            var pts = new PointF[n];
            for (int i = 0; i < n; i++)
            {
                float x = n == 1 ? plot.X + plot.Width / 2f : plot.X + plot.Width * i / (float)(n - 1);
                float y = (float)(plot.Bottom - plot.Height * (Data[i].Value / max));
                pts[i] = new PointF(x, y);
            }

            // Relleno bajo la línea.
            if (n >= 2)
            {
                using var area = new GraphicsPath();
                area.AddLines(pts);
                area.AddLine(pts[n - 1].X, pts[n - 1].Y, pts[n - 1].X, plot.Bottom);
                area.AddLine(pts[n - 1].X, plot.Bottom, pts[0].X, plot.Bottom);
                area.CloseFigure();
                using var fill = new LinearGradientBrush(new Rectangle(plot.X, plot.Y, Math.Max(1, plot.Width), Math.Max(1, plot.Height)),
                    Color.FromArgb(110, LineColor), Color.FromArgb(8, LineColor), LinearGradientMode.Vertical);
                g.FillPath(fill, area);
                using var pen = new Pen(LineColor, 2.2f) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawLines(pen, pts);
            }

            // Puntos y etiquetas del eje X.
            for (int i = 0; i < n; i++)
            {
                int d = Theme.Px(7);
                using (var b = new SolidBrush(Theme.Surface)) g.FillEllipse(b, pts[i].X - d / 2f, pts[i].Y - d / 2f, d, d);
                using (var pen = new Pen(LineColor, 2f)) g.DrawEllipse(pen, pts[i].X - d / 2f, pts[i].Y - d / 2f, d, d);
                int lw = (int)(plot.Width / Math.Max(1, n)) + Theme.Px(20);
                TextRenderer.DrawText(g, Data[i].Key, f, new Rectangle((int)pts[i].X - lw / 2, plot.Bottom + Theme.Px(4), lw, Theme.Px(14)), Theme.Subtle,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            }

            // Último valor, en una pastilla sobre su punto.
            string last = Format(Data[n - 1].Value);
            var sz = TextRenderer.MeasureText(g, last, fb);
            var chip = new Rectangle((int)pts[n - 1].X - sz.Width / 2 - Theme.Px(6), (int)pts[n - 1].Y - sz.Height - Theme.Px(10),
                                     sz.Width + Theme.Px(12), sz.Height + Theme.Px(5));
            if (chip.Right > r.Right - padR) chip.X = r.Right - padR - chip.Width;
            if (chip.X < plot.X) chip.X = plot.X;
            if (chip.Y < r.Y + Theme.Px(2)) chip.Y = r.Y + Theme.Px(2);
            Theme.FillRound(g, chip, Theme.Px(8), Theme.Mix(Theme.Surface, LineColor, 0.35f));
            TextRenderer.DrawText(g, last, fb, chip, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }
}
