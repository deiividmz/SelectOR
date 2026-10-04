// Normas internas de una empresa: una tarjeta por norma (número, título, categoría, texto completo y
// quién la publicó o la editó). Las tarjetas miden lo que ocupa su texto. Un clic la elige (para
// editarla, moverla o borrarla).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SelectOR
{
    public sealed class RuleItem
    {
        public string Id = "", Title = "", Body = "", Category = "general", By = "", EditedBy = "", When = "", EditedWhen = "";
        public int Number;
        internal int Y, H;
    }

    public sealed class RulesView : Panel
    {
        public static readonly (string key, string label, Color color)[] Categories =
        {
            ("general", "General", Color.FromArgb(150, 160, 170)),
            ("conduccion", "Conducción", Color.FromArgb(120, 144, 226)),
            ("servicio", "Servicio", Color.FromArgb(45, 212, 191)),
            ("seguridad", "Seguridad", Color.FromArgb(229, 115, 115)),
            ("convivencia", "Convivencia", Color.FromArgb(102, 197, 106)),
            ("sanciones", "Sanciones", Color.FromArgb(251, 146, 60)),
        };
        public static (string label, Color color) Cat(string key)
        {
            foreach (var c in Categories) if (c.key == key) return (I18n.T(c.label), c.color);
            return (I18n.T("General"), Categories[0].color);
        }

        readonly List<RuleItem> _items = new List<RuleItem>();
        int _total, _hover = -1;
        public string SelectedId { get; private set; }
        public event Action SelectionChanged;
        public string EmptyText = "";
        readonly Font _fNum = Theme.Font(15f, FontStyle.Bold), _fTitle = Theme.Font(11f, FontStyle.Bold), _fBody = Theme.Font(10f),
                      _fSmall = Theme.Font(8.5f), _fPill = Theme.Font(8.25f, FontStyle.Bold), _fMsg = Theme.Font(10f);
        const TextFormatFlags Wrap = TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix;
        const TextFormatFlags One = TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;

        public RulesView()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            AutoScroll = true; BackColor = Theme.Bg;
        }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Native.UseDarkScrollBars(this); }
        protected override void Dispose(bool disposing)
        {
            if (disposing) foreach (var f in new[] { _fNum, _fTitle, _fBody, _fSmall, _fPill, _fMsg }) f.Dispose();
            base.Dispose(disposing);
        }

        public void SetItems(IEnumerable<RuleItem> items)
        {
            _items.Clear(); _items.AddRange(items);
            if (SelectedId != null && !_items.Exists(i => i.Id == SelectedId)) { SelectedId = null; SelectionChanged?.Invoke(); }
            Relayout();
        }

        public RuleItem Selected => _items.Find(i => i.Id == SelectedId);

        int NumW => Theme.Px(54);
        int TextW => Math.Max(100, ClientSize.Width - Theme.Px(4) - NumW - Theme.Px(40));

        void Relayout()
        {
            int y = Theme.Px(2);
            using (var g = CreateGraphics())
                foreach (var it in _items)
                {
                    int bodyH = it.Body.Length == 0 ? 0 : TextRenderer.MeasureText(g, it.Body, _fBody, new Size(TextW, 100000), Wrap).Height;
                    it.Y = y; it.H = Theme.Px(14) + Theme.Px(26) + (bodyH > 0 ? Theme.Px(6) + bodyH : 0) + Theme.Px(8) + Theme.Px(18) + Theme.Px(12);
                    y += it.H + Theme.Px(8);
                }
            _total = y;
            AutoScrollMinSize = new Size(0, _items.Count == 0 ? 0 : _total);
            Invalidate();
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); if (IsHandleCreated) Relayout(); }
        protected override void OnScroll(ScrollEventArgs se) { base.OnScroll(se); Invalidate(); }
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            int max = Math.Max(0, _total - ClientSize.Height);
            int y = Math.Max(0, Math.Min(max, -AutoScrollPosition.Y - e.Delta * Theme.Px(120) / 120));
            AutoScrollPosition = new Point(0, y); Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(BackColor);
            if (_items.Count == 0)
            {
                TextRenderer.DrawText(g, EmptyText, _fMsg, new Rectangle(Theme.Px(20), Theme.Px(30), ClientSize.Width - Theme.Px(40), Theme.Px(80)), Theme.Subtle,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak);
                return;
            }
            int top = -AutoScrollPosition.Y, w = ClientSize.Width - Theme.Px(4);
            for (int i = 0; i < _items.Count; i++)
            {
                var it = _items[i];
                if (it.Y + it.H < top || it.Y > top + ClientSize.Height) continue;
                var rc = new Rectangle(Theme.Px(2), it.Y - top, w - Theme.Px(2), it.H);
                bool sel = it.Id == SelectedId, hov = i == _hover;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var b = new SolidBrush(sel || hov ? Color.FromArgb(50, 54, 57) : Theme.Surface)) using (var p = Theme.Round(rc, Theme.Px(10))) g.FillPath(b, p);
                if (sel) using (var pen = new Pen(Theme.Accent, 1.5f)) using (var p = Theme.Round(rc, Theme.Px(10))) g.DrawPath(pen, p);
                g.SmoothingMode = SmoothingMode.None;
                var (cl, cc) = Cat(it.Category);
                using (var sb = new SolidBrush(cc)) g.FillRectangle(sb, rc.X, rc.Y + Theme.Px(12), Theme.Px(3), rc.Height - Theme.Px(24));

                int x = rc.X + Theme.Px(16), y = rc.Y + Theme.Px(14);
                TextRenderer.DrawText(g, it.Number + ".", _fNum, new Rectangle(x, y - Theme.Px(2), NumW, Theme.Px(30)), Theme.AccentHi, One);
                int tx = x + NumW, tw = TextW;
                // categoría a la derecha del título
                int pw = TextRenderer.MeasureText(g, cl, _fPill).Width + Theme.Px(14), ph = Theme.Px(20);
                var pill = new Rectangle(rc.Right - Theme.Px(16) - pw, y + Theme.Px(3), pw, ph);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var pb = new SolidBrush(Color.FromArgb(45, cc))) using (var pp = Theme.Round(pill, ph / 2)) g.FillPath(pb, pp);
                g.SmoothingMode = SmoothingMode.None;
                TextRenderer.DrawText(g, cl, _fPill, pill, cc, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, it.Title, _fTitle, new Rectangle(tx, y, pill.Left - tx - Theme.Px(10), Theme.Px(26)), Theme.Text, One);
                y += Theme.Px(26);
                if (it.Body.Length > 0)
                {
                    y += Theme.Px(6);
                    var br = new Rectangle(tx, y, tw, 100000);
                    int bh = TextRenderer.MeasureText(g, it.Body, _fBody, new Size(tw, 100000), Wrap).Height;
                    TextRenderer.DrawText(g, it.Body, _fBody, new Rectangle(tx, y, tw, bh), Color.FromArgb(214, 218, 222), Wrap);
                    y += bh;
                }
                y += Theme.Px(8);
                string foot = string.Format(I18n.T("Publicada por {0} · {1}"), it.By.Length > 0 ? it.By : "—", it.When)
                              + (it.EditedWhen.Length > 0 && it.EditedWhen != it.When ? "   ·   " + string.Format(I18n.T("editada por {0} · {1}"), it.EditedBy.Length > 0 ? it.EditedBy : "—", it.EditedWhen) : "");
                TextRenderer.DrawText(g, foot, _fSmall, new Rectangle(tx, y, tw, Theme.Px(18)), Theme.Subtle, One);
            }
        }

        int IndexAt(Point p)
        {
            int y = p.Y - AutoScrollPosition.Y;
            for (int i = 0; i < _items.Count; i++) if (y >= _items[i].Y && y < _items[i].Y + _items[i].H) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int i = IndexAt(e.Location);
            Cursor = i >= 0 ? Cursors.Hand : Cursors.Default;
            if (i != _hover) { _hover = i; Invalidate(); }
        }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (_hover >= 0) { _hover = -1; Invalidate(); } }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e); Focus();
            int i = IndexAt(e.Location);
            string id = i >= 0 ? _items[i].Id : null;
            if (id != SelectedId) { SelectedId = id; Invalidate(); SelectionChanged?.Invoke(); }
        }

        public void Select(string id)
        {
            SelectedId = id; Invalidate(); SelectionChanged?.Invoke();
            var it = _items.Find(x => x.Id == id);
            if (it == null) return;
            int top = -AutoScrollPosition.Y;
            if (it.Y < top || it.Y + it.H > top + ClientSize.Height) AutoScrollPosition = new Point(0, Math.Max(0, it.Y - Theme.Px(10)));
        }
    }
}
