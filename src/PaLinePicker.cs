// Lista de líneas de megafonía que sale del HUD al pulsar sobre la línea actual.
// Es una ventana propia, sin bordes y con WS_EX_NOACTIVATE, para NO robarle el foco a Open Rails:
// se puede pulsar una línea mientras el simulador sigue teniendo el teclado.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SelectOR
{
    public class PaLinePicker : Form
    {
        readonly List<(string id, string name)> _items = new List<(string, string)>();
        readonly string _current;
        readonly Action<string> _pick;
        readonly Action _off;            // desconectar la megafonía del todo
        int _offIdx = -1;
        int _hoverIdx = -1;
        const int RowH = 26, PadV = 6;

        public PaLinePicker(List<(string id, string name)> lines, string currentName, Action<string> pick, Action off = null)
        {
            _current = currentName; _pick = pick; _off = off;
            _items.Add((null, I18n.T("Sin línea (voz base)")));
            if (lines != null) foreach (var l in lines) _items.Add(l);
            if (_off != null) { _offIdx = _items.Count; _items.Add((null, I18n.T("Desconectar megafonía"))); }

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false; TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Theme.Surface;
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
            Opacity = 0.95;
        }

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

        public void ShowAt(int x, int y, int minWidth)
        {
            int w = Math.Max(minWidth, 180);
            using (var f = Theme.Font(9f))
                foreach (var it in _items)
                    w = Math.Max(w, TextRenderer.MeasureText(it.name, f).Width + 44);
            int h = _items.Count * RowH + PadV * 2;
            var wa = Screen.FromPoint(new Point(x, y)).WorkingArea;
            if (y + h > wa.Bottom) y = Math.Max(wa.Top, y - h - 8);          // no salirse por abajo
            if (x + w > wa.Right) x = Math.Max(wa.Left, wa.Right - w - 4);
            Bounds = new Rectangle(x, y, w, h);
            Region = new Region(Round(new Rectangle(0, 0, w, h), 10));
            Show();
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

        int IndexAt(Point p)
        {
            int i = (p.Y - PadV) / RowH;
            return (p.Y < PadV || i < 0 || i >= _items.Count) ? -1 : i;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int i = IndexAt(e.Location);
            if (i != _hoverIdx) { _hoverIdx = i; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hoverIdx = -1; Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            int i = IndexAt(e.Location);
            if (i >= 0)
            {
                try { if (i == _offIdx) _off?.Invoke(); else _pick?.Invoke(_items[i].id); } catch { }
            }
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

            for (int i = 0; i < _items.Count; i++)
            {
                var r = new Rectangle(4, PadV + i * RowH, Width - 8, RowH);
                bool sel = i != _offIdx
                           && (string.Equals(_items[i].name, _current, StringComparison.Ordinal)
                               || (i == 0 && string.IsNullOrEmpty(_current)));
                if (i == _hoverIdx)
                    using (var hb = new SolidBrush(Color.FromArgb(52, 58, 64)))
                        using (var path = Round(r, 7)) g.FillPath(hb, path);
                if (sel)
                    using (var mb = new SolidBrush(Color.FromArgb(94, 190, 155)))
                        g.FillRectangle(mb, r.Left + 2, r.Top + 7, 3, r.Height - 14);
                TextRenderer.DrawText(g, _items[i].name, Theme.Font(9f, sel ? FontStyle.Bold : FontStyle.Regular),
                    new Rectangle(r.Left + 12, r.Top, r.Width - 16, r.Height),
                    i == _offIdx ? Theme.Subtle : sel ? Color.FromArgb(224, 232, 228) : Theme.Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            }
        }
    }
}
