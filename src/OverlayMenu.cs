// Menú para los HUD que van sobre Open Rails (pupitre, chat del HUD…). Un menú de Windows (ContextMenuStrip) se cierra
// solo en cuanto SelectOR no es la ventana activa, y los HUD no le quitan el foco a Open Rails (WS_EX_NOACTIVATE): el
// menú se abría y se cerraba sin verse. Este es una ventanita propia que tampoco toma el foco, con el mismo aspecto
// que los demás menús de SelectOR (MenuStyle): fondo oscuro, filas holgadas, resaltado redondeado, casillas en verde
// y esquinas redondeadas. Se cierra al elegir una opción o al pulsar fuera de él.
//  · Item.Then: en vez de hacer algo, cambia el menú por otro (p. ej. «Eliminar mensaje» → «Sí, eliminarlo»), porque
//    un submenú o un diálogo quedarían detrás del simulador.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SelectOR
{
    public sealed class OverlayMenu : Form
    {
        public sealed class Item
        {
            public string Text; public Color Color = Color.Empty;
            public bool Checked, Enabled = true, Separator;
            public Action Click;
            public List<Item> Then; public string ThenTitle;   // en vez de Click: otro menú en su lugar
            public static Item Sep() => new Item { Separator = true };
        }

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        List<Item> _items; string _title;
        readonly List<(Rectangle row, int item)> _rows = new();
        int _hover = -1;
        readonly Timer _watch;
        bool _checks;
        public static OverlayMenu Current { get; private set; }   // (el abierto: así las pruebas lo encuentran)

        int Pad => Theme.Px(14); int RowH => Theme.Px(34); int SepH => Theme.Px(11); int Edge => Theme.Px(6); int CheckW => Theme.Px(26);

        OverlayMenu(string title, List<Item> items)
        {
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Theme.Surface; DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
            Font = Theme.Font(9.75f);
            SetItems(title, items);
            // pulsar fuera (en Open Rails, en otro HUD…): se cierra
            _watch = new Timer { Interval = 60 };
            _watch.Tick += (s, e) =>
            {
                if (IsDisposed) return;
                if (Control.MouseButtons != MouseButtons.None && !Bounds.Contains(Cursor.Position)) Close();
            };
        }

        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x08000000 /*WS_EX_NOACTIVATE*/ | 0x00000080 /*WS_EX_TOOLWINDOW*/ | 0x00000008 /*WS_EX_TOPMOST*/;
                cp.ClassStyle |= 0x00020000; /*CS_DROPSHADOW*/
                return cp;
            }
        }

        // Abre el menú en ese punto de la pantalla (dentro de ella). Cierra el que hubiera abierto.
        public static OverlayMenu Show(Point screen, string title, List<Item> items)
        {
            try { Current?.Close(); } catch { }
            var m = new OverlayMenu(title, items);
            var wa = Screen.FromPoint(screen).WorkingArea;
            int x = Math.Min(screen.X, wa.Right - m.Width - 4), y = Math.Min(screen.Y, wa.Bottom - m.Height - 4);
            m.Location = new Point(Math.Max(wa.Left + 4, x), Math.Max(wa.Top + 4, y));
            m.Show();
            try { int v = 2; DwmSetWindowAttribute(m.Handle, 33, ref v, 4); } catch { }   // esquinas redondeadas (Windows 11)
            m._watch.Start();
            Current = m;
            return m;
        }

        void SetItems(string title, List<Item> items)
        {
            _title = title; _items = items ?? new List<Item>(); _hover = -1;
            _checks = _items.Exists(i => i.Checked);
            using var g = CreateGraphics();
            using var fT = Theme.Font(8.6f, FontStyle.Bold);
            int w = Theme.Px(190);
            if (!string.IsNullOrEmpty(_title)) w = Math.Max(w, TextRenderer.MeasureText(g, _title, fT).Width + 2 * Pad + 2 * Edge);
            foreach (var it in _items)
                if (!it.Separator && it.Text != null) w = Math.Max(w, TextRenderer.MeasureText(g, it.Text, Font).Width + 2 * Pad + 2 * Edge + (_checks ? CheckW : 0));
            int h = 2 * Edge + (string.IsNullOrEmpty(_title) ? 0 : Theme.Px(28) + SepH);
            foreach (var it in _items) h += it.Separator ? SepH : RowH;
            Size = new Size(w, h);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var b = new SolidBrush(Theme.Surface)) g.FillRectangle(b, ClientRectangle);
            using (var p = new Pen(Color.FromArgb(60, 255, 255, 255))) g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
            _rows.Clear();
            int y = Edge;
            using var sep = new Pen(Color.FromArgb(60, 255, 255, 255), 1f);
            if (!string.IsNullOrEmpty(_title))
            {
                using var fT = Theme.Font(8.6f, FontStyle.Bold);
                TextRenderer.DrawText(g, _title, fT, new Rectangle(Edge + Pad, y, Width - 2 * (Edge + Pad), Theme.Px(28)), Theme.Subtle,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                y += Theme.Px(28); g.DrawLine(sep, Edge + Pad, y + SepH / 2, Width - Edge - Pad, y + SepH / 2); y += SepH;
            }
            for (int i = 0; i < _items.Count; i++)
            {
                var it = _items[i];
                if (it.Separator) { g.DrawLine(sep, Edge + Pad, y + SepH / 2, Width - Edge - Pad, y + SepH / 2); y += SepH; continue; }
                var row = new Rectangle(Edge, y, Width - 2 * Edge, RowH);
                bool live = it.Enabled && (it.Click != null || it.Then != null);
                if (live && _hover == i)
                    using (var hp = Theme.Round(row, Theme.Px(7))) using (var hb = new SolidBrush(Color.FromArgb(50, 255, 255, 255))) g.FillPath(hb, hp);
                int tx = Edge + Pad;
                if (_checks)
                {
                    if (it.Checked)
                        using (var cp = new Pen(Theme.AccentHi, Theme.Px(2)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                        {
                            int cx = tx + Theme.Px(2), cy = y + RowH / 2;
                            g.DrawLines(cp, new[] { new Point(cx, cy), new Point(cx + Theme.Px(4), cy + Theme.Px(4)), new Point(cx + Theme.Px(11), cy - Theme.Px(4)) });
                        }
                    tx += CheckW;
                }
                var col = !it.Enabled ? Theme.Subtle : it.Color.IsEmpty ? Theme.Text : it.Color;
                TextRenderer.DrawText(g, it.Text ?? "", Font, new Rectangle(tx, y, Width - tx - Edge - Pad, RowH), col,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                if (live) _rows.Add((row, i));
                y += RowH;
            }
        }

        int RowAt(Point p) { foreach (var (r, i) in _rows) if (r.Contains(p)) return i; return -1; }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = RowAt(e.Location);
            if (h != _hover) { _hover = h; Invalidate(); }
            Cursor = h >= 0 ? Cursors.Hand : Cursors.Default;
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { if (_hover != -1) { _hover = -1; Invalidate(); } base.OnMouseLeave(e); }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            int i = RowAt(e.Location);
            if (i < 0) return;
            Choose(i);
        }

        // (también lo usan las pruebas)
        public void Choose(int i)
        {
            if (i < 0 || i >= _items.Count) return;
            var it = _items[i];
            if (!it.Enabled) return;
            if (it.Then != null)
            {
                var loc = Location;
                SetItems(it.ThenTitle, it.Then);
                var wa = Screen.FromPoint(loc).WorkingArea;
                Location = new Point(Math.Min(loc.X, wa.Right - Width - 4), Math.Min(loc.Y, wa.Bottom - Height - 4));
                return;
            }
            Close();
            try { it.Click?.Invoke(); } catch { }
        }

        public IReadOnlyList<Item> Items => _items;

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _watch.Stop(); _watch.Dispose();
            if (Current == this) Current = null;
            base.OnFormClosed(e);
        }
    }
}
