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
using System.Linq;
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
        public bool QuoteMarks = true;                   // false: la tercera línea sin comillas y con el color de su celda
        public int PillCol = -1;                         // pastilla a la derecha del título (con el color de su celda)
        public int RightCol = -1;                        // valor grande a la derecha
        public int[] RightSubCols = Array.Empty<int>();  // debajo del valor grande
        public int CheckCol = -1;                        // casilla (✓ si la celda no está vacía)
        public string Icon;                              // emoji en el círculo; null = iniciales del título (o el logotipo)
        public bool ShowAvatar = true;
        public readonly Dictionary<int, string> Formats = new Dictionary<int, string>();   // celda → «{0} parada|{0} paradas» (singular|plural)
        public readonly Dictionary<int, string> Badges = new Dictionary<int, string>();    // fila (orden de inserción) → distintivo
        public int CardHeight = 72, MinWidth = 420, Columns = 1;
        // Franja 2D de la composición abajo de la tarjeta (como en Flota): Strip da la imagen de cada fila (null mientras
        // no está); StripHeight es su alto (sin escalar) y entra en CardHeight; 0 = sin franja.
        public Func<Row, Bitmap> Strip;
        public Func<Row, string> StripNote;              // texto en el hueco si la fila no tiene imagen (null = hueco vacío)
        public int StripHeight, StripRows = 1;
        // Imagen grande a la izquierda en lugar del logotipo (anuncios): ThumbSlot dice si la fila la tiene (se reserva el
        // hueco aunque aún no haya llegado) y Thumb la da (null mientras se descarga). Ocupa el alto de la tarjeta.
        public Func<Row, bool> ThumbSlot;
        public Func<Row, Image> Thumb;
        public double ThumbAspect = 16 / 9.0;

        // (1.2.84, Tablón) Cada tarjeta del alto que pide su texto (sin cortar nada): el título, la segunda línea y la tercera
        // pasan de línea; CardHeight es el mínimo. SubChips: la segunda línea en pastillas (cada celda, y dentro de ella lo
        // separado por «  ·  »). ThumbWidth: el ancho de la imagen grande (sin escalar). AvatarSize: el del logotipo.
        public bool AutoHeight, SubChips;
        public int ThumbWidth = 200, AvatarSize = 40;
        // (1.2.90, Tablón) Accent: franja de color a la izquierda de cada tarjeta (por categoría); null = sin franja.
        // Ticket: tarjeta de «billete» (mercado): muescas arriba y abajo y una línea de puntos antes del valor grande.
        public Func<Row, Color?> Accent;
        public bool Ticket;
        protected override bool VariableHeights => AutoHeight;
        protected override int ItemHeight(int i, int cardW) => i >= 0 && i < Items.Count && Items[i] is Row r ? DrawAuto(null, r, new Rectangle(0, 0, cardW, Theme.Px(CardHeight)), false, false) : CardH;

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

        bool SafeSlot(Row r) { try { return ThumbSlot(r); } catch { return false; } }

        static string Initials(string name)
        {
            var p = (name ?? "").Split(new[] { ' ', '_', '.', '-' }, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length == 0) return "?";
            return (p.Length == 1 ? p[0].Substring(0, Math.Min(2, p[0].Length)) : "" + p[0][0] + p[1][0]).ToUpperInvariant();
        }

        protected override void PaintCard(Graphics g, int i, Rectangle rc, bool selected, bool hover)
        {
            if (Items[i] is not Row r) return;
            if (AutoHeight) { DrawAuto(g, r, rc, selected, hover); return; }
            Fill(g, rc, Theme.Px(10), selected || hover ? Color.FromArgb(50, 54, 57) : Theme.Surface);
            if (selected) Stroke(g, rc, Theme.Px(10), Theme.Accent);
            int pad = Theme.Px(14), x = rc.X + pad, right = rc.Right - pad;

            // franja 2D abajo; el resto de la tarjeta queda encima
            if (StripHeight > 0 && Strip != null)
            {
                int sh = Theme.Px(StripHeight), gap = Theme.Px(10);
                var sr = new Rectangle(rc.X + pad, rc.Bottom - gap - sh, rc.Width - 2 * pad, sh);
                Bitmap img = null;
                try { img = Strip(r); } catch { }
                string note = null;
                if (img == null) try { note = StripNote?.Invoke(r); } catch { }
                if (img != null || !string.IsNullOrEmpty(note)) StripPaint.Draw(g, sr, img, note, _fSub, maxRows: StripRows, left: true);
                rc = new Rectangle(rc.X, rc.Y, rc.Width, Math.Max(Theme.Px(40), sr.Top - rc.Y));
            }

            // casilla
            if (CheckCol >= 0)
            {
                bool on = Cell(r, CheckCol).Length > 0;
                int s = Theme.Px(20);
                var cb = new Rectangle(x, rc.Y + (rc.Height - s) / 2, s, s);
                if (on) Fill(g, cb, Theme.Px(5), Theme.Accent); else Stroke(g, cb, Theme.Px(5), Theme.Subtle, 1.4f);
                if (on) ColorText.Text(g, "✓", _fCheck, cb, Color.White, C1);
                x = cb.Right + Theme.Px(12);
            }
            // imagen grande (anuncios)
            else if (ThumbSlot != null && SafeSlot(r))
            {
                int th = rc.Height - Theme.Px(20), tw = (int)Math.Round(th * ThumbAspect);
                var tr = new Rectangle(x - Theme.Px(4), rc.Y + Theme.Px(10), tw, th);
                Fill(g, tr, Theme.Px(8), Color.FromArgb(24, 26, 28));
                Image im = null;
                try { im = Thumb?.Invoke(r); } catch { }
                if (im != null && im.Width > 0 && im.Height > 0)
                {
                    double k = Math.Min(tr.Width / (double)im.Width, tr.Height / (double)im.Height);
                    int w = (int)Math.Round(im.Width * k), h = (int)Math.Round(im.Height * k);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(im, new Rectangle(tr.X + (tr.Width - w) / 2, tr.Y + (tr.Height - h) / 2, w, h));
                    g.PixelOffsetMode = PixelOffsetMode.Default;
                }
                else ColorText.Text(g, "🖼", _fIcon, tr, Theme.Subtle, C1);
                x = tr.Right + Theme.Px(14);
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
                    if (Icon != null) ColorText.Text(g, Icon, _fIcon, ar, Theme.Text, C1);
                    else ColorText.Text(g, Initials(Cell(r, TitleCol)), _fAv, ar, Theme.AccentHi, C1);
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
                ColorText.Text(g, big, _fRight, new Rectangle(right - rightW, ry, rightW, Theme.Px(24)), Col(r, RightCol) ?? Theme.Text, R1);
                ry += Theme.Px(24);
                foreach (int c in RightSubCols)
                {
                    ColorText.Text(g, Cell(r, c), _fSub, new Rectangle(right - rightW, ry, rightW, Theme.Px(18)), Col(r, c) ?? Theme.Subtle, R1);
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
                ColorText.Text(g, text, _fPill, pr, c, C1);
                pillX = pr.Left - Theme.Px(6);
            }
            if (PillCol >= 0) { string pv = Cell(r, PillCol); if (pv.Length > 0 && pv != "—") PillAt(pv, Col(r, PillCol) ?? Theme.AccentHi); }
            if (Badges.TryGetValue(r.Orig, out var badge)) PillAt(badge, Theme.Accent);
            ColorText.Text(g, title, _fTitle, new Rectangle(x, y, Math.Max(10, pillX - x - Theme.Px(4)), lh), Col(r, TitleCol) ?? Theme.Text, L1);
            y += lh + Theme.Px(2);
            if (sub.Length > 0)
            {
                Color sc = Theme.Subtle;
                if (SubCols.Length == 1 && Col(r, SubCols[0]) is Color c1) sc = c1;
                ColorText.Text(g, sub, _fSub, new Rectangle(x, y, right - x, lh), sc, L1);
                y += lh + Theme.Px(2);
            }
            if (quote.Length > 0)
                ColorText.Text(g, QuoteMarks ? "«" + quote + "»" : quote, _fQuote, new Rectangle(x, y, right - x, lh),
                    QuoteMarks ? Color.FromArgb(205, 210, 214) : Col(r, QuoteCol) ?? Color.FromArgb(205, 210, 214), L1);
        }

        // ---- (1.2.84) tarjeta del alto de su texto: con g = null solo mide y devuelve el alto que necesita ----
        const TextFormatFlags Wrap = TextFormatFlags.NoPadding | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl;
        static int TH(string s, Font f, int w) => string.IsNullOrEmpty(s) ? 0 : TextRenderer.MeasureText(s, f, new Size(Math.Max(10, w), int.MaxValue), Wrap).Height;
        static readonly Color ChipBg = Color.FromArgb(46, 50, 54), ChipText = Color.FromArgb(196, 202, 208), BodyText = Color.FromArgb(214, 218, 222);

        int DrawAuto(Graphics g, Row r, Rectangle rc, bool selected, bool hover)
        {
            bool paint = g != null;
            int pad = Theme.Px(14), gap = Theme.Px(5);
            Color? accent = null; try { accent = Accent?.Invoke(r); } catch { }
            if (paint)
            {
                Fill(g, rc, Theme.Px(10), selected || hover ? Color.FromArgb(50, 54, 57) : Theme.Surface);
                if (accent is Color ac)
                {
                    // la franja de su color a la izquierda (recortada a la tarjeta) y un velo muy suave de ese color
                    var st0 = g.Save();
                    using (var clip = RoundPath(rc, Theme.Px(10))) g.SetClip(clip);
                    using (var lg = new LinearGradientBrush(new Rectangle(rc.X, rc.Y, Math.Max(1, rc.Width / 2), rc.Height), Color.FromArgb(26, ac), Color.FromArgb(0, ac), LinearGradientMode.Horizontal))
                        g.FillRectangle(lg, rc.X, rc.Y, rc.Width / 2, rc.Height);
                    using (var b = new SolidBrush(ac)) g.FillRectangle(b, rc.X, rc.Y, Theme.Px(5), rc.Height);
                    g.Restore(st0);
                }
                if (selected) Stroke(g, rc, Theme.Px(10), Theme.Accent);
            }
            int x = rc.X + pad + (accent != null ? Theme.Px(4) : 0), right = rc.Right - pad, top = rc.Y + pad, bottom = top;
            int perfX = -1, stripTop = -1;   // (Ticket) dónde va la línea de puntos y dónde empieza la franja (ahí acaba la línea)

            // a la izquierda: la imagen grande (anuncios) o el logotipo
            if (ThumbSlot != null && SafeSlot(r))
            {
                int tw = Math.Min(Theme.Px(ThumbWidth), (rc.Width - 2 * pad) / 3), th = (int)Math.Round(tw / ThumbAspect);
                var tr = new Rectangle(x, top, tw, th);
                if (paint)
                {
                    Fill(g, tr, Theme.Px(8), Color.FromArgb(24, 26, 28));
                    Image im = null;
                    try { im = Thumb?.Invoke(r); } catch { }
                    if (im != null && im.Width > 0 && im.Height > 0)
                    {
                        double k = Math.Max(tr.Width / (double)im.Width, tr.Height / (double)im.Height);   // (llena el hueco: recorta lo que sobra)
                        int w = (int)Math.Round(im.Width * k), h = (int)Math.Round(im.Height * k);
                        var st = g.Save();
                        using (var clip = RoundPath(tr, Theme.Px(8))) g.SetClip(clip);
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.DrawImage(im, new Rectangle(tr.X + (tr.Width - w) / 2, tr.Y + (tr.Height - h) / 2, w, h));
                        g.Restore(st);
                    }
                    else ColorText.Text(g, "🖼", _fIcon, tr, Theme.Subtle, C1);
                }
                x = tr.Right + Theme.Px(16); bottom = Math.Max(bottom, tr.Bottom);
            }
            else if (ShowAvatar)
            {
                int av = Theme.Px(AvatarSize);
                var ar = new Rectangle(x, top, av, av);
                if (paint)
                {
                    if (r.Image != null) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.DrawImage(r.Image, ar); }
                    else
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        using (var b = new SolidBrush(Theme.Surface2)) g.FillEllipse(b, ar);
                        g.SmoothingMode = SmoothingMode.None;
                        if (Icon != null) ColorText.Text(g, Icon, _fIcon, ar, Theme.Text, C1);
                        else ColorText.Text(g, Initials(Cell(r, TitleCol)), _fAv, ar, Theme.AccentHi, C1);
                    }
                }
                x = ar.Right + Theme.Px(14); bottom = Math.Max(bottom, ar.Bottom);
            }

            // a la derecha: el valor grande y su detalle, arriba
            if (RightCol >= 0)
            {
                string big = Cell(r, RightCol);
                if (big.Length > 0)
                {
                    int rightW = Math.Max(TW(big, _fRight), Theme.Px(80));
                    foreach (int c in RightSubCols) rightW = Math.Max(rightW, TW(Cell(r, c), _fSub));
                    rightW = Math.Min(rightW + Theme.Px(4), Math.Max(Theme.Px(60), (right - x) / 3));
                    int ry = top;
                    int bh = Math.Max(Theme.Px(24), TH(big, _fRight, rightW));
                    if (paint) ColorText.Text(g, big, _fRight, new Rectangle(right - rightW, ry, rightW, bh), Col(r, RightCol) ?? Theme.Text, Wrap | TextFormatFlags.Right);
                    ry += bh;
                    foreach (int c in RightSubCols)
                    {
                        string v = Cell(r, c); if (v.Length == 0) continue;
                        int h = TH(v, _fSub, rightW);
                        if (paint) ColorText.Text(g, v, _fSub, new Rectangle(right - rightW, ry, rightW, h), Col(r, c) ?? Theme.Subtle, Wrap | TextFormatFlags.Right);
                        ry += h + Theme.Px(2);
                    }
                    bottom = Math.Max(bottom, ry);
                    right -= rightW + Theme.Px(16);
                    if (Ticket) { perfX = right + Theme.Px(4); right -= Theme.Px(10); }
                }
            }

            // las pastillas (el distintivo y la de su columna): a la derecha del título si cabe; si no, debajo
            var pills = new List<(string t, Color c)>();
            if (Badges.TryGetValue(r.Orig, out var badge)) pills.Add((badge, Theme.Accent));
            if (PillCol >= 0) { string pv = Cell(r, PillCol); if (pv.Length > 0 && pv != "—") pills.Add((pv, Col(r, PillCol) ?? Theme.AccentHi)); }
            int ph = Theme.Px(20), pillsW = pills.Sum(p => TW(p.t, _fPill) + Theme.Px(22));
            bool pillsInline = pills.Count == 0 || right - x - pillsW >= Theme.Px(170);
            void DrawPills(int px, int py, bool fromRight)
            {
                foreach (var (t, c) in pills)
                {
                    int pw = TW(t, _fPill) + Theme.Px(16);
                    var pr = fromRight ? new Rectangle(px - pw, py, pw, ph) : new Rectangle(px, py, pw, ph);
                    if (paint) { Fill(g, pr, ph / 2, Color.FromArgb(40, c)); ColorText.Text(g, t, _fPill, pr, c, C1); }
                    px = fromRight ? pr.Left - Theme.Px(6) : pr.Right + Theme.Px(6);
                }
            }

            int y = top;
            string title = Cell(r, TitleCol);
            int titleW = pillsInline ? right - x - pillsW : right - x;
            int tH = Math.Max(Theme.Px(20), TH(title, _fTitle, titleW));
            if (paint) ColorText.Text(g, title, _fTitle, new Rectangle(x, y, titleW, tH), Col(r, TitleCol) ?? Theme.Text, Wrap);
            if (pills.Count > 0 && pillsInline) DrawPills(right, y, true);
            y += tH + gap;
            if (pills.Count > 0 && !pillsInline) { DrawPills(x, y, false); y += ph + gap; }

            // segunda línea: en pastillas o en texto
            var subs = new List<(string t, Color c)>();
            foreach (int c in SubCols)
            {
                string v = Cell(r, c); if (v.Length == 0 || v == "—") continue;
                Color? sc = Col(r, c);
                if (SubChips) foreach (var part in v.Split(new[] { "  ·  " }, StringSplitOptions.RemoveEmptyEntries)) subs.Add((part.Trim(), sc ?? ChipText));
                else subs.Add((v, sc ?? Theme.Subtle));
            }
            if (subs.Count > 0)
            {
                if (SubChips)
                {
                    int cx = x, ch = Theme.Px(22), rowY = y + Theme.Px(1);
                    foreach (var (t, c) in subs)
                    {
                        int cw = Math.Min(right - x, TW(t, _fSub) + Theme.Px(18));
                        if (cx > x && cx + cw > right) { cx = x; rowY += ch + Theme.Px(5); }
                        var cr = new Rectangle(cx, rowY, cw, ch);
                        if (paint) { Fill(g, cr, ch / 2, ChipBg); ColorText.Text(g, t, _fSub, cr, c == Theme.Subtle ? ChipText : c, C1); }
                        cx = cr.Right + Theme.Px(6);
                    }
                    y = rowY + ch + gap + Theme.Px(2);
                }
                else
                {
                    string sub = string.Join("   ·   ", subs.Select(q => q.t));
                    Color sc = subs.Count == 1 ? subs[0].c : Theme.Subtle;
                    int h = TH(sub, _fSub, right - x);
                    if (paint) ColorText.Text(g, sub, _fSub, new Rectangle(x, y, right - x, h), sc, Wrap);
                    y += h + gap;
                }
            }

            // tercera: el texto entero
            string quote = QuoteCol >= 0 ? Cell(r, QuoteCol) : "";
            if (quote.Length > 0)
            {
                string q = QuoteMarks ? "«" + quote + "»" : quote;
                int h = TH(q, _fQuote, right - x);
                if (paint) ColorText.Text(g, q, _fQuote, new Rectangle(x, y, right - x, h), QuoteMarks ? BodyText : Col(r, QuoteCol) ?? BodyText, Wrap);
                y += h + gap;
            }
            bottom = Math.Max(bottom, y - gap);

            // la franja 2D (mercado), debajo de todo
            if (StripHeight > 0 && Strip != null)
            {
                int sh = Theme.Px(StripHeight);
                var sr = new Rectangle(rc.X + pad, bottom + Theme.Px(10), rc.Width - 2 * pad, sh);
                stripTop = sr.Top;
                if (paint)
                {
                    Bitmap img = null; try { img = Strip(r); } catch { }
                    string note = null; if (img == null) try { note = StripNote?.Invoke(r); } catch { }
                    if (img != null || !string.IsNullOrEmpty(note)) StripPaint.Draw(g, sr, img, note, _fSub, maxRows: StripRows, left: true);
                }
                bottom = sr.Bottom;
            }
            int hgt = Math.Max(Theme.Px(CardHeight), bottom + pad - rc.Y);
            // (Ticket) las muescas del billete (del color del fondo) y la línea de puntos entre ellas
            if (paint && perfX > 0)
            {
                int nr = Theme.Px(7), cardBottom = rc.Y + Math.Min(rc.Height, hgt);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var bg = new SolidBrush(BackColor))
                {
                    g.FillEllipse(bg, perfX - nr, rc.Y - nr, 2 * nr, 2 * nr);
                    if (stripTop < 0) g.FillEllipse(bg, perfX - nr, cardBottom - nr, 2 * nr, 2 * nr);
                }
                g.SmoothingMode = SmoothingMode.None;
                int lineEnd = stripTop > 0 ? stripTop - Theme.Px(6) : cardBottom - nr - Theme.Px(3);
                using (var pen = new Pen(Color.FromArgb(70, 78, 86), Math.Max(1, Theme.Px(1))) { DashPattern = new[] { 2f, 3f } })
                    g.DrawLine(pen, perfX, rc.Y + nr + Theme.Px(3), perfX, lineEnd);
            }
            return hgt;
        }

        static GraphicsPath RoundPath(Rectangle r, int rad)
        {
            var p = new GraphicsPath(); int d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90); p.CloseFigure();
            return p;
        }
    }
}
