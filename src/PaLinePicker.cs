// Lista de líneas de megafonía que sale del HUD al pulsar sobre la línea actual.
// Es una ventana propia, sin bordes y con WS_EX_NOACTIVATE, para NO robarle el foco a Open Rails:
// se puede pulsar una línea mientras el simulador sigue teniendo el teclado.
// Con muchas líneas no cabe en la pantalla: la lista se desplaza (rueda o barra a la derecha) y «Desconectar
// megafonía» se queda fija al pie. Al abrirse, la línea elegida queda a la vista. Las filas solo se pintan dentro de
// la lista (el texto con PreserveGraphicsClipping: TextRenderer, si no, se salta el recorte).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SelectOR
{
    public class PaLinePicker : Form
    {
        readonly List<(string id, string name)> _items = new List<(string, string)>();   // la lista (sin «Desconectar»)
        readonly string _current;
        readonly Action<string> _pick;
        readonly Action _off;            // desconectar la megafonía del todo
        int _hoverIdx = -1;              // fila bajo el ratón (−1 ninguna; FooterIdx = «Desconectar»)
        const int RowH = 26, PadV = 6, BarW = 6, FooterSep = 7, FooterIdx = -2;
        int _scroll;                     // desplazamiento de la lista (px)
        bool _dragBar; int _dragY0, _dragScroll0;
        readonly Font _f = Theme.Font(9f), _fB = Theme.Font(9f, FontStyle.Bold);

        public PaLinePicker(List<(string id, string name)> lines, string currentName, Action<string> pick, Action off = null)
        {
            _current = currentName; _pick = pick; _off = off;
            _items.Add((null, I18n.T("Sin línea (voz base)")));
            if (lines != null) foreach (var l in lines) _items.Add(l);

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false; TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Theme.Surface;
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
            Opacity = 0.95;
        }

        protected override void Dispose(bool disposing) { if (disposing) { _f.Dispose(); _fB.Dispose(); } base.Dispose(disposing); }

        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x08000000 /*WS_EX_NOACTIVATE*/ | 0x00000080 /*WS_EX_TOOLWINDOW*/;
                return cp;
            }
        }

        bool HasFooter => _off != null;
        int FooterH => HasFooter ? RowH + FooterSep : 0;
        Rectangle ListRect => new Rectangle(0, PadV, Width, Height - PadV * 2 - FooterH);
        int ContentH => _items.Count * RowH;
        int MaxScroll => Math.Max(0, ContentH - ListRect.Height);
        bool Scrolls => MaxScroll > 0;

        public void ShowAt(int x, int y, int minWidth)
        {
            int w = Math.Max(minWidth, 180);
            foreach (var it in _items) w = Math.Max(w, TextRenderer.MeasureText(it.name, _f).Width + 44);
            if (HasFooter) w = Math.Max(w, TextRenderer.MeasureText(I18n.T("Desconectar megafonía"), _f).Width + 44);
            var wa = Screen.FromPoint(new Point(x, y)).WorkingArea;
            // como mucho media pantalla (conduciendo no debe taparlo todo): lo demás, desplazando la lista
            int full = Math.Min(ContentH + PadV * 2 + FooterH, Math.Max(RowH * 8 + PadV * 2 + FooterH, wa.Height / 2));
            // Hacia abajo si cabe; si no, donde haya más sitio (abajo o encima del HUD), y con la lista desplazable.
            int below = wa.Bottom - y - 6, above = y - 8 - wa.Top - 8;
            int h = full; bool up = false;
            if (full > below)
            {
                if (above > below) { h = Math.Min(full, above); up = true; }
                else h = Math.Max(RowH * 3 + PadV * 2 + FooterH, below);
            }
            h = Math.Min(h, full);
            // la lista con un número entero de filas: nunca media fila cortada arriba o abajo
            h = Math.Max(RowH, (h - PadV * 2 - FooterH) / RowH * RowH) + PadV * 2 + FooterH;
            if (up) y = y - h - 8;   // encima del HUD, pegada a él
            if (h < ContentH + PadV * 2 + FooterH) w += BarW + 6;   // sitio para la barra
            if (x + w > wa.Right) x = Math.Max(wa.Left, wa.Right - w - 4);
            Bounds = new Rectangle(x, Math.Max(wa.Top, y), w, h);
            Region = new Region(Round(new Rectangle(0, 0, w, h), 10));
            // la línea elegida, a la vista
            int sel = SelectedIndex();
            if (sel >= 0) _scroll = Snap(sel * RowH - (ListRect.Height - RowH) / 2);
            Show();
        }

        int SelectedIndex()
        {
            for (int i = 0; i < _items.Count; i++)
                if (string.Equals(_items[i].name, _current, StringComparison.Ordinal) || (i == 0 && string.IsNullOrEmpty(_current))) return i;
            return -1;
        }

        static GraphicsPath Round(Rectangle r, int rad)
        {
            var p = new GraphicsPath();
            int d = rad * 2;
            p.AddArc(r.Left, r.Top, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        Rectangle FooterRect => new Rectangle(4, Height - PadV - RowH, Width - 8, RowH);
        Rectangle BarTrack => Scrolls ? new Rectangle(Width - BarW - 5, ListRect.Top + 2, BarW, ListRect.Height - 4) : Rectangle.Empty;
        Rectangle BarThumb
        {
            get
            {
                var t = BarTrack; if (t.IsEmpty) return t;
                int th = Math.Max(24, (int)((long)t.Height * ListRect.Height / Math.Max(1, ContentH)));
                int ty = t.Top + (int)((long)(t.Height - th) * _scroll / Math.Max(1, MaxScroll));
                return new Rectangle(t.Left, ty, t.Width, th);
            }
        }

        int IndexAt(Point p)
        {
            if (HasFooter && FooterRect.Contains(p)) return FooterIdx;
            var lr = ListRect;
            if (!lr.Contains(p) || (Scrolls && p.X >= BarTrack.Left - 3)) return -1;
            int i = (p.Y - lr.Top + _scroll) / RowH;
            return i >= 0 && i < _items.Count ? i : -1;
        }

        // de fila en fila (la lista mide un número entero de filas, así que el final también cae en una)
        int Snap(int s) => Math.Max(0, Math.Min(MaxScroll, (int)Math.Round(s / (double)RowH) * RowH));

        void ScrollTo(int s)
        {
            s = Snap(s);
            if (s == _scroll) return;
            _scroll = s; Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            ScrollTo(_scroll - Math.Sign(e.Delta) * RowH * 3);
            _hoverIdx = IndexAt(PointToClient(Cursor.Position));
            base.OnMouseWheel(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (Scrolls && e.X >= BarTrack.Left - 3 && ListRect.Contains(e.Location))
            {
                var th = BarThumb;
                if (!th.Contains(e.Location))   // en la pista: salta hasta ahí
                    ScrollTo((int)((long)(e.Y - BarTrack.Top - th.Height / 2) * MaxScroll / Math.Max(1, BarTrack.Height - th.Height)));
                _dragBar = true; _dragY0 = e.Y; _dragScroll0 = _scroll;
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_dragBar)
            {
                var t = BarTrack; int th = BarThumb.Height;
                ScrollTo(_dragScroll0 + (int)((long)(e.Y - _dragY0) * MaxScroll / Math.Max(1, t.Height - th)));
            }
            else
            {
                int i = IndexAt(e.Location);
                if (i != _hoverIdx) { _hoverIdx = i; Invalidate(); }
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hoverIdx = -1; Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (_dragBar) { _dragBar = false; base.OnMouseUp(e); return; }   // se arrastraba la barra: la lista sigue abierta
            int i = IndexAt(e.Location);
            if (i == -1 && ListRect.Contains(e.Location)) { base.OnMouseUp(e); return; }   // hueco de la lista: nada
            if (i == FooterIdx) { try { _off?.Invoke(); } catch { } }
            else if (i >= 0) { try { _pick?.Invoke(_items[i].id); } catch { } }
            Close();
            base.OnMouseUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using (var bg = new SolidBrush(Theme.Surface)) g.FillRectangle(bg, ClientRectangle);
            using (var pen = new Pen(Color.FromArgb(70, 76, 82)))
                using (var path = Round(new Rectangle(0, 0, Width - 1, Height - 1), 10)) g.DrawPath(pen, path);

            int sel = SelectedIndex();
            var lr = ListRect;
            int textRight = Scrolls ? BarTrack.Left - 4 : Width - 4;
            var st = g.Save();
            g.SetClip(lr);
            int first = Math.Max(0, _scroll / RowH), last = Math.Min(_items.Count - 1, (_scroll + lr.Height) / RowH);
            for (int i = first; i <= last; i++)
            {
                var r = new Rectangle(4, lr.Top + i * RowH - _scroll, textRight - 4, RowH);
                DrawRow(g, r, _items[i].name, i == _hoverIdx, i == sel, false);
            }
            g.Restore(st);

            // barra de desplazamiento
            if (Scrolls)
            {
                using (var tb = new SolidBrush(Color.FromArgb(40, 44, 48))) using (var p = Round(BarTrack, BarW / 2)) g.FillPath(tb, p);
                using (var hb = new SolidBrush(_dragBar ? Color.FromArgb(150, 160, 168) : Color.FromArgb(110, 118, 126))) using (var p = Round(BarThumb, BarW / 2)) g.FillPath(hb, p);
            }

            // pie fijo: «Desconectar megafonía»
            if (HasFooter)
            {
                int ys = Height - PadV - RowH - FooterSep / 2 - 1;
                using (var pen = new Pen(Color.FromArgb(62, 68, 74))) g.DrawLine(pen, 10, ys, Width - 10, ys);
                DrawRow(g, FooterRect, I18n.T("Desconectar megafonía"), _hoverIdx == FooterIdx, false, true);
            }
        }

        void DrawRow(Graphics g, Rectangle r, string text, bool hover, bool sel, bool off)
        {
            if (hover)
                using (var hb = new SolidBrush(Color.FromArgb(52, 58, 64)))
                    using (var path = Round(r, 7)) g.FillPath(hb, path);
            if (sel)
                using (var mb = new SolidBrush(Color.FromArgb(94, 190, 155)))
                    g.FillRectangle(mb, r.Left + 2, r.Top + 7, 3, r.Height - 14);
            TextRenderer.DrawText(g, text, sel ? _fB : _f,
                new Rectangle(r.Left + 12, r.Top, r.Width - 16, r.Height),
                off ? Theme.Subtle : sel ? Color.FromArgb(224, 232, 228) : Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.PreserveGraphicsClipping);
        }
    }
}
