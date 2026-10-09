// Selector del teleindicador (cabecera «Tren elegido» de Conducción libre y «Tren seleccionado» de Horarios):
//   ◀  [ cartel elegido, en grande ]  ▾  ▶
// Las flechas (y la rueda sobre el cartel) pasan al destino anterior o siguiente; un clic en el cartel abre la
// galería: todos los carteles en cuadrícula, con buscador, «Original del tren» el primero y el elegido en verde.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SelectOR
{
    public sealed class TeleOption
    {
        public string Name;     // null = los carteles originales del tren
        public string Sample;   // fichero del cartel (para la miniatura)
    }

    public sealed class TeleSelector : Control
    {
        public const int LedH = 26;                 // alto del cartel en la cabecera
        const int Arrow = 26, MaxLedW = 230;   // (en Horarios la columna es estrecha: un cartel más largo se encoge)
        readonly List<TeleOption> _items = new();
        int _sel = -1;
        int _hover = -1;   // 0 = ◀ · 1 = cartel · 2 = ▶
        readonly Font _f = Theme.Font(9f, FontStyle.Bold);
        readonly ToolTip _tip = new ToolTip();
        ToolStripDropDown _pop;

        public event Action Chosen;   // el usuario ha elegido otro (no al rellenar)

        public IReadOnlyList<TeleOption> Items => _items;
        public int SelectedIndex => _sel;
        public TeleOption Selected => _sel >= 0 && _sel < _items.Count ? _items[_sel] : null;

        public TeleSelector()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Height = LedH + 8; Cursor = Cursors.Hand;
        }
        protected override void Dispose(bool disposing) { if (disposing) { _f.Dispose(); _tip.Dispose(); try { _pop?.Dispose(); } catch { } } base.Dispose(disposing); }

        public void SetItems(IEnumerable<TeleOption> items, int selected)
        {
            _items.Clear(); _items.AddRange(items);
            _sel = _items.Count == 0 ? -1 : Math.Max(0, Math.Min(_items.Count - 1, selected));
            Fit(); Invalidate();
        }

        // Elige (por el usuario): avisa si cambia.
        public void Choose(int i)
        {
            if (_items.Count == 0) return;
            i = ((i % _items.Count) + _items.Count) % _items.Count;   // da la vuelta
            if (i == _sel) return;
            _sel = i; Fit(); Invalidate();
            Chosen?.Invoke();
        }

        static string Nice(TeleOption o) => o?.Name == null ? I18n.T("Original del tren") : Teleindicadores.Nice(o.Name);

        int LedWidth(TeleOption o)
        {
            if (o?.Name != null)
            {
                var th = o.Sample != null ? Teleindicadores.LedThumb(o.Sample, LedH) : null;
                if (th != null) return Math.Min(MaxLedW, th.Width);
            }
            return TextRenderer.MeasureText(Nice(o), _f).Width + 18;
        }

        void Fit()
        {
            int w = Arrow + 6 + LedWidth(Selected) + 22 + 6 + Arrow;
            if (Width != w) Width = w;
            _tip.SetToolTip(this, Selected == null ? "" : I18n.T("Teleindicador") + ": " + Nice(Selected) + "  ·  " + string.Format(I18n.T("{0} de {1}"), _sel + 1, _items.Count));
        }

        Rectangle PrevR => new Rectangle(0, (Height - Arrow) / 2, Arrow, Arrow);
        Rectangle NextR => new Rectangle(Width - Arrow, (Height - Arrow) / 2, Arrow, Arrow);
        Rectangle LedR => new Rectangle(Arrow + 6, 0, Width - 2 * Arrow - 12, Height);
        int HitAt(Point p) => PrevR.Contains(p) ? 0 : NextR.Contains(p) ? 2 : LedR.Contains(p) ? 1 : -1;

        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); int h = HitAt(e.Location); if (h != _hover) { _hover = h; Invalidate(); } }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (_hover != -1) { _hover = -1; Invalidate(); } }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || _items.Count == 0) return;
            switch (HitAt(e.Location))
            {
                case 0: Choose(_sel - 1); break;
                case 2: Choose(_sel + 1); break;
                case 1: OpenGallery(); break;
            }
        }
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (_items.Count == 0 || e.Delta == 0) return;
            if (e is HandledMouseEventArgs h) h.Handled = true;
            Choose(_sel + (e.Delta > 0 ? -1 : 1));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            using (var bg = new SolidBrush(Theme.ResolveBg(this))) g.FillRectangle(bg, ClientRectangle);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            void ArrowBtn(Rectangle r, bool left, bool hot)
            {
                Theme.FillRound(g, r, 7, hot ? Theme.SurfaceHi : Theme.Surface2);
                using var p = new Pen(Theme.Text, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f, d = left ? 2.5f : -2.5f;
                g.DrawLines(p, new[] { new PointF(cx + d, cy - 5), new PointF(cx - d, cy), new PointF(cx + d, cy + 5) });
            }
            ArrowBtn(PrevR, true, _hover == 0);
            ArrowBtn(NextR, false, _hover == 2);
            var lr = LedR;
            var box = new Rectangle(lr.X, (Height - (LedH + 6)) / 2, lr.Width, LedH + 6);
            Theme.FillRound(g, box, 7, _hover == 1 ? Theme.SurfaceHi : Theme.Surface2);
            var o = Selected;
            var th = o?.Name != null && o.Sample != null ? Teleindicadores.LedThumb(o.Sample, LedH) : null;
            var img = new Rectangle(box.X + 3, box.Y + 3, box.Width - 6 - 18, LedH);
            if (th != null)
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                // entero: si no cabe a lo ancho, se encoge (sin cortar el texto del cartel)
                double k = Math.Min(1.0, img.Width / (double)th.Width);
                int w = (int)(th.Width * k), h = (int)(th.Height * k);
                g.DrawImage(th, new Rectangle(img.X, img.Y + (LedH - h) / 2, w, h));
            }
            else if (o != null)
                TextRenderer.DrawText(g, Nice(o), _f, new Rectangle(img.X + 6, img.Y, img.Width - 6, img.Height), Theme.Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            using (var p = new Pen(Theme.Subtle, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                float cx = box.Right - 11, cy = box.Y + box.Height / 2f;
                g.DrawLines(p, new[] { new PointF(cx - 4, cy - 2), new PointF(cx, cy + 2), new PointF(cx + 4, cy - 2) });
            }
        }

        // ---------------- galería ----------------
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        public void OpenGallery()
        {
            try { _pop?.Close(); _pop?.Dispose(); } catch { }
            // alto justo para todos los carteles (como mucho 420): con pocos destinos, sin hueco debajo
            int rowsAll = (_items.Count + 2) / 3;
            int bodyH = Math.Min(Theme.Px(420), Theme.Px(34) + Theme.Px(24) + rowsAll * (Theme.Px(46) + Theme.Px(8)) + Theme.Px(8));
            var body = new Panel { BackColor = Theme.Surface, Size = new Size(Theme.Px(600), bodyH) };
            var search = new RoundedInput(I18n.T("Buscar destino…")) { Dock = DockStyle.Top, Height = Theme.Px(34) };
            var count = new Label { Dock = DockStyle.Top, Height = Theme.Px(24), ForeColor = Theme.Subtle, Font = Theme.Font(8.5f), TextAlign = ContentAlignment.MiddleLeft, BackColor = Theme.Surface };
            var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Surface };
            var grid = new Grid(this) { Location = Point.Empty, BackColor = Theme.Surface };
            scroll.Controls.Add(grid);
            body.Controls.Add(scroll); body.Controls.Add(count); body.Controls.Add(search);
            void Relayout()
            {
                grid.Filter(search.Box.Text);
                grid.Width = scroll.ClientSize.Width - (grid.NeedsScroll(scroll.ClientSize.Width, scroll.ClientSize.Height) ? SystemInformation.VerticalScrollBarWidth : 0);
                grid.Height = Math.Max(scroll.ClientSize.Height, grid.ContentHeight(grid.Width));
                count.Text = string.Format(I18n.T(grid.Count == 1 ? "{0} destino" : "{0} destinos"), Math.Max(0, grid.Count - (grid.HasOriginal ? 1 : 0)))
                             + "  ·  " + I18n.T("Clic para elegir · Esc para cerrar");
                grid.Invalidate();
            }
            search.Box.TextChanged += (s, e) => { scroll.AutoScrollPosition = Point.Empty; Relayout(); };
            search.Box.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter && grid.Count > 0) { e.SuppressKeyPress = true; grid.ChooseVisible(0); }
                else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; _pop?.Close(); }
            };
            var host = new ToolStripControlHost(body) { AutoSize = false, Size = body.Size, Margin = Padding.Empty, Padding = Padding.Empty };
            var pop = new ToolStripDropDown { Padding = new Padding(Theme.Px(10)), BackColor = Theme.Surface, Renderer = new PopRenderer(), DropShadowEnabled = true, AutoClose = true };
            pop.Items.Add(host);
            pop.HandleCreated += (s, e) => { try { int v = 2; DwmSetWindowAttribute(pop.Handle, 33, ref v, 4); } catch { } };
            _pop = pop;
            grid.Picked += i => { _pop?.Close(); Choose(i); };
            pop.Opened += (s, e) => { Relayout(); grid.ScrollToSelected(scroll); try { Native.UseDarkScrollBars(scroll); } catch { } search.Box.Focus(); };
            pop.Show(this, new Point(0, Height + 4));
        }

        sealed class PopRenderer : ToolStripProfessionalRenderer
        {
            protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) { using var b = new SolidBrush(Theme.Surface); e.Graphics.FillRectangle(b, e.AffectedBounds); }
            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) { using var p = new Pen(Theme.Surface2); e.Graphics.DrawRectangle(p, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1); }
        }

        // Cuadrícula de carteles (3 columnas): el original primero y el elegido en verde con ✓.
        sealed class Grid : Control
        {
            readonly TeleSelector _o;
            readonly List<int> _shown = new();
            int _hot = -1;
            const int Cols = 3;
            static int TileH => Theme.Px(46);
            static int Gap => Theme.Px(8);
            readonly Font _f = Theme.Font(9f, FontStyle.Bold), _fs = Theme.Font(8f);
            public event Action<int> Picked;
            public int Count => _shown.Count;
            public bool HasOriginal => _shown.Count > 0 && _o._items[_shown[0]].Name == null;

            public Grid(TeleSelector o)
            {
                _o = o;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                Cursor = Cursors.Hand;
            }
            protected override void Dispose(bool d) { if (d) { _f.Dispose(); _fs.Dispose(); } base.Dispose(d); }

            public void Filter(string q)
            {
                q = (q ?? "").Trim();
                _shown.Clear();
                for (int i = 0; i < _o._items.Count; i++)
                {
                    var it = _o._items[i];
                    if (q.Length == 0 || Nice(it).IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0
                        || (it.Name ?? "").IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0)
                        _shown.Add(i);
                }
                _hot = -1;
            }
            public int ContentHeight(int w) { int rows = (_shown.Count + Cols - 1) / Cols; return rows * (TileH + Gap) + Gap; }
            public bool NeedsScroll(int w, int h) => ContentHeight(w) > h;
            public void ChooseVisible(int k) { if (k >= 0 && k < _shown.Count) Picked?.Invoke(_shown[k]); }
            public void ScrollToSelected(ScrollableControl sc)
            {
                int k = _shown.IndexOf(_o._sel);
                if (k < 0) return;
                int y = Gap + (k / Cols) * (TileH + Gap);
                if (y + TileH > sc.ClientSize.Height) sc.AutoScrollPosition = new Point(0, y - sc.ClientSize.Height / 2);
            }

            Rectangle TileR(int k)
            {
                int tw = (Width - Gap * (Cols + 1)) / Cols;
                return new Rectangle(Gap + (k % Cols) * (tw + Gap), Gap + (k / Cols) * (TileH + Gap), tw, TileH);
            }
            int HitAt(Point p) { for (int k = 0; k < _shown.Count; k++) if (TileR(k).Contains(p)) return k; return -1; }
            protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); int h = HitAt(e.Location); if (h != _hot) { _hot = h; Invalidate(); } }
            protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (_hot != -1) { _hot = -1; Invalidate(); } }
            protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); int h = HitAt(e.Location); if (e.Button == MouseButtons.Left && h >= 0) Picked?.Invoke(_shown[h]); }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics; g.Clear(BackColor); g.SmoothingMode = SmoothingMode.AntiAlias;
                if (_shown.Count == 0)
                {
                    TextRenderer.DrawText(g, I18n.T("Ningún destino coincide con la búsqueda."), _fs, new Rectangle(0, Gap, Width, Theme.Px(40)), Theme.Subtle, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    return;
                }
                for (int k = 0; k < _shown.Count; k++)
                {
                    var r = TileR(k);
                    if (!e.ClipRectangle.IntersectsWith(r)) continue;
                    int i = _shown[k]; var it = _o._items[i];
                    bool sel = i == _o._sel, hot = k == _hot;
                    Theme.FillRound(g, r, 8, sel ? Color.FromArgb(44, 62, 48) : hot ? Theme.SurfaceHi : Theme.Surface2);
                    if (sel) Theme.DrawRoundBorder(g, r, 8, Theme.Accent, 1.6f);
                    var th = it.Name != null && it.Sample != null ? Teleindicadores.LedThumb(it.Sample, LedH) : null;
                    int right = sel ? 22 : 8;
                    if (th != null)
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        int maxW = r.Width - 16 - right;
                        double k2 = Math.Min(1.0, maxW / (double)th.Width);
                        int w = (int)(th.Width * k2), h = (int)(th.Height * k2);
                        g.DrawImage(th, new Rectangle(r.X + 8, r.Y + (r.Height - h) / 2, w, h));
                    }
                    else
                        TextRenderer.DrawText(g, Nice(it), _f, new Rectangle(r.X + 10, r.Y, r.Width - 10 - right, r.Height), Theme.Text,
                            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                    if (sel)
                        using (var p = new Pen(Color.FromArgb(143, 224, 147), 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                        {
                            float cx = r.Right - 13, cy = r.Y + r.Height / 2f;
                            g.DrawLines(p, new[] { new PointF(cx - 5, cy), new PointF(cx - 1.5f, cy + 3.5f), new PointF(cx + 5, cy - 4) });
                        }
                }
            }
        }
    }
}
