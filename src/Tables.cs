// Tabla estilizada (ListView con dibujo propio): cabecera oscura, filas altas y alternas,
// selección con acento, hover, filtro por texto y ordenación al pulsar la cabecera.
// Las columnas se ajustan AUTOMÁTICAMENTE al contenido (no se pueden redimensionar a mano);
// una columna "Fill" absorbe el espacio restante. Soporta un logotipo por fila.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace SelectOR
{
    public class StyledTable : ListView
    {
        public sealed class Col
        {
            public string Title;
            public int Min;                   // ancho mínimo (px)
            public bool Fill;                 // absorbe el ancho restante
            public HorizontalAlignment Align = HorizontalAlignment.Left;
            public Col(string t, int min, bool fill = false, HorizontalAlignment a = HorizontalAlignment.Left)
            { Title = t; Min = min; Fill = fill; Align = a; }
        }

        sealed class Row
        {
            public string[] Cells;
            public Color?[] Colors;
            public Image Image;
            public int OrigIndex;        // orden de inserción (para que la selección mapee a los datos)
            public ListViewItem Item;    // su fila dibujada (si está visible): cambiarla es O(1)
            public string Key;           // identificador opcional (id del vehículo, del servicio…)
        }

        readonly List<Col> _cols = new();
        readonly List<Row> _rows = new();
        int[] _maxW;                    // ancho medido máx. por columna (incluye cabecera)
        string _filter = "";
        string _emptyText;
        int _sortCol = -1; bool _sortAsc = true;
        int _hoverItem = -1;
        bool _applyingWidths;           // true mientras el auto-ancho fija anchos (no cancelar esos)

        static int CellPad => Theme.Px(20);   // margen horizontal total por celda
        static int ImgSize => Theme.Px(24);   // lado del logotipo
        static int ImgGap => Theme.Px(8);     // separación logo→texto
        static int RowH => Theme.Px(36);

        public int ImageColumn { get; set; } = -1;   // columna donde se dibuja el logotipo (−1 = ninguna)

        static readonly Color RowA = Theme.Surface;
        static readonly Color RowB = Blend(Theme.Surface, Theme.Surface2, 0.42f);
        static readonly Color HoverBg = Blend(Theme.Surface, Theme.Accent, 0.09f);
        static readonly Color SelBg = Blend(Theme.Surface, Theme.Accent, 0.16f);
        static readonly Color HeaderBg = Blend(Theme.Surface2, Theme.Bg, 0.35f);
        static readonly Color SepColor = Blend(Theme.Surface, Theme.Bg, 0.4f);
        static readonly Font HeaderFont = Theme.Font(9f, FontStyle.Bold);

        public Func<string[], bool> RowFilter { get; set; }
        public void Refilter() => Rebuild();

        public StyledTable()
        {
            View = View.Details;
            OwnerDraw = true;
            FullRowSelect = true;
            MultiSelect = false;
            HeaderStyle = ColumnHeaderStyle.Clickable;
            AllowColumnReorder = false;
            BorderStyle = BorderStyle.None;
            BackColor = Theme.Surface;
            ForeColor = Theme.Text;
            Font = Theme.Font(9.75f);
            SmallImageList = new ImageList { ImageSize = new Size(1, RowH) };   // fuerza filas más altas
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            DrawColumnHeader += OnDrawHeader;
            DrawSubItem += OnDrawSub;
            DrawItem += (s, e) => { };
            Resize += (s, e) => ApplyWidths();
            ColumnClick += (s, e) => SortByColumn(e.Column);
            // Columnas NO redimensionables a mano: se cancela el arrastre del usuario,
            // pero NO el auto-ancho programático (_applyingWidths).
            ColumnWidthChanging += (s, e) => { if (!_applyingWidths) { e.Cancel = true; e.NewWidth = Columns[e.ColumnIndex].Width; } };
            MouseMove += OnMouseMoveHover;
            MouseLeave += (s, e) => SetHover(-1);
            Native.UseDarkScrollBars(this);
        }

        // La tabla NUNCA muestra barra horizontal: las columnas se ajustan al ancho visible
        // (ApplyWidths) y, mientras se rellenan las filas, se oculta por si Windows la saca sola.
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool ShowScrollBar(IntPtr hWnd, int wBar, bool bShow);

        void HideHScroll()
        {
            if (IsHandleCreated) { try { ShowScrollBar(Handle, 0 /*SB_HORZ*/, false); } catch { } }
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_NCCALCSIZE = 0x0083;
            if (m.Msg == WM_NCCALCSIZE) HideHScroll();
            base.WndProc(ref m);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            HideHScroll();
            ApplyWidths();
            // Las columnas no se pueden redimensionar a mano, así que su cabecera tampoco debe
            // enseñar el cursor de «cambiar ancho»: se engancha a la ventana nativa de la cabecera.
            try
            {
                var hdr = SendMessage(Handle, LVM_GETHEADER, IntPtr.Zero, IntPtr.Zero);
                if (hdr != IntPtr.Zero) _headerCursor.Attach(hdr);
            }
            catch { }
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            try { _headerCursor.ReleaseHandle(); } catch { }
            base.OnHandleDestroyed(e);
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        const int LVM_GETHEADER = 0x101F;
        readonly HeaderCursor _headerCursor = new();

        // Cabecera de la tabla: siempre puntero normal (no hay anchos que arrastrar).
        sealed class HeaderCursor : NativeWindow
        {
            [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr SetCursor(IntPtr hCursor);
            [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr LoadCursor(IntPtr hInstance, int cursor);
            const int WM_SETCURSOR = 0x0020, IDC_ARROW = 32512;

            public void Attach(IntPtr handle)
            {
                if (Handle != IntPtr.Zero) ReleaseHandle();
                AssignHandle(handle);
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WM_SETCURSOR)
                {
                    SetCursor(LoadCursor(IntPtr.Zero, IDC_ARROW));
                    m.Result = (IntPtr)1;
                    return;
                }
                base.WndProc(ref m);
            }
        }

        public void SetColumns(params Col[] cols)
        {
            _cols.Clear(); _cols.AddRange(cols);
            foreach (var c in _cols) c.Title = I18n.T(c.Title);   // cabeceras traducidas según el idioma de OR
            Columns.Clear();
            _maxW = new int[cols.Length];
            foreach (var c in _cols)
            {
                var ch = new ColumnHeader { Text = c.Title }; ch.TextAlign = c.Align;
                Columns.Add(ch);
            }
            ResetMeasures();
            ApplyWidths();
        }

        public int SelectedRow => SelectedItems.Count > 0 && SelectedItems[0].Tag is Row r ? r.OrigIndex : -1;

        /// <summary>Selecciona (y hace visible) la fila por su índice de inserción; −1 = ninguna.</summary>
        public void SelectRow(int origIndex)
        {
            foreach (ListViewItem it in Items)
                if (it.Tag is Row r && r.OrigIndex == origIndex) { it.Selected = true; it.Focused = true; it.EnsureVisible(); return; }
            SelectedItems.Clear();
        }

        // ---- Conservar la vista al recargar ----
        // Al editar una fila, la tabla se vacía y se vuelve a llenar, y el ListView vuelve arriba.
        // BeginReload(contexto) antes de vaciarla y EndReload() después de llenarla la dejan con la
        // misma fila arriba y la misma elegida, SOLO si el contexto (empresa, ruta…) es el mismo:
        // al cambiar de empresa la lista empieza arriba como siempre.
        public Func<string[], string> RowKey;   // identifica una fila; por defecto, su primera celda
        string _viewContext;
        (string topKey, int topIdx, string selKey, int selIdx)? _reloadView;

        string KeyOf(Row r) => r?.Cells == null ? null : r.Key ?? (RowKey != null ? RowKey(r.Cells) : (r.Cells.Length > 0 ? r.Cells[0] : null));

        public void BeginReload(string context)
        {
            _reloadView = null;
            _reloadArmed = true;
            if (context != null && context == _viewContext && _emptyText == null && Items.Count > 0)
            {
                var top = TopItem?.Tag as Row;
                var sel = SelectedItems.Count > 0 ? SelectedItems[0].Tag as Row : null;
                _reloadView = (KeyOf(top), TopItem?.Index ?? 0, KeyOf(sel), sel != null ? SelectedItems[0].Index : -1);
            }
            _viewContext = context;
        }

        public void EndReload()
        {
            _reloadArmed = false;
            if (_reloadView == null) { EndBatchSoon(); return; }
            var v = _reloadView.Value; _reloadView = null;
            if (_emptyText != null || Items.Count == 0) { EndBatchSoon(); return; }
            ListViewItem Find(string key, int idx)
            {
                if (key != null)
                    foreach (ListViewItem it in Items)
                        if (it.Tag is Row r && KeyOf(r) == key) return it;
                return idx >= 0 ? Items[Math.Min(idx, Items.Count - 1)] : null;
            }
            var sel = v.selIdx >= 0 ? Find(v.selKey, v.selIdx) : null;
            if (sel != null) { sel.Selected = true; sel.Focused = true; }
            var top = Find(v.topKey, v.topIdx);
            if (top == null) { EndBatchSoon(); return; }
            // En vista de detalles, TopItem a veces no se aplica hasta que la lista termina de maquetar:
            // se fija ahora y otra vez justo antes de volver a pintar (con el redibujado aún parado).
            try { TopItem = top; } catch { }
            if (_batching) { _restoreTop = top; EndBatchSoon(); }
            else try { BeginInvoke((Action)(() => { try { if (top.ListView == this) TopItem = top; } catch { } })); } catch { }
        }

        // ---- Sin parpadeos al recargar ----
        // Recargar vacía la lista y la vuelve a llenar fila a fila; con el redibujado activo se ve la
        // lista en blanco un instante, o «Cargando…», en cada refresco en vivo. Ahora:
        //  · ShowLoading() NO cambia nada si la tabla ya tiene filas del mismo contexto: se ven los datos
        //    de antes hasta que llegan los nuevos (y la primera vez, o con otro contexto, sí sale el aviso).
        //  · Vaciar y llenar se hace con el redibujado parado (BeginUpdate) y se pinta UNA vez al final,
        //    ya con la misma fila arriba y la misma elegida.
        bool _reloadArmed;          // BeginReload llamado y la recarga aún no ha terminado
        bool _batching;             // redibujado parado mientras se vacía y se llena
        ListViewItem _restoreTop;   // fila que debe quedar arriba al terminar

        /// <summary>Aviso de carga («Cargando…») solo si no hay nada que enseñar mientras tanto.</summary>
        public void ShowLoading(string text)
        {
            bool hasRows = _emptyText == null && _rows.Count > 0;
            bool keep = _reloadArmed ? _reloadView != null : hasRows;
            if (!keep) SetEmpty(text);
        }

        void StartBatch()
        {
            if (_batching || !IsHandleCreated) return;
            _batching = true;
            BeginUpdate();
            // Por si nadie la cierra (la recarga no llama a EndReload): se vuelve a pintar en cuanto
            // la interfaz queda libre, que es cuando ya se han añadido las filas.
            EndBatchSoon();
        }

        void EndBatchSoon()
        {
            if (!_batching) return;
            try { BeginInvoke((Action)EndBatch); } catch { EndBatch(); }
        }

        void EndBatch()
        {
            if (!_batching) return;
            var top = _restoreTop; _restoreTop = null;
            try { if (top != null && top.ListView == this) TopItem = top; } catch { }
            _batching = false;
            EndUpdate();
            try { if (top != null && top.ListView == this && TopItem != top) TopItem = top; } catch { }
        }

        public void ClearRows()
        {
            StartBatch();
            _rows.Clear(); _emptyText = null; _hoverItem = -1;
            Items.Clear();
            ResetMeasures();
        }

        public void AddRow(string[] cells, Color?[] colors = null) => AddRow(cells, colors, null);

        public void AddRow(string[] cells, Color?[] colors, Image image) => AddRow(cells, colors, image, null);

        /// <summary>Con <paramref name="key"/> (un id), la fila se reconoce al recargar la tabla (BeginReload/EndReload).</summary>
        public void AddRow(string[] cells, Color?[] colors, Image image, string key)
        {
            _emptyText = null;
            StartBatch();
            var row = new Row { Cells = cells, Colors = colors, Image = image, OrigIndex = _rows.Count, Key = key };
            _rows.Add(row);
            // Al llenar miles de filas, reajustar los anchos en cada una costaba más que las filas:
            // se acumulan y se hace UNA vez cuando la interfaz vuelve a estar libre.
            if (Measure(row)) ScheduleWidths();
            if (!PassesFilter(row)) return;
            var it = MakeItem(row);
            if (_sortCol < 0) Items.Add(it);
            else Items.Insert(InsertPos(row), it);
        }

        // Cambia UNA celda ya existente (por orden de inserción) sin rehacer la tabla: así una carga en
        // segundo plano puede rellenar una columna sin que la lista parpadee ni pierda el desplazamiento.
        public void SetCell(int origIndex, int col, string text, Color? color = null, bool applyWidths = true)
        {
            if (origIndex < 0 || origIndex >= _rows.Count) return;
            var row = _rows[origIndex];
            if (col < 0 || col >= row.Cells.Length) return;
            row.Cells[col] = text ?? "";
            if (color.HasValue && row.Colors != null && col < row.Colors.Length) row.Colors[col] = color;
            bool grew = Measure(row);
            if (grew && applyWidths) ApplyWidths();
            var it = row.Item;
            if (it == null || it.ListView != this) return;
            while (it.SubItems.Count <= col) it.SubItems.Add("");
            it.SubItems[col].Text = row.Cells[col];
            Invalidate(it.Bounds);
        }

        // Reajusta los anchos una sola vez después de cambiar muchas celdas seguidas.
        public void RefreshWidths() => ApplyWidths();

        bool _widthsPending;
        void ScheduleWidths()
        {
            if (_widthsPending) return;
            _widthsPending = true;
            try { BeginInvoke((Action)(() => { _widthsPending = false; ApplyWidths(); })); }
            catch { _widthsPending = false; ApplyWidths(); }
        }

        public void SetEmpty(string text)
        {
            if (_emptyText == text && Items.Count == 1) return;   // ya lo está diciendo: nada que repintar
            StartBatch();
            _rows.Clear(); _hoverItem = -1;
            _emptyText = text;
            Items.Clear();
            Items.Add(new ListViewItem(text));
            ResetMeasures();
        }

        public void Filter(string text) { _filter = (text ?? "").Trim(); Rebuild(); }

        void SortByColumn(int col)
        {
            if (_emptyText != null || _rows.Count == 0) return;
            if (_sortCol == col) _sortAsc = !_sortAsc; else { _sortCol = col; _sortAsc = true; }
            Rebuild();
        }

        void Rebuild()
        {
            if (_emptyText != null) return;
            var visible = new List<Row>();
            foreach (var r in _rows) if (PassesFilter(r)) visible.Add(r);
            if (_sortCol >= 0)
                visible.Sort((a, b) => { int c = CompareCells(a, b, _sortCol); return _sortAsc ? c : -c; });

            BeginUpdate();
            Items.Clear();
            foreach (var r in visible) Items.Add(MakeItem(r));
            EndUpdate();
            if (Items.Count == 0 && _filter.Length > 0)
                Items.Add(new ListViewItem(I18n.T("Sin resultados para el filtro.")));
        }

        ListViewItem MakeItem(Row r)
        {
            var it = new ListViewItem(r.Cells.Length > 0 ? r.Cells[0] : "") { Tag = r };
            for (int i = 1; i < r.Cells.Length; i++) it.SubItems.Add(r.Cells[i] ?? "");
            r.Item = it;
            return it;
        }

        int InsertPos(Row r)
        {
            for (int i = 0; i < Items.Count; i++)
            {
                if (!(Items[i].Tag is Row other)) continue;
                int c = CompareCells(r, other, _sortCol);
                if (_sortAsc ? c < 0 : c > 0) return i;
            }
            return Items.Count;
        }

        bool PassesFilter(Row r)
        {
            if (RowFilter != null && !RowFilter(r.Cells)) return false;
            if (_filter.Length == 0) return true;
            foreach (var cell in r.Cells)
                if (cell != null && cell.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        static int CompareCells(Row a, Row b, int col)
        {
            string sa = col < a.Cells.Length ? a.Cells[col] ?? "" : "";
            string sb = col < b.Cells.Length ? b.Cells[col] ?? "" : "";
            if (TryNum(sa, out double na) && TryNum(sb, out double nb)) return na.CompareTo(nb);
            return string.Compare(sa, sb, StringComparison.CurrentCultureIgnoreCase);
        }

        static bool TryNum(string s, out double val)
        {
            val = 0;
            if (string.IsNullOrWhiteSpace(s)) return false;
            var sb = new System.Text.StringBuilder(s.Length);
            bool any = false;
            foreach (char ch in s)
            {
                if (char.IsDigit(ch)) { sb.Append(ch); any = true; }
                else if (ch == '-' || ch == '+') sb.Append(ch);
                else if (ch == ',') sb.Append('.');
            }
            return any && double.TryParse(sb.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out val);
        }

        // ---- Medida y auto-ancho ----
        void ResetMeasures()
        {
            if (_maxW == null) return;
            for (int i = 0; i < _cols.Count; i++)
            {
                int w = TextRenderer.MeasureText(_cols[i].Title, HeaderFont).Width + 20;  // hueco para la flecha ▲/▼
                if (i == ImageColumn) w += ImgSize + ImgGap;
                _maxW[i] = w;
            }
        }

        // Devuelve true si alguna columna creció (para reaplicar anchos).
        bool Measure(Row r)
        {
            if (_maxW == null) return false;
            bool grew = false;
            for (int i = 0; i < _cols.Count && i < r.Cells.Length; i++)
            {
                int w = TextRenderer.MeasureText(r.Cells[i] ?? "", Font).Width + CellPad;
                if (i == ImageColumn) w += ImgSize + ImgGap;
                if (w > _maxW[i]) { _maxW[i] = w; grew = true; }
            }
            return grew;
        }

        void ApplyWidths()
        {
            if (_cols.Count == 0 || Columns.Count != _cols.Count || _maxW == null) return;
            int avail = ClientSize.Width - 2;
            int fillIdx = -1, fixedSum = 0;
            var w = new int[_cols.Count];
            for (int i = 0; i < _cols.Count; i++)
            {
                if (_cols[i].Fill) { fillIdx = i; continue; }
                // El ancho lo marca el CONTENIDO (cabecera incluida), con un suelo pequeño y un tope.
                w[i] = Math.Min(Math.Max(_maxW[i], 38), 340);
                fixedSum += w[i];
            }
            if (fillIdx >= 0)
            {
                // Si las columnas de ancho fijo no dejan sitio, se encogen a la vez: así la tabla
                // NUNCA saca barra de desplazamiento horizontal (el texto que no cabe se recorta).
                int fillMin = Math.Max(_cols[fillIdx].Min, 120);
                if (fixedSum > 0 && fixedSum + fillMin > avail)
                {
                    double factor = Math.Max(0.30, (avail - fillMin) / (double)fixedSum);
                    fixedSum = 0;
                    for (int i = 0; i < _cols.Count; i++)
                        if (i != fillIdx) { w[i] = Math.Max(Math.Min(w[i], 56), (int)(w[i] * factor)); fixedSum += w[i]; }
                }
                w[fillIdx] = Math.Max(fillMin, avail - fixedSum);
            }
            else if (_cols.Count > 0)
            {
                // sin columna Fill: la última estira hasta llenar
                int last = _cols.Count - 1;
                w[last] = Math.Max(w[last], avail - (fixedSum - w[last]));
            }
            // Último ajuste: si aún se pasa del ancho visible, se recorta primero de las columnas
            // fijas (hasta 40 px) y después de la de relleno, para no dejar barra horizontal.
            int total = 0; for (int i = 0; i < w.Length; i++) total += w[i];
            for (int pass = 0; pass < 4 && total > avail; pass++)
                for (int i = 0; i < w.Length && total > avail; i++)
                {
                    if (i == fillIdx || w[i] <= 40) continue;
                    int cut = Math.Min(w[i] - 40, total - avail);
                    w[i] -= cut; total -= cut;
                }
            if (total > avail && fillIdx >= 0 && w[fillIdx] > 60)
            {
                int cut = Math.Min(w[fillIdx] - 60, total - avail);
                w[fillIdx] -= cut; total -= cut;
            }

            _applyingWidths = true;
            try { for (int i = 0; i < _cols.Count; i++) if (Columns[i].Width != w[i]) Columns[i].Width = w[i]; }
            finally { _applyingWidths = false; }
            HideHScroll();
        }

        void OnMouseMoveHover(object sender, MouseEventArgs e)
        {
            var it = GetItemAt(e.X, e.Y);
            SetHover(it != null && it.Tag is Row ? it.Index : -1);
        }

        void SetHover(int idx)
        {
            if (idx == _hoverItem) return;
            int old = _hoverItem; _hoverItem = idx;
            if (old >= 0 && old < Items.Count) Invalidate(Items[old].Bounds);
            if (idx >= 0 && idx < Items.Count) Invalidate(Items[idx].Bounds);
        }

        void OnDrawHeader(object sender, DrawListViewColumnHeaderEventArgs e)
        {
            using (var b = new SolidBrush(HeaderBg)) e.Graphics.FillRectangle(b, e.Bounds);
            var col = e.ColumnIndex < _cols.Count ? _cols[e.ColumnIndex] : null;
            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis
                        | (col != null && col.Align == HorizontalAlignment.Right ? TextFormatFlags.Right
                           : col != null && col.Align == HorizontalAlignment.Center ? TextFormatFlags.HorizontalCenter
                           : TextFormatFlags.Left);
            string title = e.Header.Text;
            if (e.ColumnIndex == _sortCol) title += _sortAsc ? "  ▲" : "  ▼";
            var r = Rectangle.Inflate(e.Bounds, -10, 0);
            TextRenderer.DrawText(e.Graphics, title, HeaderFont, r, Theme.Accent, flags);
            // línea de acento bajo la cabecera
            using (var pen = new Pen(Blend(Theme.Accent, Theme.Bg, 0.55f)))
                e.Graphics.DrawLine(pen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
        }

        void OnDrawSub(object sender, DrawListViewSubItemEventArgs e)
        {
          try {
            var row = e.Item.Tag as Row;
            bool sel = e.Item.Selected && row != null;
            bool hover = e.ItemIndex == _hoverItem && row != null;
            Color rowBg = sel ? SelBg : hover ? HoverBg : (e.ItemIndex % 2 == 0 ? RowA : RowB);
            using (var b = new SolidBrush(rowBg)) e.Graphics.FillRectangle(b, e.Bounds);

            if (sel && e.ColumnIndex == 0)
                using (var ab = new SolidBrush(Theme.Accent))
                    e.Graphics.FillRectangle(ab, e.Bounds.Left, e.Bounds.Top + 5, 3, e.Bounds.Height - 10);

            using (var lp = new Pen(SepColor))
                e.Graphics.DrawLine(lp, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);

            var textRect = Rectangle.Inflate(e.Bounds, -9, 0);

            // Logotipo en la columna designada (deja hueco fijo para alinear con las filas sin logo).
            if (e.ColumnIndex == ImageColumn)
            {
                int imgLeft = e.Bounds.Left + 8;
                int imgTop = e.Bounds.Top + (e.Bounds.Height - ImgSize) / 2;
                if (row?.Image != null)
                {
                    var old = e.Graphics.InterpolationMode;
                    e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    try { e.Graphics.DrawImage(row.Image, new Rectangle(imgLeft, imgTop, ImgSize, ImgSize)); } catch { }
                    e.Graphics.InterpolationMode = old;
                }
                textRect = new Rectangle(e.Bounds.Left + 8 + ImgSize + ImgGap, e.Bounds.Top,
                                         Math.Max(10, e.Bounds.Right - (e.Bounds.Left + 8 + ImgSize + ImgGap) - 6), e.Bounds.Height);
            }

            Color txt = row == null ? Theme.Subtle : Theme.Text;
            if (row?.Colors != null && e.ColumnIndex < row.Colors.Length && row.Colors[e.ColumnIndex].HasValue)
                txt = row.Colors[e.ColumnIndex].Value;

            var col = e.ColumnIndex < _cols.Count ? _cols[e.ColumnIndex] : null;
            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis
                        | (col != null && col.Align == HorizontalAlignment.Right ? TextFormatFlags.Right
                           : col != null && col.Align == HorizontalAlignment.Center ? TextFormatFlags.HorizontalCenter
                           : TextFormatFlags.Left);
            TextRenderer.DrawText(e.Graphics, e.SubItem?.Text ?? "", Font, textRect, txt, flags);
          } catch { }
        }

        static Color Blend(Color a, Color b, float t) => Color.FromArgb(
            (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
    }
}
