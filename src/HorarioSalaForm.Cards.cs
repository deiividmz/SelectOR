// HORARIO DE LA SALA · TARJETAS (1.2.78). Los servicios en tarjetas en vez de en tabla, con toda su información a la vista
// (también la lista entera de paradas). Cada tarjeta va unida a su servicio (no a una posición de la lista): al elegir una
// y editarla se abre siempre ese servicio, aunque dos se llamen igual (con la tabla, la selección se buscaba por el nombre
// al recargar y podía saltar a otro). Clic: elegir (el mapa lo encuadra) · doble clic: editar.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SelectOR
{
    public sealed partial class HorarioSalaForm
    {
        public sealed class ServiceCards : Panel
        {
            public List<Service> Items = new();
            public Func<int, Color> ColorOf = _ => Theme.Accent;
            public Func<string, string> KindName = k => k;
            public Service Selected { get; private set; }
            public event Action SelectedChanged;
            public event Action<Service> Open;
            public string EmptyText = "";
            readonly List<(Service s, Rectangle r)> _boxes = new();
            Service _hover;

            static readonly Color Card = Color.FromArgb(32, 38, 46), CardHi = Color.FromArgb(40, 47, 57), CardSel = Color.FromArgb(36, 54, 46);

            public ServiceCards()
            {
                DoubleBuffered = true; ResizeRedraw = true; AutoScroll = true; BackColor = Theme.Bg;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            }

            public void SetItems(List<Service> items)
            {
                // la elegida sigue elegida si sigue ahí (por su id; si no, ninguna)
                long selId = Selected?.Id ?? 0;
                Items = items ?? new();
                Selected = selId != 0 ? Items.Find(x => x.Id == selId) : null;
                Layout0(); Invalidate();
                SelectedChanged?.Invoke();
            }

            public void Select(Service s) { if (s != Selected) { Selected = s; Invalidate(); SelectedChanged?.Invoke(); EnsureVisible(s); } }
            public int SelectedIndex => Selected == null ? -1 : Items.IndexOf(Selected);

            void EnsureVisible(Service s)
            {
                foreach (var (x, r) in _boxes)
                    if (x == s)
                    {
                        int top = r.Top, bottom = r.Bottom, view = ClientSize.Height, y = -AutoScrollPosition.Y;
                        if (top < y) AutoScrollPosition = new Point(0, top);
                        else if (bottom > y + view) AutoScrollPosition = new Point(0, bottom - view);
                        return;
                    }
            }

            protected override void OnResize(EventArgs e) { base.OnResize(e); Layout0(); }
            protected override void OnScroll(ScrollEventArgs se) { base.OnScroll(se); Invalidate(); }
            protected override void OnMouseWheel(MouseEventArgs e) { base.OnMouseWheel(e); Invalidate(); }

            int Pad => Theme.Px(12);
            Font FTitle => Theme.Font(12f, FontStyle.Bold);
            Font FText => Theme.Font(9.5f);
            Font FSmall => Theme.Font(8.5f);
            Font FBig => Theme.Font(11f, FontStyle.Bold);

            // posición y alto de cada tarjeta (el alto depende de lo largas que sean las paradas)
            void Layout0()
            {
                _boxes.Clear();
                int w = Math.Max(Theme.Px(300), ClientSize.Width - (VerticalScroll.Visible ? 0 : SystemInformation.VerticalScrollBarWidth) - 2);
                int y = 0, gap = Theme.Px(8);
                foreach (var s in Items)
                {
                    int h = CardHeight(s, w);
                    _boxes.Add((s, new Rectangle(0, y, w, h)));
                    y += h + gap;
                }
                AutoScrollMinSize = new Size(0, Math.Max(0, y - gap));
            }

            string StopsText(Service s) => s.Stops == "todas" ? Tr("En todas las estaciones") : s.Stops == "ninguna" ? Tr("Sin paradas") : s.Stops;
            string WhenBig(Service s) => s.Every > 0 ? s.First + " – " + s.Last : s.First;
            string WhenSmall(Service s) => s.Every > 0 ? string.Format(Tr("cada {0} min"), s.Every) : Tr("una sola salida");
            int Departures(Service s)
            {
                if (s.Every <= 0) return 1;
                if (!TimeSpan.TryParse(s.First, out var a) || !TimeSpan.TryParse(s.Last, out var b)) return 0;
                double span = (b - a).TotalMinutes; if (span < 0) span += 1440;
                return (int)(span / s.Every) + 1;
            }

            int TextWidth(int w) => w - Theme.Px(6) - 2 * Pad;   // (tras la franja de color)

            int CardHeight(Service s, int w)
            {
                int tw = TextWidth(w) - Theme.Px(4);
                int stopsH = TextRenderer.MeasureText(Tr("Paradas") + ": " + StopsText(s), FText, new Size(tw, 2000), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
                return Pad + Theme.Px(26) + Theme.Px(6) + Theme.Px(20) + Theme.Px(20) + Theme.Px(4) + stopsH + Pad;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                g.Clear(BackColor);
                if (Items.Count == 0)
                {
                    TextRenderer.DrawText(g, EmptyText, FText, ClientRectangle, Theme.Subtle, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
                    return;
                }
                int dy = AutoScrollPosition.Y;
                for (int i = 0; i < _boxes.Count; i++)
                {
                    var (s, r0) = _boxes[i];
                    var r = new Rectangle(r0.X, r0.Y + dy, r0.Width, r0.Height);
                    if (r.Bottom < 0 || r.Top > ClientSize.Height) continue;
                    DrawCard(g, s, i, r);
                }
            }

            void DrawCard(Graphics g, Service s, int i, Rectangle r)
            {
                bool sel = s == Selected, hov = s == _hover;
                var col = ColorOf(i);
                using (var path = Round(r, Theme.Px(8)))
                {
                    using var b = new SolidBrush(sel ? CardSel : hov ? CardHi : Card); g.FillPath(b, path);
                    if (sel) { using var p = new Pen(Theme.Accent, Theme.Px(2)); g.DrawPath(p, path); }
                }
                // la franja del color de su recorrido en el mapa
                using (var b = new SolidBrush(s.Active ? col : Color.FromArgb(90, col)))
                using (var path = Round(new Rectangle(r.X, r.Y, Theme.Px(6), r.Height), Theme.Px(3))) g.FillPath(b, path);

                int x = r.X + Theme.Px(6) + Pad, y = r.Y + Pad, tw = TextWidth(r.Width);
                var dim = s.Active ? Theme.Text : Theme.Subtle;
                // a la derecha: las salidas
                string big = WhenBig(s), small = WhenSmall(s);
                int n = Departures(s);
                if (n > 1) small += " · " + string.Format(Tr("{0} salidas"), n);
                var bigSz = TextRenderer.MeasureText(big, FBig);
                var smallSz = TextRenderer.MeasureText(small, FSmall);
                int rw = Math.Max(bigSz.Width, smallSz.Width);
                int rx = r.Right - Pad - rw;
                TextRenderer.DrawText(g, big, FBig, new Rectangle(rx, y, rw, Theme.Px(24)), dim, TextFormatFlags.Right | TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(g, small, FSmall, new Rectangle(rx, y + Theme.Px(24), rw, Theme.Px(18)), Theme.Subtle, TextFormatFlags.Right | TextFormatFlags.NoPrefix);

                // título: nombre (del color de su recorrido), tipo y si está desactivado
                int avail = rx - x - Theme.Px(10);
                string name = string.IsNullOrEmpty(s.Name) ? "?" : s.Name;
                var nsz = TextRenderer.MeasureText(name, FTitle);
                int nw = Math.Min(nsz.Width, Math.Max(Theme.Px(40), avail / 2));
                TextRenderer.DrawText(g, name, FTitle, new Rectangle(x, y, nw, Theme.Px(26)), s.Active ? col : Theme.Subtle, TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                int px = x + nw + Theme.Px(8);
                px = Pill(g, KindName(s.Kind), px, y + Theme.Px(3), Color.FromArgb(70, 120, 170));
                if (!s.Active) Pill(g, Tr("Desactivado"), px + Theme.Px(6), y + Theme.Px(3), Color.FromArgb(150, 110, 60));
                y += Theme.Px(26) + Theme.Px(6);

                // recorrido, composición y paradas, enteros
                TextRenderer.DrawText(g, s.Origin + "  →  " + s.Destination, FText, new Rectangle(x, y, avail, Theme.Px(20)), dim, TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                y += Theme.Px(20);
                Line(g, Tr("Composición"), s.Consist, x, y, tw);
                y += Theme.Px(20) + Theme.Px(4);
                var lab = Tr("Paradas") + ": ";
                TextRenderer.DrawText(g, lab + StopsText(s), FText, new Rectangle(x, y, tw - Theme.Px(4), r.Bottom - y), dim, TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            }

            void Line(Graphics g, string label, string value, int x, int y, int w)
            {
                x += Theme.Px(3); w -= Theme.Px(3);   // (sin el margen de TextRenderer: alineado con las demás líneas)
                var ls = TextRenderer.MeasureText(label + ": ", FText, new Size(9999, 99), TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, label + ": ", FText, new Point(x, y), Theme.Subtle, TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, string.IsNullOrEmpty(value) ? "—" : value, FText, new Rectangle(x + ls.Width, y, w - ls.Width, Theme.Px(20)), Theme.Text, TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            }

            int Pill(Graphics g, string text, int x, int y, Color c)
            {
                var sz = TextRenderer.MeasureText(text, FSmall);
                var rr = new Rectangle(x, y, sz.Width + Theme.Px(10), Theme.Px(20));
                using (var path = Round(rr, rr.Height / 2)) using (var b = new SolidBrush(Color.FromArgb(60, c))) g.FillPath(b, path);
                TextRenderer.DrawText(g, text, FSmall, rr, Color.FromArgb(Math.Min(255, c.R + 70), Math.Min(255, c.G + 70), Math.Min(255, c.B + 70)), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                return rr.Right;
            }

            static GraphicsPath Round(Rectangle r, int rad)
            {
                var p = new GraphicsPath(); int d = Math.Max(1, rad * 2);
                d = Math.Min(d, Math.Min(r.Width, r.Height));
                p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
                p.CloseFigure(); return p;
            }

            Service At(Point p)
            {
                var q = new Point(p.X, p.Y - AutoScrollPosition.Y);
                foreach (var (s, r) in _boxes) if (r.Contains(q)) return s;
                return null;
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                var h = At(e.Location);
                if (h != _hover) { _hover = h; Cursor = h != null ? Cursors.Hand : Cursors.Default; Invalidate(); }
            }
            protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (_hover != null) { _hover = null; Invalidate(); } }
            protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); Focus(); var s = At(e.Location); if (s != null) Select(s); }
            protected override void OnMouseDoubleClick(MouseEventArgs e) { base.OnMouseDoubleClick(e); var s = At(e.Location); if (s != null) { Select(s); Open?.Invoke(s); } }
            protected override bool IsInputKey(Keys k) => k == Keys.Up || k == Keys.Down || base.IsInputKey(k);
            protected override void OnKeyDown(KeyEventArgs e)
            {
                base.OnKeyDown(e);
                if (Items.Count == 0) return;
                int i = SelectedIndex;
                if (e.KeyCode == Keys.Down) Select(Items[Math.Min(Items.Count - 1, i + 1)]);
                else if (e.KeyCode == Keys.Up) Select(Items[Math.Max(0, i < 0 ? 0 : i - 1)]);
                else if (e.KeyCode == Keys.Enter && Selected != null) Open?.Invoke(Selected);
            }
        }
    }
}
