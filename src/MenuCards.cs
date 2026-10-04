// Listas en tarjetas del menú (rutas, actividades, trenes, salidas de un horario). CardListBase imita lo
// que el resto del programa usaba del ListBox (Items, SelectedItem, SelectedIndex, BeginUpdate/EndUpdate,
// SelectedIndexChanged), así que cambiar una lista por tarjetas no toca la lógica. Todo se dibuja a mano,
// solo lo visible, en doble búfer.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace SelectOR
{
    public abstract class CardListBase : Panel
    {
        // --- lo que se usaba del ListBox ---
        public readonly List<object> Items = new List<object>();
        int _sel = -1, _updating;
        object _selBefore;
        public event EventHandler SelectedIndexChanged;
        public event Action<object> ItemActivated;          // doble clic o Intro
        public object SelectedItem { get => _sel >= 0 && _sel < Items.Count ? Items[_sel] : null; set => SelectedIndex = value == null ? -1 : Items.IndexOf(value); }
        public int SelectedIndex
        {
            get => _sel < Items.Count ? _sel : -1;
            set
            {
                int v = value >= -1 && value < Items.Count ? value : -1;
                if (v == _sel) return;
                _sel = v; EnsureVisible(v); Invalidate();
                SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        public int TopIndex
        {
            get { int row = Math.Max(0, ScrollY / Math.Max(1, CardH + Gap)); return Math.Min(Items.Count == 0 ? 0 : Items.Count - 1, row * Cols); }
            set { int row = Math.Max(0, value) / Math.Max(1, Cols); AutoScrollPosition = new Point(0, row * (CardH + Gap)); Invalidate(); }
        }
        // Selección múltiple (opcional): Ctrl+clic marca o desmarca, Mayús+clic marca un tramo. Sin marcas,
        // la «selección» es la de siempre (SelectedItem).
        public bool MultiSelect;
        readonly HashSet<object> _marked = new HashSet<object>();
        int _anchor = -1;
        public int MarkedCount => _marked.Count;
        public bool IsMarked(object o) => o != null && _marked.Contains(o);
        // Lo elegido, en el orden de la lista: las marcadas o, si no hay, la seleccionada.
        public List<object> SelectedItems
        {
            get
            {
                var l = new List<object>();
                if (_marked.Count == 0) { if (SelectedItem != null) l.Add(SelectedItem); return l; }
                foreach (var o in Items) if (_marked.Contains(o)) l.Add(o);
                return l;
            }
        }
        public event EventHandler MarksChanged;
        public void ClearMarks() { if (_marked.Count == 0) return; _marked.Clear(); Invalidate(); MarksChanged?.Invoke(this, EventArgs.Empty); }

        public void BeginUpdate() { if (_updating++ == 0) _selBefore = SelectedItem; }
        public void EndUpdate()
        {
            if (--_updating > 0) return;
            _updating = 0;
            // Como el ListBox: si el elegido ya no está, se queda sin elección (y se avisa); si sigue, se mantiene.
            int idx = _selBefore == null ? -1 : Items.IndexOf(_selBefore);
            bool lost = _selBefore != null && idx < 0;
            _sel = idx; _selBefore = null;
            if (_marked.Count > 0) { var keep = new HashSet<object>(Items); int n = _marked.RemoveWhere(o => !keep.Contains(o)); if (n > 0) MarksChanged?.Invoke(this, EventArgs.Empty); }
            Relayout();
            if (lost) SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }

        public string EmptyText;
        protected int Hover = -1;
        int _total;
        protected readonly Font FMsg = Theme.Font(10f);

        protected CardListBase()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            AutoScroll = true; TabStop = true; BackColor = Theme.Bg;
        }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Native.UseDarkScrollBars(this); }
        protected override void Dispose(bool disposing) { if (disposing) FMsg.Dispose(); base.Dispose(disposing); }

        // --- rejilla ---
        protected virtual int MinCardW => Theme.Px(260);
        protected virtual int MaxCols => 99;
        protected abstract int CardH { get; }
        protected virtual int Gap => Theme.Px(10);
        protected virtual int TopOffset => 0;          // sitio fijo arriba (p. ej. la cabecera del panel de salidas)
        protected int Cols => Math.Max(1, Math.Min(MaxCols, (ClientSize.Width - Theme.Px(4) + Gap) / (MinCardW + Gap)));
        protected int CardW => Math.Max(10, (ClientSize.Width - Theme.Px(4) - Gap * (Cols - 1)) / Cols);
        int RowH => CardH + Gap;
        protected int ScrollY => -AutoScrollPosition.Y;

        public void Relayout()
        {
            int rows = (Items.Count + Cols - 1) / Cols;
            _total = rows * RowH + Theme.Px(4) + TopOffset;
            AutoScrollMinSize = new Size(0, Items.Count == 0 ? 0 : _total);
            Invalidate();
        }
        protected override void OnResize(EventArgs e) { base.OnResize(e); Relayout(); }
        protected override void OnScroll(ScrollEventArgs se) { base.OnScroll(se); Invalidate(); }
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            int max = Math.Max(0, _total - ClientSize.Height);
            int y = Math.Max(0, Math.Min(max, ScrollY - e.Delta * Theme.Px(120) / 120));
            if (y != ScrollY) { AutoScrollPosition = new Point(0, y); Invalidate(); }
        }

        protected Rectangle CardRect(int i)
        {
            int c = i % Cols, r = i / Cols;
            return new Rectangle(Theme.Px(2) + c * (CardW + Gap), Theme.Px(2) + TopOffset + r * RowH - ScrollY, CardW, CardH);
        }

        void EnsureVisible(int i)
        {
            if (i < 0 || !IsHandleCreated) return;
            int y = (i / Cols) * RowH, top = ScrollY;
            if (TopOffset > 0) { if (y < top) AutoScrollPosition = new Point(0, y); else if (y + RowH + TopOffset > top + ClientSize.Height) AutoScrollPosition = new Point(0, y + RowH + TopOffset - ClientSize.Height + Theme.Px(4)); return; }
            if (y < top) AutoScrollPosition = new Point(0, y);
            else if (y + RowH > top + ClientSize.Height) AutoScrollPosition = new Point(0, y + RowH - ClientSize.Height + Theme.Px(4));
        }

        protected abstract void PaintCard(Graphics g, int i, Rectangle rc, bool selected, bool hover);
        protected virtual void AfterPaint(Graphics g, int first, int last) { }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            if (Items.Count == 0)
            {
                if (!string.IsNullOrEmpty(EmptyText))
                    TextRenderer.DrawText(g, EmptyText, FMsg, new Rectangle(Theme.Px(10), Theme.Px(28), ClientSize.Width - Theme.Px(20), Theme.Px(60)), Theme.Subtle, TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak);
                return;
            }
            int first = Math.Max(0, (ScrollY / RowH) * Cols), last = Math.Min(Items.Count - 1, ((ScrollY + ClientSize.Height) / RowH + 2) * Cols - 1);
            for (int i = first; i <= last; i++) PaintCard(g, i, CardRect(i), _marked.Count > 0 ? _marked.Contains(Items[i]) : i == _sel, i == Hover);
            AfterPaint(g, first, last);
        }

        protected int IndexAt(Point p)
        {
            for (int i = Math.Max(0, (ScrollY / Math.Max(1, RowH)) * Cols); i < Items.Count; i++)
            {
                var r = CardRect(i);
                if (r.Y > ClientSize.Height) break;
                if (r.Contains(p)) return i;
            }
            return -1;
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int i = IndexAt(e.Location);
            Cursor = i >= 0 ? Cursors.Hand : Cursors.Default;
            if (i != Hover) { Hover = i; Invalidate(); }
        }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (Hover >= 0) { Hover = -1; Invalidate(); } }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            int i = IndexAt(e.Location);
            if (i >= 0 && MultiSelect)
            {
                var mk = ModifierKeys;
                if (e.Button == MouseButtons.Left && (mk & Keys.Control) != 0)
                {
                    if (_marked.Count == 0 && _sel >= 0 && _sel != i) _marked.Add(Items[_sel]);
                    if (!_marked.Remove(Items[i])) _marked.Add(Items[i]);
                    _anchor = i;
                    MarksChanged?.Invoke(this, EventArgs.Empty);
                    if (_marked.Contains(Items[i])) SelectedIndex = i; else Invalidate();
                    base.OnMouseDown(e);
                    return;
                }
                if (e.Button == MouseButtons.Left && (mk & Keys.Shift) != 0)
                {
                    int a = _anchor >= 0 && _anchor < Items.Count ? _anchor : Math.Max(0, _sel);
                    _marked.Clear();
                    for (int k = Math.Min(a, i); k <= Math.Max(a, i); k++) _marked.Add(Items[k]);
                    MarksChanged?.Invoke(this, EventArgs.Empty);
                    SelectedIndex = i; Invalidate();
                    base.OnMouseDown(e);
                    return;
                }
                // Botón derecho sobre una marcada: el menú actúa sobre todas las marcadas.
                if (!(e.Button == MouseButtons.Right && _marked.Contains(Items[i]))) ClearMarks();
                _anchor = i;
            }
            if (i >= 0) SelectedIndex = i;      // también con el botón derecho: el menú contextual actúa sobre ella
            base.OnMouseDown(e);
        }
        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            int i = IndexAt(e.Location);
            if (i >= 0 && e.Button == MouseButtons.Left) ItemActivated?.Invoke(Items[i]);
        }
        protected override bool IsInputKey(Keys k) => k == Keys.Up || k == Keys.Down || k == Keys.Left || k == Keys.Right || k == Keys.Home || k == Keys.End || k == Keys.Enter || base.IsInputKey(k);
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (Items.Count == 0) return;
            if (MultiSelect && e.KeyCode == Keys.Escape && _marked.Count > 0) { ClearMarks(); e.Handled = true; return; }
            if (MultiSelect && e.Control && e.KeyCode == Keys.A)
            {
                _marked.Clear(); foreach (var o in Items) _marked.Add(o);
                MarksChanged?.Invoke(this, EventArgs.Empty); Invalidate(); e.Handled = true; return;
            }
            int i = Math.Max(0, _sel);
            switch (e.KeyCode)
            {
                case Keys.Left: i = Math.Max(0, i - 1); break;
                case Keys.Right: i = Math.Min(Items.Count - 1, i + 1); break;
                case Keys.Up: i = Math.Max(0, i - Cols); break;
                case Keys.Down: i = Math.Min(Items.Count - 1, i + Cols); break;
                case Keys.Home: i = 0; break;
                case Keys.End: i = Items.Count - 1; break;
                case Keys.Enter: if (SelectedItem != null) ItemActivated?.Invoke(SelectedItem); e.Handled = true; return;
                default: return;
            }
            e.Handled = true; ClearMarks(); _anchor = i; SelectedIndex = i;
        }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        // --- ayudas de dibujo ---
        protected static void Fill(Graphics g, Rectangle r, int rad, Color c) { g.SmoothingMode = SmoothingMode.AntiAlias; Theme.FillRound(g, r, rad, c); g.SmoothingMode = SmoothingMode.None; }
        protected static void Stroke(Graphics g, Rectangle r, int rad, Color c, float w = 1.5f) { g.SmoothingMode = SmoothingMode.AntiAlias; Theme.DrawRoundBorder(g, r, rad, c, w); g.SmoothingMode = SmoothingMode.None; }
        protected const TextFormatFlags L1 = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
        protected const TextFormatFlags C1 = L1 | TextFormatFlags.HorizontalCenter;
        protected const TextFormatFlags R1 = L1 | TextFormatFlags.Right;
        // Anchos de texto recordados: las tarjetas y pastillas se repintan a menudo con los mismos textos.
        static readonly Dictionary<(string, Font), int> _tw = new Dictionary<(string, Font), int>();
        protected static int TW(string s, Font f)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            if (_tw.TryGetValue((s, f), out int w)) return w;
            w = TextRenderer.MeasureText(s, f, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
            if (_tw.Count > 20000) _tw.Clear();
            _tw[(s, f)] = w;
            return w;
        }
        protected bool ShowFocus => Focused && ShowFocusCues;

        // Imagen recortada para llenar el hueco (sin deformar), con esquinas redondeadas.
        // La imagen ya escalada se recuerda por tamaño: escalar con calidad en cada repintado era lo más caro.
        static readonly Dictionary<(Image, int, int), Bitmap> _cover = new Dictionary<(Image, int, int), Bitmap>();
        static readonly LinkedList<(Image, int, int)> _coverOrder = new LinkedList<(Image, int, int)>();
        protected static void Cover(Graphics g, Rectangle r, Image img, int rad, bool roundBottom = true)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = roundBottom ? Theme.Round(r, rad) : TopRound(r, rad);
            var st = g.Save(); g.SetClip(path, CombineMode.Intersect);
            var key = (img, r.Width, r.Height);
            if (!_cover.TryGetValue(key, out var bmp))
            {
                bmp = new Bitmap(Math.Max(1, r.Width), Math.Max(1, r.Height), System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                using (var gb = Graphics.FromImage(bmp))
                {
                    gb.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    gb.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    double k = Math.Max(r.Width / (double)img.Width, r.Height / (double)img.Height);
                    int w = (int)Math.Ceiling(img.Width * k), h = (int)Math.Ceiling(img.Height * k);
                    gb.DrawImage(img, (r.Width - w) / 2, (r.Height - h) / 2, w, h);
                }
                _cover[key] = bmp; _coverOrder.AddLast(key);
                while (_cover.Count > 120 && _coverOrder.First != null)
                {
                    var old = _coverOrder.First.Value; _coverOrder.RemoveFirst();
                    if (_cover.TryGetValue(old, out var ob)) { ob.Dispose(); _cover.Remove(old); }
                }
            }
            g.DrawImageUnscaled(bmp, r.X, r.Y);
            g.Restore(st);
            g.SmoothingMode = SmoothingMode.None;
        }
        protected static GraphicsPath TopRound(Rectangle r, int rad)
        {
            var p = new GraphicsPath(); int d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddLine(r.Right, r.Y + rad, r.Right, r.Bottom); p.AddLine(r.Right, r.Bottom, r.X, r.Bottom); p.CloseFigure();
            return p;
        }
        protected void Pill(Graphics g, ref int x, int y, int maxX, string t, Font f, Color fg, Color bg, int h)
        {
            int w = TW(t, f) + Theme.Px(14);
            if (x + w > maxX) { x = maxX + 1; return; }
            var r = new Rectangle(x, y, w, h);
            Fill(g, r, h / 2, bg);
            TextRenderer.DrawText(g, t, f, r, fg, C1);
            x += w + Theme.Px(5);
        }
    }

    // ------------------------------------------------------------------ RUTAS (lateral)
    public class RouteCardList : CardListBase
    {
        public Func<object, Image> ImageOf;
        public Func<object, string> TitleOf, SubOf;
        public Func<object, bool> FavOf;
        readonly Font _fT = Theme.Font(10f, FontStyle.Bold), _fS = Theme.Font(8.25f), _fStar = Theme.Font(12f, FontStyle.Bold);
        public RouteCardList() { BackColor = Theme.BgSidebar; }
        protected override void Dispose(bool disposing) { if (disposing) { _fT.Dispose(); _fS.Dispose(); _fStar.Dispose(); } base.Dispose(disposing); }
        protected override int MaxCols => 1;
        protected override int CardH => Theme.Px(116);
        protected override int Gap => Theme.Px(8);

        protected override void PaintCard(Graphics g, int i, Rectangle rc, bool sel, bool hov)
        {
            var it = Items[i];
            int rad = Theme.Px(10);
            Fill(g, rc, rad, sel ? Color.FromArgb(50, 62, 54) : hov ? Color.FromArgb(54, 58, 62) : Theme.Surface);
            var ir = new Rectangle(rc.X, rc.Y, rc.Width, Theme.Px(66));
            var img = ImageOf?.Invoke(it);
            if (img != null) Cover(g, ir, img, rad, roundBottom: false);
            else
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var p = TopRound(ir, rad)) using (var b = new LinearGradientBrush(ir, Color.FromArgb(70, 96, 76), Color.FromArgb(36, 46, 40), LinearGradientMode.ForwardDiagonal)) g.FillPath(b, p);
                g.SmoothingMode = SmoothingMode.None;
            }
            if (FavOf?.Invoke(it) == true)
            {
                var sr = new Rectangle(rc.Right - Theme.Px(30), rc.Y + Theme.Px(6), Theme.Px(24), Theme.Px(22));
                Fill(g, sr, Theme.Px(11), Color.FromArgb(150, 0, 0, 0));
                TextRenderer.DrawText(g, "★", _fStar, sr, Theme.Gold, C1);
            }
            int x = rc.X + Theme.Px(12), w = rc.Width - Theme.Px(24);
            TextRenderer.DrawText(g, TitleOf?.Invoke(it) ?? it.ToString(), _fT, new Rectangle(x, ir.Bottom + Theme.Px(7), w, Theme.Px(20)), Theme.Text, L1);
            TextRenderer.DrawText(g, SubOf?.Invoke(it) ?? "", _fS, new Rectangle(x, ir.Bottom + Theme.Px(28), w, Theme.Px(16)), Theme.Subtle, L1);
            if (sel) Stroke(g, rc, rad, Theme.Accent);
            else if (i == SelectedIndex && ShowFocus) Stroke(g, rc, rad, Theme.AccentHi, 1f);
        }
    }

    // ------------------------------------------------------------------ barra de abajo: lo elegido en pastillas
    // El texto sigue siendo «Ruta: X   ·   Tren: Y …» (lo escribe UpdateStatus); aquí cada trozo «Clave: valor»
    // se pinta como una pastilla con la clave pequeña. Lo demás (avisos, «Cargando…») va como texto normal.
    public class StatusPills : Label
    {
        static readonly Regex Sep = new Regex(@"\s{2,}·\s{2,}", RegexOptions.Compiled);
        static readonly Regex KeyVal = new Regex(@"^(.{1,28}?):\s+(.+)$", RegexOptions.Compiled);
        readonly Font _fK = Theme.Font(8f), _fV = Theme.Font(9.5f, FontStyle.Bold), _fT = Theme.Font(10f);
        public StatusPills() { SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true); }
        protected override void Dispose(bool disposing) { if (disposing) { _fK.Dispose(); _fV.Dispose(); _fT.Dispose(); } base.Dispose(disposing); }
        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            string text = Text ?? "";
            var parts = Sep.Split(text);
            int x = Padding.Left, maxX = ClientSize.Width - Theme.Px(8), h = Theme.Px(28), y = (ClientSize.Height - h) / 2;
            const TextFormatFlags F = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
            bool anyKv = false;
            foreach (var p in parts) if (KeyVal.IsMatch(p.Trim())) { anyKv = true; break; }
            if (!anyKv)
            {
                TextRenderer.DrawText(g, text, _fT, new Rectangle(x, 0, maxX - x, ClientSize.Height), ForeColor, F);
                return;
            }
            foreach (var raw in parts)
            {
                string p = raw.Trim(); if (p.Length == 0) continue;
                if (x >= maxX - Theme.Px(30)) break;
                var m = KeyVal.Match(p);
                string k = m.Success ? m.Groups[1].Value : null, v = m.Success ? m.Groups[2].Value : p;
                const TextFormatFlags M = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;   // medir sin elipsis
                int kw = k == null ? 0 : TextRenderer.MeasureText(g, k, _fK, Size.Empty, M).Width + Theme.Px(6);
                int vw = TextRenderer.MeasureText(g, v, _fV, Size.Empty, M).Width;
                int w = Math.Min(maxX - x, kw + vw + Theme.Px(22));
                var r = new Rectangle(x, y, w, h);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Theme.FillRound(g, r, h / 2, Theme.Surface2);
                g.SmoothingMode = SmoothingMode.None;
                int tx = r.X + Theme.Px(11);
                if (k != null) { TextRenderer.DrawText(g, k, _fK, new Rectangle(tx, r.Y, kw, h), Theme.Subtle, F); tx += kw; }
                TextRenderer.DrawText(g, v, _fV, new Rectangle(tx, r.Y, r.Right - tx - Theme.Px(10), h), Theme.Text, F);
                x += w + Theme.Px(8);
            }
        }
    }
}
