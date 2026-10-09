// EXPLORACIÓN y HORARIOS con el estilo común: trenes en tarjetas (vista 2D de la cabeza, tipo y datos), ficha
// técnica del elegido, panel de salidas tipo teleindicador y el itinerario del tren con sus paradas (leídas del
// propio archivo del horario).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SelectOR
{
    public sealed class TrainSpec
    {
        public string Traction = "", Service = "";
        public double Kw, Kmh, Capacity, MassT, LengthM;   // masa (t) y longitud (m) del tren completo
        public int Cars, Engines;
        public bool Freight, Automotor, Known;
    }

    // ------------------------------------------------------------------ trenes (Conducción libre)
    public class TrainCardGrid : CardListBase
    {
        public Func<object, string> PathOf, KeyOf;              // .eng de la cabeza · clave del tren (su .con)
        public Func<object, Task<TrainSpec>> SpecOf;
        public Func<object, bool> FavOf;
        public Func<object, List<string>> CompaniesOf;
        public Func<object, string> PriceOf, BadgeOf;   // Compra: precio del tren y «Tienes 2»
        public Func<object, string> WarnOf;             // aviso en naranja (p. ej. «⚠ Falta: …»)
        public VehicleThumbs Thumbs;
        public string LblPax = "VIAJEROS", LblFreight = "MERCANCÍAS", LblCars = "{0} coches", LblSeats = "plazas", LblLoading = "Calculando…";
        readonly Dictionary<string, TrainSpec> _spec = new Dictionary<string, TrainSpec>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> _pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly ThumbMemory _mem = new ThumbMemory(160);
        int _busy;
        readonly Font _fT = Theme.Font(9.75f, FontStyle.Bold), _fTag = Theme.Font(7.25f, FontStyle.Bold), _fS = Theme.Font(8.25f), _fSB = Theme.Font(8.25f, FontStyle.Bold), _fStar = Theme.Font(11f, FontStyle.Bold), _fCo = Theme.Font(7.5f, FontStyle.Bold);
        public TrainCardGrid() { BackColor = Theme.Bg; }
        protected override void Dispose(bool disposing) { if (disposing) { _mem.Dispose(); foreach (var f in new[] { _fT, _fTag, _fS, _fSB, _fStar, _fCo }) f.Dispose(); } base.Dispose(disposing); }
        // Tarjetas (cuadrícula) o lista (una fila por tren, más trenes a la vista).
        bool _list;
        public bool ListMode { get => _list; set { if (_list == value) return; _list = value; Relayout(); Invalidate(); } }
        protected override int MinCardW => Theme.Px(230);
        protected override int MaxCols => _list ? 1 : 99;
        protected override int CardH => _list ? Theme.Px(40) : Theme.Px(PriceOf != null ? 172 : 148);   // Compra: una fila más (precio)
        protected override int Gap => _list ? Theme.Px(4) : base.Gap;

        public TrainSpec CachedSpec(object it) { string k = KeyOf?.Invoke(it); return k != null && _spec.TryGetValue(k, out var s) ? s : null; }
        // Estos trenes han cambiado (Editor): sus tarjetas vuelven a pedir los datos.
        public void ForgetSpecs(IEnumerable<string> keys) { bool any = false; foreach (var k in keys) any |= _spec.Remove(k); if (any) Invalidate(); }

        protected override void PaintCard(Graphics g, int i, Rectangle rc, bool sel, bool hov)
        {
            if (_list) { PaintRow(g, i, rc, sel, hov); return; }
            var it = Items[i];
            int rad = Theme.Px(12);
            Fill(g, rc, rad, sel ? CardPaint.Sel : hov ? Color.FromArgb(58, 62, 66) : Color.FromArgb(52, 56, 60));
            if (sel) Stroke(g, rc, rad, Theme.Accent);
            else if (ShowFocus && i == SelectedIndex) Stroke(g, rc, rad, Theme.AccentHi, 1f);
            int pad = Theme.Px(10), x = rc.X + pad, w = rc.Width - pad * 2;
            string path = PathOf?.Invoke(it) ?? "";
            var rail = new Rectangle(x, rc.Y + pad, w, Theme.Px(62));
            CardPaint.Rail_(g, rail, _mem.Get(path), _mem.Failed(path) || path.Length == 0 ? "🚆" : null, _fS);
            var sp = CachedSpec(it);
            // tipo (viajeros / mercancías) sobre la imagen
            if (sp != null && sp.Known)
            {
                string t = sp.Freight ? LblFreight : LblPax;
                var c = sp.Freight ? Color.FromArgb(251, 146, 60) : Color.FromArgb(120, 144, 226);
                int tw = TW(t, _fTag) + Theme.Px(12);
                var tr = new Rectangle(rail.X + Theme.Px(6), rail.Y + Theme.Px(6), tw, Theme.Px(16));
                Fill(g, tr, Theme.Px(5), Color.FromArgb(190, 16, 18, 20));
                TextRenderer.DrawText(g, t, _fTag, tr, c, C1);
            }
            if (FavOf?.Invoke(it) == true)
            {
                var sr = new Rectangle(rail.Right - Theme.Px(26), rail.Y + Theme.Px(4), Theme.Px(22), Theme.Px(20));
                Fill(g, sr, Theme.Px(10), Color.FromArgb(160, 0, 0, 0));
                TextRenderer.DrawText(g, "★", _fStar, sr, Theme.Gold, C1);
            }
            int y = rail.Bottom + Theme.Px(8);
            TextRenderer.DrawText(g, it.ToString(), _fT, new Rectangle(x, y, w, Theme.Px(20)), Theme.Text, L1);
            y += Theme.Px(24);
            // datos
            int cx = x;
            if (sp == null) TextRenderer.DrawText(g, LblLoading, _fS, new Rectangle(x, y, w, Theme.Px(18)), Theme.Subtle, L1);
            else
            {
                var parts = new List<string>();
                if (!string.IsNullOrEmpty(sp.Traction)) parts.Add(sp.Traction);
                if (sp.Kmh > 0) parts.Add(sp.Kmh.ToString("N0", CultureInfo.GetCultureInfo("es-ES")) + " km/h");
                if (!sp.Freight && sp.Capacity > 0) parts.Add(sp.Capacity.ToString("N0", CultureInfo.GetCultureInfo("es-ES")) + " " + LblSeats);
                else if (sp.Cars > 0) parts.Add(string.Format(LblCars, sp.Cars));
                foreach (var p in parts) Pill(g, ref cx, y, x + w, p, _fS, Theme.Subtle, CardPaint.Rail, Theme.Px(20));
            }
            y += Theme.Px(26);
            // lo que le falta (si es incompleto), las empresas que lo tienen y, en Compra, precio y distintivo
            cx = x;
            string warn = WarnOf?.Invoke(it);
            if (!string.IsNullOrEmpty(warn)) Pill(g, ref cx, y, x + w, warn, _fCo, Color.FromArgb(251, 146, 60), Color.FromArgb(55, 251, 146, 60), Theme.Px(18));
            var cos = CompaniesOf?.Invoke(it);
            if (cos != null) foreach (var co in cos) Pill(g, ref cx, y, x + w, "🏢 " + co, _fCo, Theme.AccentHi, Color.FromArgb(40, 76, 175, 80), Theme.Px(18));
            PriceAndBadge(g, it, cx, y, x + w);
        }

        // Compra: el precio del tren y, si la empresa ya lo tiene, cuántos.
        void PriceAndBadge(Graphics g, object it, int x, int y, int right)
        {
            string price = PriceOf?.Invoke(it), badge = BadgeOf?.Invoke(it);
            int cx = x;
            if (!string.IsNullOrEmpty(price)) Pill(g, ref cx, y, right, price, _fCo, Theme.Text, CardPaint.Rail, Theme.Px(18));
            if (!string.IsNullOrEmpty(badge)) Pill(g, ref cx, y, right, badge, _fCo, Theme.AccentHi, Color.FromArgb(40, 76, 175, 80), Theme.Px(18));
        }

        // Modo lista (sin vistas 2D, más ligera): ★ · nombre · tipo · datos · empresas
        void PaintRow(Graphics g, int i, Rectangle rc, bool sel, bool hov)
        {
            var it = Items[i];
            int rad = Theme.Px(9);
            Fill(g, rc, rad, sel ? CardPaint.Sel : hov ? Color.FromArgb(58, 62, 66) : Color.FromArgb(48, 52, 56));
            if (sel) Stroke(g, rc, rad, Theme.Accent);
            else if (ShowFocus && i == SelectedIndex) Stroke(g, rc, rad, Theme.AccentHi, 1f);
            int pad = Theme.Px(12), right = rc.Right - pad;
            if (FavOf?.Invoke(it) == true)
            {
                TextRenderer.DrawText(g, "★", _fStar, new Rectangle(right - Theme.Px(20), rc.Y, Theme.Px(20), rc.Height), Theme.Gold, C1);
                right -= Theme.Px(26);
            }
            int x = rc.X + pad, cy = rc.Y + (rc.Height - Theme.Px(20)) / 2;
            string name = it.ToString();
            int nameW = Math.Min(TW(name, _fT) + Theme.Px(4), Math.Max(Theme.Px(120), (int)((right - x) * 0.45)));
            TextRenderer.DrawText(g, name, _fT, new Rectangle(x, rc.Y, nameW, rc.Height), Theme.Text, L1);
            int cx = x + nameW + Theme.Px(12);
            var sp = CachedSpec(it);
            if (sp == null) { TextRenderer.DrawText(g, LblLoading, _fS, new Rectangle(cx, rc.Y, Math.Max(0, right - cx), rc.Height), Theme.Subtle, L1); return; }
            if (sp.Known)
            {
                string t = sp.Freight ? LblFreight : LblPax;
                var c = sp.Freight ? Color.FromArgb(251, 146, 60) : Color.FromArgb(120, 144, 226);
                Pill(g, ref cx, cy, right, t, _fTag, c, Color.FromArgb(60, c), Theme.Px(20));
            }
            var es = CultureInfo.GetCultureInfo("es-ES");
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(sp.Traction)) parts.Add(sp.Traction);
            if (sp.Kmh > 0) parts.Add(sp.Kmh.ToString("N0", es) + " km/h");
            if (!sp.Freight && sp.Capacity > 0) parts.Add(sp.Capacity.ToString("N0", es) + " " + LblSeats);
            else if (sp.Cars > 0) parts.Add(string.Format(LblCars, sp.Cars));
            foreach (var p in parts) Pill(g, ref cx, cy, right, p, _fS, Theme.Subtle, CardPaint.Rail, Theme.Px(20));
            string warnL = WarnOf?.Invoke(it);
            if (!string.IsNullOrEmpty(warnL)) Pill(g, ref cx, cy + Theme.Px(1), right, warnL, _fCo, Color.FromArgb(251, 146, 60), Color.FromArgb(55, 251, 146, 60), Theme.Px(18));
            var cos = CompaniesOf?.Invoke(it);
            if (cos != null) foreach (var co in cos) Pill(g, ref cx, cy + Theme.Px(1), right, "🏢 " + co, _fCo, Theme.AccentHi, Color.FromArgb(40, 76, 175, 80), Theme.Px(18));
            PriceAndBadge(g, it, cx, cy + Theme.Px(1), right);
        }

        protected override void AfterPaint(Graphics g, int first, int last)
        {
            int pre = Math.Min(Items.Count - 1, last + Cols);
            var visible = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = first; i <= last; i++) visible.Add(PathOf?.Invoke(Items[i]) ?? "");
            for (int i = first; i <= pre; i++)
            {
                var it = Items[i];
                string p = PathOf?.Invoke(it) ?? "";
                if (p.Length > 0 && !_list) _mem.Request(Thumbs, p, Theme.Px(52), CardW - Theme.Px(30), Invalidate, k => visible.Contains(k));
                RequestSpec(it);
            }
        }

        // Pantalla de inicio: vistas 2D y datos de los primeros trenes de la lista, ya en memoria al abrir.
        public async Task PrefetchAsync(int count)
        {
            var items = Items.Take(Math.Min(count, Items.Count)).ToList();
            var jobs = new List<Task>();
            foreach (var it in items)
            {
                string p = PathOf?.Invoke(it) ?? "";
                if (p.Length > 0 && !_list) jobs.Add(_mem.Preload(Thumbs, p, Theme.Px(52), Theme.Px(400)));   // en lista no hay vistas 2D
                string k = KeyOf?.Invoke(it);
                if (SpecOf != null && !string.IsNullOrEmpty(k) && !_spec.ContainsKey(k))
                    jobs.Add(SpecOf(it).ContinueWith(t => { if (t.Status == TaskStatus.RanToCompletion) return t.Result; return null; })
                                       .ContinueWith(t => { var s = t.Result; try { BeginInvoke((Action)(() => { if (!_spec.ContainsKey(k)) _spec[k] = s ?? new TrainSpec(); })); } catch { } }));
            }
            try { await Task.WhenAll(jobs); } catch { }
            if (!IsDisposed) Invalidate();
        }

        async void RequestSpec(object it)
        {
            string k = KeyOf?.Invoke(it);
            if (SpecOf == null || string.IsNullOrEmpty(k) || _spec.ContainsKey(k) || _pending.Contains(k) || _busy >= 2) return;
            _pending.Add(k); _busy++;
            TrainSpec s = null;
            try { s = await SpecOf(it); } catch { }
            _busy--; _pending.Remove(k);
            if (IsDisposed) return;
            _spec[k] = s ?? new TrainSpec();
            Invalidate();
        }
    }

    // ------------------------------------------------------------------ conmutador tarjetas / lista
    public class ViewModeToggle : Control
    {
        int _mode, _hot = -1;
        public event EventHandler ModeChanged;
        public string TipCards = "Ver en tarjetas", TipList = "Ver en lista";
        readonly ToolTip _tip = new ToolTip();
        public int Mode { get => _mode; set { value = value == 1 ? 1 : 0; if (_mode == value) return; _mode = value; Invalidate(); } }
        public ViewModeToggle()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Size = new Size(Theme.Px(72), Theme.Px(34)); Cursor = Cursors.Hand; BackColor = Theme.Bg;
        }
        protected override void Dispose(bool disposing) { if (disposing) _tip.Dispose(); base.Dispose(disposing); }
        Rectangle Seg(int k) { int w = (Width - Theme.Px(6)) / 2; return new Rectangle(Theme.Px(3) + k * w, Theme.Px(3), w, Height - Theme.Px(6)); }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int h = Seg(0).Contains(e.Location) ? 0 : Seg(1).Contains(e.Location) ? 1 : -1;
            if (h != _hot) { _hot = h; Invalidate(); if (h >= 0) _tip.SetToolTip(this, h == 1 ? TipList : TipCards); }
        }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hot = -1; Invalidate(); }
        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            int k = Seg(1).Contains(e.Location) ? 1 : 0;
            if (k != _mode) { _mode = k; Invalidate(); ModeChanged?.Invoke(this, EventArgs.Empty); }
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Theme.FillRound(g, new Rectangle(0, 0, Width - 1, Height - 1), Theme.Px(9), Theme.Surface2);
            for (int k = 0; k < 2; k++)
            {
                var r = Seg(k);
                if (k == _mode) Theme.FillRound(g, r, Theme.Px(7), Theme.Accent);
                else if (k == _hot) Theme.FillRound(g, r, Theme.Px(7), Theme.SurfaceHi);
                var col = k == _mode ? Color.White : Theme.Text;
                int s = Theme.Px(14), x0 = r.X + (r.Width - s) / 2, y0 = r.Y + (r.Height - s) / 2;
                if (k == 0)   // cuadrícula 2×2
                {
                    int q = (s - Theme.Px(2)) / 2;
                    foreach (var (dx, dy) in new[] { (0, 0), (1, 0), (0, 1), (1, 1) })
                        Theme.FillRound(g, new Rectangle(x0 + dx * (q + Theme.Px(2)), y0 + dy * (q + Theme.Px(2)), q, q), Theme.Px(2), col);
                }
                else          // lista: tres renglones
                {
                    using var b = new SolidBrush(col);
                    int lh = Math.Max(2, Theme.Px(3));
                    for (int j = 0; j < 3; j++)
                    {
                        int yy = y0 + j * (s - lh) / 2;
                        g.FillRectangle(b, x0, yy, lh, lh);
                        g.FillRectangle(b, x0 + lh + Theme.Px(2), yy, s - lh - Theme.Px(2), lh);
                    }
                }
            }
            g.SmoothingMode = SmoothingMode.None;
        }
    }

    // ------------------------------------------------------------------ ficha técnica (casillas)
    public class SpecTiles : Control
    {
        public List<(string cap, string val)> Items = new List<(string, string)>();
        public Dictionary<int, Color> ValueColors = new Dictionary<int, Color>();   // color del valor de alguna casilla (p. ej. el estado)
        public int Columns = 3;
        readonly Font _fC = Theme.Font(7.5f, FontStyle.Bold), _fV = Theme.Font(10.5f, FontStyle.Bold);
        public SpecTiles() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); }
        protected override void Dispose(bool disposing) { if (disposing) { _fC.Dispose(); _fV.Dispose(); } base.Dispose(disposing); }
        public int NeededHeight => Items.Count == 0 ? 0 : ((Items.Count + Columns - 1) / Columns) * (Theme.Px(46) + Theme.Px(6));
        public void SetItems(List<(string, string)> items) { Items = items ?? new(); int h = NeededHeight; if (Height != h) Height = h; Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(Parent?.BackColor ?? Theme.Surface);
            int gap = Theme.Px(6), cw = (Width - gap * (Columns - 1)) / Columns, ch = Theme.Px(46);
            const TextFormatFlags L = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
            for (int i = 0; i < Items.Count; i++)
            {
                var r = new Rectangle((i % Columns) * (cw + gap), (i / Columns) * (ch + gap), cw, ch);
                g.SmoothingMode = SmoothingMode.AntiAlias; Theme.FillRound(g, r, Theme.Px(9), Color.FromArgb(52, 56, 60)); g.SmoothingMode = SmoothingMode.None;
                TextRenderer.DrawText(g, Items[i].cap, _fC, new Rectangle(r.X + Theme.Px(10), r.Y + Theme.Px(6), r.Width - Theme.Px(14), Theme.Px(14)), Theme.Subtle, L);
                TextRenderer.DrawText(g, Items[i].val, _fV, new Rectangle(r.X + Theme.Px(10), r.Y + Theme.Px(21), r.Width - Theme.Px(14), Theme.Px(20)), ValueColors.TryGetValue(i, out var vc) ? vc : Theme.Text, L);
            }
        }
    }

    // ------------------------------------------------------------------ horarios: paradas de cada tren
    public static class TtStops
    {
        public sealed class Table { public string[] Head; public List<string[]> Rows = new List<string[]>(); }
        public sealed class Stop { public string Station = "", Arr = "", Dep = ""; }
        static readonly Dictionary<string, (DateTime when, Table t)> _cache = new Dictionary<string, (DateTime, Table)>(StringComparer.OrdinalIgnoreCase);
        static readonly Regex TimeRx = new Regex(@"(\d{1,2}:\d{2})(?::\d{2})?(?:\s*-\s*(\d{1,2}:\d{2})(?::\d{2})?)?", RegexOptions.Compiled);

        // El .timetable_or de cada horario de un conjunto (si el conjunto es una lista, la lee).
        public static List<string> FilesOf(string setFile)
        {
            var list = new List<string>();
            try
            {
                if (string.IsNullOrEmpty(setFile) || !File.Exists(setFile)) return list;
                string ext = Path.GetExtension(setFile).ToLowerInvariant();
                if (!ext.Contains("list")) { list.Add(setFile); return list; }
                string dir = Path.GetDirectoryName(setFile);
                foreach (var raw in File.ReadAllLines(setFile))
                {
                    string l = raw.Trim().Trim('"');
                    if (l.Length == 0 || l.StartsWith("#")) continue;
                    string f = Path.IsPathRooted(l) ? l : Path.Combine(dir, l);
                    if (File.Exists(f)) list.Add(f);
                }
            }
            catch { }
            return list;
        }

        public static Table Load(string file)
        {
            try
            {
                if (string.IsNullOrEmpty(file) || !File.Exists(file)) return null;
                var when = File.GetLastWriteTimeUtc(file);
                lock (_cache) if (_cache.TryGetValue(file, out var c) && c.when == when) return c.t;
                var lines = File.ReadAllLines(file);
                if (lines.Length == 0) return null;
                char sep = new[] { ';', ',', '\t' }.OrderByDescending(ch => lines[0].Count(x => x == ch)).First();
                var t = new Table();
                foreach (var l in lines)
                {
                    var cells = l.Split(sep).Select(x => x.Trim().Trim('"').Trim()).ToArray();
                    if (t.Head == null) { t.Head = cells; continue; }
                    t.Rows.Add(cells);
                }
                lock (_cache) _cache[file] = (when, t);
                return t;
            }
            catch { return null; }
        }

        // Columna de un tren: por su nombre en la cabecera; si no, la que dice Open Rails.
        public static int ColumnOf(Table t, string train, int orColumn)
        {
            if (t?.Head == null) return -1;
            for (int i = 1; i < t.Head.Length; i++) if (string.Equals(t.Head[i], train, StringComparison.OrdinalIgnoreCase)) return i;
            for (int i = 1; i < t.Head.Length; i++) if (!string.IsNullOrEmpty(t.Head[i]) && train != null && t.Head[i].Split(' ', '$')[0].Equals(train.Split(' ', '$')[0], StringComparison.OrdinalIgnoreCase)) return i;
            return orColumn > 0 && orColumn < t.Head.Length ? orColumn : -1;
        }

        // Paradas de un tren, con las mismas reglas que Open Rails (ProcessTimetable.cs, StopInfo):
        //  · «hh:mm» (llegada = salida) o «hh:mm-hh:mm» (llegada-salida); lo que va tras «$» son órdenes;
        //  · «P…» («P10:30», «10P30»): pasa SIN parar → no es parada;
        //  · «*» o «x» solos: para, sin hora («—»); «10*30» o «10x30»: para a esa hora.
        public const string NoTime = "—";
        public static List<Stop> StopsOf(Table t, int col)
        {
            var list = new List<Stop>();
            if (t == null || col < 1) return list;
            foreach (var row in t.Rows)
            {
                if (row.Length <= col || row.Length == 0) continue;
                string st = row[0];
                if (string.IsNullOrEmpty(st) || st.StartsWith("#")) continue;
                int dollar = st.IndexOf('$'); if (dollar >= 0) st = st.Substring(0, dollar).Trim();
                if (st.Length == 0) continue;
                string cell = row[col] ?? "";
                int cmd = cell.IndexOf('$'); if (cmd >= 0) cell = cell.Substring(0, cmd);
                cell = cell.Trim();
                if (cell.Length == 0) continue;
                string first = cell.Split('-')[0].Trim();
                if (first.Contains('P')) continue;                                      // pasa sin parar
                if (first == "*" || first == "x") { list.Add(new Stop { Station = st, Arr = NoTime, Dep = NoTime }); continue; }
                var m = TimeRx.Match(cell.Replace('*', ':').Replace('x', ':'));
                if (!m.Success) continue;
                string a = m.Groups[1].Value, d = m.Groups[2].Success ? m.Groups[2].Value : a;
                list.Add(new Stop { Station = st, Arr = Norm(a), Dep = Norm(d) });
            }
            return InTravelOrder(list);
        }

        // Las filas del horario no tienen por qué ir en el orden de marcha (los trenes del otro sentido van de abajo
        // arriba, y hay horarios con las estaciones en cualquier orden): se ordenan por la hora. El tren empieza
        // después del mayor hueco del día, así 23:50 → 00:10 sigue en orden. Una parada sin hora («*», «x») va
        // detrás de la fila con hora que tenía encima.
        static List<Stop> InTravelOrder(List<Stop> list)
        {
            if (list.Count < 2) return list;
            var key = new double[list.Count];
            double last = double.NaN;
            for (int i = 0; i < list.Count; i++)
            {
                int m = Minutes(list[i].Arr); if (m < 0) m = Minutes(list[i].Dep);
                key[i] = m >= 0 ? m : (double.IsNaN(last) ? -1 : last + 0.001 * i);
                if (m >= 0) last = m;
            }
            var timed = key.Where(k => k >= 0).OrderBy(k => k).ToList();
            double start = timed.Count > 0 ? timed[0] : 0;
            if (timed.Count > 1)
            {
                double gap = timed[0] + 1440 - timed[^1];   // el hueco que cruza la medianoche
                for (int i = 1; i < timed.Count; i++) if (timed[i] - timed[i - 1] > gap) { gap = timed[i] - timed[i - 1]; start = timed[i]; }
            }
            return list.Select((s, i) => (s, k: key[i] < 0 ? -1 : (key[i] - start + 1440) % 1440, i))
                       .OrderBy(x => x.k).ThenBy(x => x.i).Select(x => x.s).ToList();
        }
        static string Norm(string hm) { var p = hm.Split(':'); return p.Length == 2 ? int.Parse(p[0]).ToString("00") + ":" + p[1] : hm; }
        public static int Minutes(string hm) { var p = (hm ?? "").Split(':'); return p.Length >= 2 && int.TryParse(p[0], out var h) && int.TryParse(p[1], out var m) ? h * 60 + m : -1; }
    }

    // ------------------------------------------------------------------ panel de salidas (teleindicador)
    public class DepartureBoard : CardListBase
    {
        public sealed class Row { public object Train; public string Time = "", Name = "", From = "", To = "", Via = "", Consist = ""; public int Stops = -1; }
        public string Title = "SALIDAS", Hint = "", LblStops = "{0} paradas", LblStop = "1 parada", LblNoStops = "sin paradas";
        public string[] Heads = { "HORA", "TREN", "RECORRIDO", "PARADAS", "COMPOSICIÓN" };
        static readonly Color Amber = Color.FromArgb(255, 196, 64), BoardBg = Color.FromArgb(11, 12, 13), Line = Color.FromArgb(24, 27, 29);
        readonly Font _fH = Theme.Font(7.75f, FontStyle.Bold), _fTitle = Theme.Font(8.5f, FontStyle.Bold), _fTime = new Font("Consolas", 14f * Theme.UiScale * Theme.DpiComp, FontStyle.Bold),
                      _fName = new Font("Consolas", 10f * Theme.UiScale * Theme.DpiComp, FontStyle.Bold), _fD = Theme.Font(10f, FontStyle.Bold), _fDs = Theme.Font(8f), _fP = Theme.Font(8.75f);
        public DepartureBoard() { BackColor = BoardBg; }
        protected override void Dispose(bool disposing) { if (disposing) foreach (var f in new[] { _fH, _fTitle, _fTime, _fName, _fD, _fDs, _fP }) f.Dispose(); base.Dispose(disposing); }
        protected override int MaxCols => 1;
        protected override int CardH => Theme.Px(50);
        protected override int Gap => 0;
        int HeadH => Theme.Px(56);
        protected override int TopOffset => HeadH;

        public int NameW;   // ancho de la columna TREN (lo justo para los nombres de este horario)
        // HORA · TREN (a su medida) · RECORRIDO · PARADAS · COMPOSICIÓN: el recorrido no se queda todo el sitio,
        // así la composición se lee entera.
        int[] Cols5(int w)
        {
            int gaps = Theme.Px(10) * 4 + Theme.Px(28);
            int t = Theme.Px(70), n = Math.Max(Theme.Px(80), Math.Min(Theme.Px(170), NameW > 0 ? NameW : Theme.Px(150))), p = Theme.Px(90);
            int rest = Math.Max(Theme.Px(200), w - t - n - p - gaps);
            int route = Math.Min(Theme.Px(420), (int)(rest * 0.58));
            return new[] { t, n, route, p, rest - route };
        }

        protected override void PaintCard(Graphics g, int i, Rectangle rc, bool sel, bool hov)
        {
            if (rc.Bottom < HeadH) return;
            var r = Items[i] as Row; if (r == null) return;
            var bg = sel ? Color.FromArgb(28, 52, 32) : hov ? Color.FromArgb(22, 24, 26) : (i % 2 == 0 ? BoardBg : Color.FromArgb(15, 17, 18));
            using (var b = new SolidBrush(bg)) g.FillRectangle(b, rc);
            if (sel) using (var b = new SolidBrush(Theme.Accent)) g.FillRectangle(b, rc.X, rc.Y, Theme.Px(3), rc.Height);
            using (var p = new Pen(Line)) g.DrawLine(p, rc.X, rc.Bottom - 1, rc.Right, rc.Bottom - 1);
            var cw = Cols5(rc.Width);
            int x = rc.X + Theme.Px(14), gap = Theme.Px(10);
            TextRenderer.DrawText(g, r.Time, _fTime, new Rectangle(x, rc.Y, cw[0], rc.Height), Amber, L1); x += cw[0] + gap;
            TextRenderer.DrawText(g, r.Name, _fName, new Rectangle(x, rc.Y, cw[1], rc.Height), Color.White, L1); x += cw[1] + gap;
            string route = r.From.Length > 0 ? r.From + (r.To.Length > 0 && r.To != r.From ? "  →  " + r.To : "") : r.Via;
            TextRenderer.DrawText(g, route, _fD, new Rectangle(x, rc.Y + Theme.Px(7), cw[2], Theme.Px(20)), Color.White, L1);
            TextRenderer.DrawText(g, r.From.Length > 0 ? r.Via : "", _fDs, new Rectangle(x, rc.Y + Theme.Px(27), cw[2], Theme.Px(16)), Color.FromArgb(138, 143, 148), L1);
            x += cw[2] + gap;
            string st = r.Stops < 0 ? "—" : r.Stops == 0 ? LblNoStops : r.Stops == 1 ? LblStop : string.Format(LblStops, r.Stops);
            TextRenderer.DrawText(g, st, _fP, new Rectangle(x, rc.Y, cw[3], rc.Height), r.Stops > 0 ? Color.FromArgb(207, 211, 214) : Color.FromArgb(110, 115, 120), L1); x += cw[3] + gap;
            TextRenderer.DrawText(g, r.Consist, _fDs, new Rectangle(x, rc.Y, rc.Right - x - Theme.Px(10), rc.Height), Color.FromArgb(138, 143, 148), L1);
        }

        protected override void AfterPaint(Graphics g, int first, int last) => PaintHead(g);

        void PaintHead(Graphics g)
        {
            var hr = new Rectangle(0, 0, ClientSize.Width, HeadH);
            using (var b = new SolidBrush(BoardBg)) g.FillRectangle(b, hr);
            int x = Theme.Px(14);
            TextRenderer.DrawText(g, Title, _fTitle, new Rectangle(x, Theme.Px(8), hr.Width / 2, Theme.Px(18)), Amber, L1);
            TextRenderer.DrawText(g, Hint, _fH, new Rectangle(hr.Width / 2, Theme.Px(8), hr.Width / 2 - Theme.Px(14), Theme.Px(18)), Color.FromArgb(138, 143, 148), R1);
            var cw = Cols5(hr.Width - Theme.Px(4));
            int gap = Theme.Px(10);
            for (int i = 0; i < Heads.Length; i++)
            {
                TextRenderer.DrawText(g, Heads[i], _fH, new Rectangle(x, Theme.Px(32), cw[i], Theme.Px(18)), Color.FromArgb(138, 143, 148), L1);
                x += cw[i] + gap;
            }
            using (var p = new Pen(Color.FromArgb(36, 39, 42))) g.DrawLine(p, 0, HeadH - 1, hr.Width, HeadH - 1);
        }

    }

    // ------------------------------------------------------------------ itinerario del tren elegido
    public class ItineraryView : CanvasPanel
    {
        public string Badge = "", Title = "", Sub = "", Empty = "Elige un tren", LblOrigin = "origen", LblDest = "destino", LblNoStops = "Este horario no detalla las paradas de este tren.", BriefCap = "RESUMEN";
        public List<TtStops.Stop> Stops = new List<TtStops.Stop>();
        public List<string> Brief = new List<string>();
        Font _fBadge, _fT, _fS, _fTime, _fTime2, _fN, _fNs, _fW, _fCap, _fP;
        public ItineraryView()
        {
            _fBadge = F(8f, FontStyle.Bold); _fT = F(13.5f, FontStyle.Bold); _fS = F(8.5f); _fN = F(9.75f, FontStyle.Bold); _fNs = F(8f); _fW = F(7.75f); _fCap = F(8f, FontStyle.Bold); _fP = F(9f);
            _fTime = new Font("Consolas", 10.5f * Theme.UiScale * Theme.DpiComp, FontStyle.Bold); _fTime2 = new Font("Consolas", 8.5f * Theme.UiScale * Theme.DpiComp);
            BackColor = Theme.Surface;
        }
        protected override void Dispose(bool disposing) { if (disposing) { _fTime.Dispose(); _fTime2.Dispose(); } base.Dispose(disposing); }
        int RowH => Theme.Px(40);
        int ParaH(string p, int w) => TextRenderer.MeasureText(p, _fP, new Size(w, 100000), Wrap).Height;
        protected override int DoLayout(int w)
        {
            int y = Theme.Px(12) + Theme.Px(28) + Theme.Px(22) + Theme.Px(10);
            y += Stops.Count > 0 ? Stops.Count * RowH : Theme.Px(30);
            if (Brief.Count > 0) { y += Theme.Px(30); foreach (var p in Brief) y += ParaH(p, w - Theme.Px(28)) + Theme.Px(6); }
            return y + Theme.Px(12);
        }
        public void SetContent(string badge, string title, string sub, List<TtStops.Stop> stops, List<string> brief)
        {
            Badge = badge ?? ""; Title = title ?? ""; Sub = sub ?? ""; Stops = stops ?? new(); Brief = brief ?? new();
            AutoScrollPosition = new Point(0, 0);
            Relayout(true);
        }
        protected override void DoPaint(Graphics g, int top)
        {
            int x = Theme.Px(14), w = W - Theme.Px(28), y = Theme.Px(12) - top;
            if (string.IsNullOrEmpty(Title)) { Text(g, Empty, _fS, new Rectangle(x, y, w, Theme.Px(24)), Theme.Subtle, L1); return; }
            int bx = x;
            if (Badge.Length > 0)
            {
                int bw = TW(Badge, _fBadge) + Theme.Px(12);
                var br = new Rectangle(x, y + Theme.Px(4), bw, Theme.Px(20));
                Round(g, br, Theme.Px(5), Orange); Text(g, Badge, _fBadge, br, Color.FromArgb(20, 20, 20), C1);
                bx = br.Right + Theme.Px(10);
            }
            Text(g, Title, _fT, new Rectangle(bx, y, x + w - bx, Theme.Px(28)), Theme.Text, L1);
            y += Theme.Px(30);
            Text(g, Sub, _fS, new Rectangle(x, y, w, Theme.Px(18)), Theme.Subtle, L1);
            y += Theme.Px(30);
            if (Stops.Count == 0) { Text(g, LblNoStops, _fS, new Rectangle(x, y, w, Theme.Px(20)), Theme.Subtle, L1); y += Theme.Px(30); }
            else
            {
                int tx = x, tw = Theme.Px(52), lx = x + tw + Theme.Px(14), nx = lx + Theme.Px(18);
                using (var p = new Pen(Color.FromArgb(140, 102, 197, 106), Theme.Px(3)))
                    g.DrawLine(p, lx, y + RowH / 2, lx, y + (Stops.Count - 1) * RowH + RowH / 2);
                for (int i = 0; i < Stops.Count; i++)
                {
                    var s = Stops[i]; int cy = y + i * RowH + RowH / 2;
                    bool end = i == 0 || i == Stops.Count - 1;
                    string main = i == 0 ? s.Dep : s.Arr;
                    bool dwell = !end && s.Dep != s.Arr;
                    Text(g, main, _fTime, new Rectangle(tx, cy - Theme.Px(dwell ? 15 : 9), tw, Theme.Px(18)), Theme.Text, R1);
                    if (dwell) Text(g, s.Dep, _fTime2, new Rectangle(tx, cy + Theme.Px(2), tw, Theme.Px(14)), Theme.Subtle, R1);
                    int d = Theme.Px(12);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using (var b = new SolidBrush(end ? Theme.AccentHi : Theme.Surface)) g.FillEllipse(b, lx - d / 2, cy - d / 2, d, d);
                    using (var p = new Pen(Theme.AccentHi, Theme.Px(3))) g.DrawEllipse(p, lx - d / 2, cy - d / 2, d, d);
                    g.SmoothingMode = SmoothingMode.None;
                    string sub = i == 0 ? LblOrigin : i == Stops.Count - 1 ? LblDest : "";
                    Text(g, s.Station, _fN, new Rectangle(nx, cy - Theme.Px(sub.Length > 0 ? 15 : 9), x + w - nx - Theme.Px(56), Theme.Px(18)), Theme.Text, L1);
                    if (sub.Length > 0) Text(g, sub, _fNs, new Rectangle(nx, cy + Theme.Px(3), Theme.Px(120), Theme.Px(14)), Theme.Subtle, L1);
                    if (dwell)
                    {
                        int mins = TtStops.Minutes(s.Dep) - TtStops.Minutes(s.Arr); if (mins < 0) mins += 1440;
                        string wtxt = mins + " min"; int ww = TW(wtxt, _fW) + Theme.Px(12);
                        var wr = new Rectangle(x + w - ww, cy - Theme.Px(9), ww, Theme.Px(18));
                        Round(g, wr, Theme.Px(9), CardC); Text(g, wtxt, _fW, wr, Theme.Subtle, C1);
                    }
                }
                y += Stops.Count * RowH;
            }
            if (Brief.Count == 0) return;
            y += Theme.Px(10);
            Text(g, BriefCap, _fCap, new Rectangle(x, y, w, Theme.Px(16)), Theme.Subtle, L1);
            y += Theme.Px(20);
            foreach (var p in Brief)
            {
                int h = ParaH(p, w);
                TextRenderer.DrawText(g, p, _fP, new Rectangle(x, y, w, h), Color.FromArgb(205, 210, 214), Wrap);
                y += h + Theme.Px(6);
            }
        }
    }

    // ------------------------------------------------------------------ composición 2D del tren elegido
    // El tren entero de costado (todos sus vehículos), a lo ancho; clic = abrir la composición completa.
    public class ConsistStripView : Control
    {
        public Image Image;
        public string Caption = "", Hint = "", Placeholder = "";
        public bool Loading;
        readonly Font _fC = Theme.Font(7.75f, FontStyle.Bold), _fH = Theme.Font(8f), _fP = Theme.Font(9f);
        bool _hover;
        public ConsistStripView() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); Cursor = Cursors.Hand; }
        protected override void Dispose(bool disposing) { if (disposing) { _fC.Dispose(); _fH.Dispose(); _fP.Dispose(); } base.Dispose(disposing); }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }
        public void Set(Image img, string caption, bool loading) { Image = img; Caption = caption ?? ""; Loading = loading; Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent?.BackColor ?? Theme.Surface);
            const TextFormatFlags L = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
            int capH = Theme.Px(18);
            int hw = TextRenderer.MeasureText(Hint, _fH, Size.Empty, TextFormatFlags.NoPadding).Width + 4;
            TextRenderer.DrawText(g, Caption, _fC, new Rectangle(0, 0, Width - hw - 8, capH), Theme.Subtle, L);
            if (_hover && Image != null) TextRenderer.DrawText(g, Hint, _fH, new Rectangle(Width - hw, 0, hw, capH), Theme.AccentHi, L);
            var r = new Rectangle(0, capH + Theme.Px(4), Width - 1, Height - capH - Theme.Px(5));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Theme.FillRound(g, r, Theme.Px(10), CardPaint.Rail);
            if (_hover && Image != null) Theme.DrawRoundBorder(g, r, Theme.Px(10), Color.FromArgb(120, 102, 197, 106), 1.2f);
            g.SmoothingMode = SmoothingMode.None;
            // raíl
            using (var p = new Pen(Color.FromArgb(70, 255, 255, 255))) g.DrawLine(p, r.X + Theme.Px(10), r.Bottom - Theme.Px(10), r.Right - Theme.Px(10), r.Bottom - Theme.Px(10));
            if (Image == null)
            {
                TextRenderer.DrawText(g, Loading ? I18n.T("Dibujando la composición…") : Placeholder, _fP, r, Theme.Subtle, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }
            var area = Rectangle.Inflate(r, -Theme.Px(10), -Theme.Px(8));
            area.Height -= Theme.Px(2);
            double k = Math.Min(area.Width / (double)Image.Width, area.Height / (double)Image.Height);
            int w = (int)(Image.Width * k), h = (int)(Image.Height * k);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(Image, area.X + (area.Width - w) / 2, area.Bottom - h, w, h);
        }
    }

    // ------------------------------------------------------------------ lista de horarios (columna)
    public class TimetableList : CardListBase
    {
        public sealed class Entry { public int Set, File; public string Title = "", Sub = ""; public int Trains; public override string ToString() => Title; }
        readonly Font _fT = Theme.Font(9.5f, FontStyle.Bold), _fS = Theme.Font(8f), _fN = Theme.Font(9f, FontStyle.Bold);
        public TimetableList() { BackColor = Theme.Surface; }
        protected override void Dispose(bool disposing) { if (disposing) { _fT.Dispose(); _fS.Dispose(); _fN.Dispose(); } base.Dispose(disposing); }
        protected override int MaxCols => 1;
        protected override int CardH => Theme.Px(52);
        protected override int Gap => Theme.Px(6);
        protected override void PaintCard(Graphics g, int i, Rectangle rc, bool sel, bool hov)
        {
            var en = Items[i] as Entry; if (en == null) return;
            int rad = Theme.Px(9);
            Fill(g, rc, rad, sel ? Color.FromArgb(44, 62, 48) : hov ? Color.FromArgb(60, 64, 68) : Color.FromArgb(52, 56, 60));
            if (sel) { Stroke(g, rc, rad, Theme.Accent); using (var b = new SolidBrush(Theme.Accent)) g.FillRectangle(b, rc.X + 1, rc.Y + Theme.Px(10), Theme.Px(3), rc.Height - Theme.Px(20)); }
            else if (ShowFocus && i == SelectedIndex) Stroke(g, rc, rad, Theme.AccentHi, 1f);
            string n = en.Trains.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("es-ES"));
            int nw = TW(n, _fN) + Theme.Px(14);
            var nr = new Rectangle(rc.Right - nw - Theme.Px(10), rc.Y + (rc.Height - Theme.Px(20)) / 2, nw, Theme.Px(20));
            Fill(g, nr, Theme.Px(10), sel ? Color.FromArgb(60, 76, 175, 80) : Color.FromArgb(40, 255, 255, 255));
            TextRenderer.DrawText(g, n, _fN, nr, sel ? Theme.AccentHi : Theme.Subtle, C1);
            int x = rc.X + Theme.Px(14), w = nr.X - x - Theme.Px(8);
            TextRenderer.DrawText(g, en.Title, _fT, new Rectangle(x, rc.Y + Theme.Px(8), w, Theme.Px(19)), sel ? Theme.Text : Color.FromArgb(222, 226, 229), L1);
            TextRenderer.DrawText(g, en.Sub, _fS, new Rectangle(x, rc.Y + Theme.Px(28), w, Theme.Px(16)), Theme.Subtle, L1);
        }
    }

    // ------------------------------------------------------------------ avatar redondo (foto o iniciales)
    public class AvatarBox : Control
    {
        public Image Photo; public string NameText = "";
        public AvatarBox() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent?.BackColor ?? Theme.Surface);
            int d = Math.Min(Width, Height) - 2;
            var r = new Rectangle((Width - d) / 2, (Height - d) / 2, d, d);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = new GraphicsPath())
            {
                path.AddEllipse(r);
                if (Photo != null)
                {
                    var st = g.Save(); g.SetClip(path, CombineMode.Intersect);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    double k = Math.Max(r.Width / (double)Photo.Width, r.Height / (double)Photo.Height);
                    int w = (int)Math.Ceiling(Photo.Width * k), h = (int)Math.Ceiling(Photo.Height * k);
                    g.DrawImage(Photo, r.X + (r.Width - w) / 2, r.Y + (r.Height - h) / 4, w, h);
                    g.Restore(st);
                }
                else
                {
                    using (var b = new LinearGradientBrush(r, Color.FromArgb(70, 120, 80), Color.FromArgb(40, 70, 50), LinearGradientMode.ForwardDiagonal)) g.FillPath(b, path);
                    var p = (NameText ?? "").Split(new[] { ' ', '_', '.', '-' }, StringSplitOptions.RemoveEmptyEntries);
                    string ini = p.Length == 0 ? "?" : (p.Length == 1 ? p[0].Substring(0, Math.Min(2, p[0].Length)) : "" + p[0][0] + p[1][0]).ToUpperInvariant();
                    using var f = Theme.Font(Math.Max(7f, d / 3.2f / Theme.UiScale), FontStyle.Bold);
                    TextRenderer.DrawText(g, ini, f, r, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                }
                using (var pen = new Pen(Color.FromArgb(120, 102, 197, 106), 1.5f)) g.DrawEllipse(pen, r);
            }
        }
    }
}
