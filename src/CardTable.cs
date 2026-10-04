// Lista en tarjetas que sustituye a una tabla (StyledTable) sin cambiar el código que la usa: las mismas
// operaciones (AddRow, ClearRows, SetEmpty, BeginReload/EndReload, SelectedRow, SelectRow, Filter,
// ShowLoading, RowKey, RowFilter) y una plantilla que dice qué celda va en cada sitio de la tarjeta:
//
//   [icono/iniciales/✓]  TÍTULO                                   [pastilla]      VALOR GRANDE
//                        sub · sub · sub                                          detalle
//                        «cita»
//
// Solo se dibuja lo visible (CardListBase), así que va igual de rápida con muchas filas.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SelectOR
{
    public sealed class CardTable : CardListBase
    {
        public sealed class Row
        {
            public string[] Cells; public Color?[] Colors; public Image Image; public string Key; public int Orig;
            internal string Search;
        }

        // ---- plantilla ----
        public int TitleCol = 0;
        public int[] SubCols = Array.Empty<int>();       // segunda línea, separadas por «·»
        public int QuoteCol = -1;                        // tercera línea, entre comillas (si no está vacía)
        public int PillCol = -1;                         // pastilla a la derecha del título (con el color de su celda)
        public int RightCol = -1;                        // valor grande a la derecha
        public int[] RightSubCols = Array.Empty<int>();  // debajo del valor grande
        public int CheckCol = -1;                        // casilla (✓ si la celda no está vacía)
        public string Icon;                              // emoji en el círculo; null = iniciales del título (o el logotipo)
        public bool ShowAvatar = true;
        public readonly Dictionary<int, string> Formats = new Dictionary<int, string>();   // celda → «{0} parada|{0} paradas» (singular|plural)
        public readonly Dictionary<int, string> Badges = new Dictionary<int, string>();    // fila (orden de inserción) → distintivo
        public int CardHeight = 72, MinWidth = 420, Columns = 1;

        protected override int CardH => Theme.Px(CardHeight);
        protected override int MinCardW => Theme.Px(MinWidth);
        protected override int MaxCols => Columns;
        protected override int Gap => Theme.Px(8);

        // ---- lo que se usaba de la tabla ----
        public Func<string[], bool> RowFilter { get; set; }
        public Func<string[], string> RowKey;
        readonly List<Row> _rows = new List<Row>();
        string _filter = "", _empty, _ctx;
        bool _dirty, _scheduled;
        (string selKey, int top)? _reload;

        readonly Font _fTitle = Theme.Font(10f, FontStyle.Bold), _fSub = Theme.Font(8.75f), _fQuote = Theme.Font(8.75f, FontStyle.Italic),
                      _fRight = Theme.Font(11.5f, FontStyle.Bold), _fPill = Theme.Font(8f, FontStyle.Bold), _fAv = Theme.Font(9.5f, FontStyle.Bold),
                      _fIcon = Theme.Font(12f), _fCheck = Theme.Font(11f, FontStyle.Bold);

        public CardTable() { EmptyText = ""; }

        protected override void Dispose(bool disposing)
        {
            if (disposing) foreach (var f in new[] { _fTitle, _fSub, _fQuote, _fRight, _fPill, _fAv, _fIcon, _fCheck }) f.Dispose();
            base.Dispose(disposing);
        }

        public void ClearRows() { _rows.Clear(); Badges.Clear(); _empty = null; MarkDirty(); }

        public void AddRow(string[] cells, Color?[] colors = null) => AddRow(cells, colors, null, null);
        public void AddRow(string[] cells, Color?[] colors, Image image) => AddRow(cells, colors, image, null);
        public void AddRow(string[] cells, Color?[] colors, Image image, string key)
        {
            _empty = null;
            _rows.Add(new Row { Cells = cells ?? Array.Empty<string>(), Colors = colors, Image = image, Key = key, Orig = _rows.Count });
            MarkDirty();
        }

        public void SetEmpty(string text) { _rows.Clear(); _empty = text; MarkDirty(); EnsureView(); }

        // «Cargando…» solo si no hay nada que enseñar mientras tanto.
        public void ShowLoading(string text) { if (_rows.Count == 0 || _empty != null) SetEmpty(text); }

        public void Filter(string text) { _filter = (text ?? "").Trim().ToLowerInvariant(); MarkDirty(); EnsureView(); }
        public void Refilter() { MarkDirty(); EnsureView(); }

        public int SelectedRow { get { EnsureView(); return (SelectedItem as Row)?.Orig ?? -1; } }
        public int VisibleRowCount { get { EnsureView(); return Items.Count; } }

        public void SelectRow(int orig)
        {
            EnsureView();
            for (int k = 0; k < Items.Count; k++) if (((Row)Items[k]).Orig == orig) { SelectedIndex = k; return; }
            SelectedIndex = -1;
        }

        string KeyOf(Row r) => r == null ? null : r.Key ?? (RowKey != null ? RowKey(r.Cells) : (r.Cells.Length > 0 ? r.Cells[0] : null));

        // Al recargar con el mismo contexto (empresa, ruta…) se conservan la elegida y la altura.
        public void BeginReload(string context)
        {
            EnsureView();
            _reload = context != null && context == _ctx && Items.Count > 0 ? (KeyOf(SelectedItem as Row), TopIndex) : null;
            _ctx = context;
        }

        public void EndReload()
        {
            EnsureView();
            if (_reload == null) return;
            var (selKey, top) = _reload.Value; _reload = null;
            if (selKey != null)
                for (int k = 0; k < Items.Count; k++) if (KeyOf((Row)Items[k]) == selKey) { SelectedIndex = k; break; }
            if (Items.Count > 0) TopIndex = Math.Min(top, Items.Count - 1);
        }

        void MarkDirty()
        {
            _dirty = true;
            if (_scheduled || !IsHandleCreated) return;
            _scheduled = true;
            try { BeginInvoke((Action)(() => { _scheduled = false; EnsureView(); })); } catch { _scheduled = false; }
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); EnsureView(); }

        void EnsureView()
        {
            if (!_dirty) return;
            _dirty = false;
            EmptyText = _empty ?? "";
            var words = _filter.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            BeginUpdate();
            Items.Clear();
            if (_empty == null)
                foreach (var r in _rows)
                {
                    if (RowFilter != null && !RowFilter(r.Cells)) continue;
                    if (words.Length > 0)
                    {
                        r.Search ??= string.Join(" ", r.Cells).ToLowerInvariant();
                        bool ok = true;
                        foreach (var w in words) if (r.Search.IndexOf(w, StringComparison.Ordinal) < 0) { ok = false; break; }
                        if (!ok) continue;
                    }
                    Items.Add(r);
                }
            EndUpdate();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e) { EnsureView(); base.OnPaint(e); }

        // ---- dibujo ----
        string Cell(Row r, int c)
        {
            if (c < 0 || c >= r.Cells.Length) return "";
            string v = r.Cells[c] ?? "";
            if (v.Length == 0 || v == "—" || !Formats.TryGetValue(c, out var f)) return v;
            int bar = f.IndexOf('|');
            if (bar >= 0) f = v.Trim() == "1" ? f.Substring(0, bar) : f.Substring(bar + 1);
            return string.Format(f, v);
        }
        Color? Col(Row r, int c) => r.Colors != null && c >= 0 && c < r.Colors.Length ? r.Colors[c] : null;

        static string Initials(string name)
        {
            var p = (name ?? "").Split(new[] { ' ', '_', '.', '-' }, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length == 0) return "?";
            return (p.Length == 1 ? p[0].Substring(0, Math.Min(2, p[0].Length)) : "" + p[0][0] + p[1][0]).ToUpperInvariant();
        }

        protected override void PaintCard(Graphics g, int i, Rectangle rc, bool selected, bool hover)
        {
            if (Items[i] is not Row r) return;
            Fill(g, rc, Theme.Px(10), selected || hover ? Color.FromArgb(50, 54, 57) : Theme.Surface);
            if (selected) Stroke(g, rc, Theme.Px(10), Theme.Accent);
            int pad = Theme.Px(14), x = rc.X + pad, right = rc.Right - pad;

            // casilla
            if (CheckCol >= 0)
            {
                bool on = Cell(r, CheckCol).Length > 0;
                int s = Theme.Px(20);
                var cb = new Rectangle(x, rc.Y + (rc.Height - s) / 2, s, s);
                if (on) Fill(g, cb, Theme.Px(5), Theme.Accent); else Stroke(g, cb, Theme.Px(5), Theme.Subtle, 1.4f);
                if (on) TextRenderer.DrawText(g, "✓", _fCheck, cb, Color.White, C1);
                x = cb.Right + Theme.Px(12);
            }
            // icono, logotipo o iniciales
            else if (ShowAvatar)
            {
                int av = Math.Min(Theme.Px(40), rc.Height - Theme.Px(20));
                var ar = new Rectangle(x, rc.Y + (rc.Height - av) / 2, av, av);
                if (r.Image != null)
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(r.Image, ar);
                }
                else
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using (var b = new SolidBrush(Theme.Surface2)) g.FillEllipse(b, ar);
                    g.SmoothingMode = SmoothingMode.None;
                    if (Icon != null) TextRenderer.DrawText(g, Icon, _fIcon, ar, Theme.Text, C1);
                    else TextRenderer.DrawText(g, Initials(Cell(r, TitleCol)), _fAv, ar, Theme.AccentHi, C1);
                }
                x = ar.Right + Theme.Px(12);
            }

            // derecha: valor grande y su detalle
            int rightW = 0;
            if (RightCol >= 0)
            {
                string big = Cell(r, RightCol);
                rightW = Math.Max(TW(big, _fRight), Theme.Px(90));
                foreach (int c in RightSubCols) rightW = Math.Max(rightW, TW(Cell(r, c), _fSub));
                rightW = Math.Min(rightW + Theme.Px(4), (right - x) / 2);
                int ry = rc.Y + (rc.Height - Theme.Px(24) - RightSubCols.Length * Theme.Px(18)) / 2;
                TextRenderer.DrawText(g, big, _fRight, new Rectangle(right - rightW, ry, rightW, Theme.Px(24)), Col(r, RightCol) ?? Theme.Text, R1);
                ry += Theme.Px(24);
                foreach (int c in RightSubCols)
                {
                    TextRenderer.DrawText(g, Cell(r, c), _fSub, new Rectangle(right - rightW, ry, rightW, Theme.Px(18)), Col(r, c) ?? Theme.Subtle, R1);
                    ry += Theme.Px(18);
                }
                right -= rightW + Theme.Px(14);
            }

            // líneas de texto, centradas en vertical
            string sub = "";
            foreach (int c in SubCols) { string v = Cell(r, c); if (v.Length > 0 && v != "—") sub += (sub.Length > 0 ? "   ·   " : "") + v; }
            string quote = QuoteCol >= 0 ? Cell(r, QuoteCol) : "";
            int lines = 1 + (sub.Length > 0 ? 1 : 0) + (quote.Length > 0 ? 1 : 0);
            int lh = Theme.Px(20), y = rc.Y + (rc.Height - lines * lh - (lines - 1) * Theme.Px(2)) / 2;

            // título + pastilla + distintivo
            string title = Cell(r, TitleCol);
            int pillX = right;
            void PillAt(string text, Color c)
            {
                int pw = TW(text, _fPill) + Theme.Px(16), ph = Theme.Px(20);
                var pr = new Rectangle(pillX - pw, y, pw, ph);
                Fill(g, pr, ph / 2, Color.FromArgb(40, c));
                TextRenderer.DrawText(g, text, _fPill, pr, c, C1);
                pillX = pr.Left - Theme.Px(6);
            }
            if (PillCol >= 0) { string pv = Cell(r, PillCol); if (pv.Length > 0 && pv != "—") PillAt(pv, Col(r, PillCol) ?? Theme.AccentHi); }
            if (Badges.TryGetValue(r.Orig, out var badge)) PillAt(badge, Theme.Accent);
            TextRenderer.DrawText(g, title, _fTitle, new Rectangle(x, y, Math.Max(10, pillX - x - Theme.Px(4)), lh), Col(r, TitleCol) ?? Theme.Text, L1);
            y += lh + Theme.Px(2);
            if (sub.Length > 0)
            {
                Color sc = Theme.Subtle;
                if (SubCols.Length == 1 && Col(r, SubCols[0]) is Color c1) sc = c1;
                TextRenderer.DrawText(g, sub, _fSub, new Rectangle(x, y, right - x, lh), sc, L1);
                y += lh + Theme.Px(2);
            }
            if (quote.Length > 0)
                TextRenderer.DrawText(g, "«" + quote + "»", _fQuote, new Rectangle(x, y, right - x, lh), Color.FromArgb(205, 210, 214), L1);
        }
    }
}
