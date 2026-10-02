// FLOTA y COMPRA en tarjetas, con la vista 2D (de costado) de cada modelo.
//
//  · VehicleThumbs: la vista 2D de un .eng se dibuja UNA vez y se guarda en disco (caché de SelectOR);
//    la clave lleva la fecha y el tamaño del archivo, así que si cambias el modelo se rehace sola.
//    La geometría y las texturas se cargan en segundo plano (2 a la vez); solo el dibujo (unos ms) va
//    en el hilo de la interfaz, porque usa el dispositivo gráfico.
//  · FleetCardList: una tarjeta por modelo con sus unidades en chips de color (estado).
//  · BuyCardGrid: cuadrícula de máquinas en venta; imita lo que se usaba del ListBox (Items, SelectedItem,
//    SelectedIndex, TopIndex…) para que el resto de Compra siga igual. Los datos y el precio de cada
//    tarjeta se calculan al aparecer en pantalla (2 a la vez) y se guardan.
//  · VehicleViewport: el visor de la derecha; enseña la vista 2D y, solo si se pide, el 3D de siempre.
// Todo se dibuja a mano, solo lo visible, en doble búfer.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SelectOR
{
    // ------------------------------------------------------------------ vistas 2D en disco
    public sealed class VehicleThumbs
    {
        public const float Ppm = 20f, WorldH = 5.6f;     // 20 px por metro: ~112 px de alto
        readonly Control _ui;
        readonly Dictionary<string, Task<string>> _files = new Dictionary<string, Task<string>>(StringComparer.OrdinalIgnoreCase);
        readonly SemaphoreSlim _gate = new SemaphoreSlim(2);

        public VehicleThumbs(Control ui) { _ui = ui; }

        public static string Dir => Path.Combine(AppDataTidy.CacheRoot, "vistas2d");

        static string KeyFile(string path)
        {
            var fi = new FileInfo(path);
            string k = path.ToLowerInvariant() + "|" + fi.LastWriteTimeUtc.Ticks + "|" + fi.Length + "|" + Ppm;
            using var sha = System.Security.Cryptography.SHA1.Create();
            var h = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(k));
            return Path.Combine(Dir, "v1_" + BitConverter.ToString(h, 0, 10).Replace("-", "").ToLowerInvariant() + ".png");
        }

        // Archivo PNG de la vista 2D (lo crea si no existe); null si el modelo no se puede dibujar.
        public Task<string> FileAsync(string path)
        {
            if (string.IsNullOrEmpty(path)) return Task.FromResult<string>(null);
            lock (_files)
            {
                if (!_files.TryGetValue(path, out var t)) { t = Make(path); _files[path] = t; }
                return t;
            }
        }

        async Task<string> Make(string path)
        {
            try
            {
                string file = await Task.Run(() => File.Exists(path) ? KeyFile(path) : null);
                if (file == null) return null;
                if (File.Exists(file)) return file;
                await _gate.WaitAsync();
                try
                {
                    if (File.Exists(file)) return file;
                    var geom = await Task.Run(() =>
                    {
                        var g = ShapeRenderer.BuildGeometry(path);
                        try { ShapeRenderer.PrefetchTextures(g); } catch { }
                        return g;
                    });
                    if (geom == null || geom.IsEmpty || !_ui.IsHandleCreated) return null;
                    var tcs = new TaskCompletionSource<Bitmap>();
                    _ui.BeginInvoke((Action)(() =>
                    {
                        try { tcs.SetResult(ServiceImages.TrimX(ShapeRenderer.RenderSide(geom, Ppm, WorldH, true))); }
                        catch { tcs.SetResult(null); }
                    }));
                    var bmp = await tcs.Task;
                    if (bmp == null) return null;
                    await Task.Run(() =>
                    {
                        Directory.CreateDirectory(Dir);
                        string tmp = file + ".tmp";
                        using (bmp) bmp.Save(tmp, ImageFormat.Png);
                        File.Move(tmp, file, true);
                    });
                    return file;
                }
                finally { _gate.Release(); }
            }
            catch { return null; }
        }

        // Imagen a un alto dado (y, si se pasa, como mucho a un ancho), premultiplicada: la más rápida de pintar.
        public static Bitmap Decode(string file, int h, int maxW = 0)
        {
            try
            {
                if (string.IsNullOrEmpty(file) || !File.Exists(file)) return null;
                using var ms = new MemoryStream(File.ReadAllBytes(file));
                using var src = Image.FromStream(ms);
                h = Math.Min(h, src.Height);
                int w = Math.Max(1, (int)Math.Round(src.Width * (h / (double)src.Height)));
                if (maxW > 0 && w > maxW) { h = Math.Max(1, (int)Math.Round(h * (maxW / (double)w))); w = maxW; }
                var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(src, new Rectangle(0, 0, w, h));
                }
                return bmp;
            }
            catch { return null; }
        }
    }

    // Cajita común de las vistas 2D cargadas en memoria (por clave), con tope.
    sealed class ThumbMemory : IDisposable
    {
        readonly Dictionary<string, Bitmap> _img = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
        readonly LinkedList<string> _order = new LinkedList<string>();
        readonly HashSet<string> _pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase), _failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly int _max;
        int _busy;
        public ThumbMemory(int max) { _max = max; }
        public Bitmap Get(string key) => key != null && _img.TryGetValue(key, out var b) ? b : null;
        public bool Failed(string key) => key != null && _failed.Contains(key);

        // Pide la imagen (si no está ya); «done» se llama en el hilo de la interfaz al tenerla.
        public void Request(VehicleThumbs thumbs, string path, int h, int maxW, Action done, Func<string, bool> stillWanted)
        {
            if (thumbs == null || string.IsNullOrEmpty(path) || _img.ContainsKey(path) || _pending.Contains(path) || _failed.Contains(path) || _busy >= 4) return;
            _pending.Add(path); _busy++;
            Load(thumbs, path, h, maxW, done, stillWanted);
        }

        async void Load(VehicleThumbs thumbs, string path, int h, int maxW, Action done, Func<string, bool> stillWanted)
        {
            Bitmap bmp = null;
            try
            {
                string file = await thumbs.FileAsync(path);
                if (file != null) bmp = await Task.Run(() => VehicleThumbs.Decode(file, h, maxW));
            }
            catch { }
            _pending.Remove(path); _busy--;
            if (_disposed) { bmp?.Dispose(); return; }
            if (bmp == null) _failed.Add(path);
            else
            {
                _img[path] = bmp; _order.AddLast(path);
                var n = _order.First;
                while (_img.Count > _max && n != null)
                {
                    var next = n.Next;
                    if (!stillWanted(n.Value)) { _img[n.Value].Dispose(); _img.Remove(n.Value); _order.Remove(n); }
                    n = next;
                }
            }
            done();
        }

        // Carga previa (pantalla de inicio): sin el tope de peticiones a la vez; las pone en la cola de memoria.
        public async Task Preload(VehicleThumbs thumbs, string path, int h, int maxW)
        {
            if (thumbs == null || string.IsNullOrEmpty(path) || _img.ContainsKey(path) || _pending.Contains(path) || _failed.Contains(path)) return;
            _pending.Add(path);
            Bitmap bmp = null;
            try
            {
                string file = await thumbs.FileAsync(path);
                if (file != null) bmp = await Task.Run(() => VehicleThumbs.Decode(file, h, maxW));
            }
            catch { }
            _pending.Remove(path);
            if (_disposed) { bmp?.Dispose(); return; }
            if (bmp == null) _failed.Add(path);
            else if (!_img.ContainsKey(path)) { _img[path] = bmp; _order.AddLast(path); }
            else bmp.Dispose();
        }

        bool _disposed;
        public void Clear() { foreach (var b in _img.Values) b.Dispose(); _img.Clear(); _order.Clear(); _failed.Clear(); }
        public void Dispose() { _disposed = true; Clear(); }
    }

    static class CardPaint
    {
        public static readonly Color Rail = Color.FromArgb(28, 31, 36);
        public static readonly Color RailLine = Color.FromArgb(78, 84, 92);
        public static readonly Color Sel = Color.FromArgb(50, 54, 57);
        public const TextFormatFlags Line = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;
        public const TextFormatFlags Measure = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;

        public static void FillRound(Graphics g, Rectangle r, int radius, Color c)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Theme.FillRound(g, r, radius, c);
            g.SmoothingMode = SmoothingMode.None;
        }

        // Fondo «vía» con la imagen centrada (o, si aún no está, nada / el aviso).
        public static void Rail_(Graphics g, Rectangle r, Bitmap img, string emptyText, Font f)
        {
            FillRound(g, r, Theme.Px(6), Rail);
            using (var p = new Pen(RailLine, Math.Max(1, Theme.Px(2))))
                g.DrawLine(p, r.X + Theme.Px(6), r.Bottom - Theme.Px(9), r.Right - Theme.Px(6), r.Bottom - Theme.Px(9));
            if (img != null)
            {
                int w = img.Width, h = img.Height;
                if (w > r.Width - Theme.Px(12)) { h = Math.Max(1, h * (r.Width - Theme.Px(12)) / w); w = r.Width - Theme.Px(12); }
                var dst = new Rectangle(r.X + (r.Width - w) / 2, r.Bottom - Theme.Px(8) - h, w, h);
                g.InterpolationMode = w == img.Width ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBilinear;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(img, dst);
                g.PixelOffsetMode = PixelOffsetMode.Default;
            }
            else if (!string.IsNullOrEmpty(emptyText))
                TextRenderer.DrawText(g, emptyText, f, r, Theme.Subtle, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    // ------------------------------------------------------------------ FLOTA
    public sealed class FleetModel
    {
        public string Key = "", Title = "", Sub = "", Path = "";
        public List<FleetUnit> Units = new List<FleetUnit>();
    }

    public sealed class FleetUnit
    {
        public int Row;          // fila en las listas de datos de Flota
        public string Id = "", Label = "";
        public int State;        // 0 disponible · 1 en servicio · 2 en taller · 3 revisión pronto
        internal int W;          // ancho del chip
    }

    public class FleetCardList : Panel
    {
        public static readonly Color[] StateColors = { Theme.Accent, Color.FromArgb(120, 144, 226), Color.FromArgb(229, 115, 115), Color.FromArgb(240, 196, 90) };
        sealed class Card { public FleetModel M; public List<FleetUnit> Shown = new(); public int Y, H; public List<Rectangle> Chips = new(); }

        readonly List<FleetModel> _models = new();
        readonly List<Card> _cards = new();
        int _total, _layoutW = -1, _state = -1;   // -1 todas · 0 disponibles · 1 en servicio · 2 en taller
        string _filter = "";
        FleetUnit _hover;
        public VehicleThumbs Thumbs;
        readonly ThumbMemory _mem = new ThumbMemory(80);
        public string SelectedId { get; private set; }
        public event Action SelectionChanged;
        public string EmptyText;
        Font _fTitle, _fSub, _fChip, _fCnt, _fMsg;

        public FleetCardList()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            AutoScroll = true; TabStop = true; BackColor = Theme.Surface;
            _fTitle = Theme.Font(10.5f, FontStyle.Bold); _fSub = Theme.Font(8.5f); _fChip = Theme.Font(9f); _fCnt = Theme.Font(8.5f, FontStyle.Bold); _fMsg = Theme.Font(10f);
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Native.UseDarkScrollBars(this); }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { _mem.Dispose(); foreach (var f in new[] { _fTitle, _fSub, _fChip, _fCnt, _fMsg }) f.Dispose(); }
            base.Dispose(disposing);
        }

        public int SelectedRow
        {
            get { foreach (var m in _models) foreach (var u in m.Units) if (u.Id == SelectedId) return u.Row; return -1; }
        }

        public void SetModels(List<FleetModel> models)
        {
            _models.Clear(); _models.AddRange(models);
            foreach (var m in _models) foreach (var u in m.Units) u.W = TextRenderer.MeasureText(u.Label, _fChip, Size.Empty, CardPaint.Measure).Width + Theme.Px(30);
            bool exists = false;
            foreach (var m in _models) foreach (var u in m.Units) if (u.Id == SelectedId) exists = true;
            if (!exists) SelectedId = null;
            _hover = null; _layoutW = -1; Relayout();
        }

        public int Count(int state) { int n = 0; foreach (var m in _models) foreach (var u in m.Units) if (state < 0 || u.State == state || (state == 0 && u.State == 3)) n++; return n; }
        public int StateFilter { get => _state; set { if (_state == value) return; _state = value; AutoScrollPosition = Point.Empty; _layoutW = -1; Relayout(); } }
        public void Filter(string text) { var f = (text ?? "").Trim().ToLowerInvariant(); if (f == _filter) return; _filter = f; AutoScrollPosition = Point.Empty; _layoutW = -1; Relayout(); }

        bool UnitPass(FleetUnit u) => _state < 0 || u.State == _state || (_state == 0 && u.State == 3);

        int ThumbW => Theme.Px(200);

        void Relayout()
        {
            int w = Math.Max(200, ClientSize.Width - Theme.Px(4));
            if (w == _layoutW) return;
            _layoutW = w;
            _cards.Clear();
            int y = Theme.Px(2), gap = Theme.Px(8), pad = Theme.Px(10);
            int chipH = Theme.Px(26), chipGap = Theme.Px(6);
            int x0 = pad + ThumbW + Theme.Px(14), maxX = w - pad;
            foreach (var m in _models)
            {
                var c = new Card { M = m };
                bool textHit = _filter.Length > 0 && (m.Title + " " + m.Sub).ToLowerInvariant().Contains(_filter);
                foreach (var u in m.Units)
                    if (UnitPass(u) && (_filter.Length == 0 || textHit || u.Label.ToLowerInvariant().Contains(_filter))) c.Shown.Add(u);
                if (c.Shown.Count == 0) continue;
                int cx = x0, cy = Theme.Px(38);
                foreach (var u in c.Shown)
                {
                    if (cx + u.W > maxX && cx > x0) { cx = x0; cy += chipH + chipGap; }
                    c.Chips.Add(new Rectangle(cx, cy, u.W, chipH));
                    cx += u.W + chipGap;
                }
                c.Y = y;
                c.H = Math.Max(Theme.Px(94), cy + chipH + pad);
                _cards.Add(c);
                y += c.H + gap;
            }
            _total = y;
            AutoScrollMinSize = new Size(0, _cards.Count == 0 ? 0 : _total);
            Invalidate();
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); Relayout(); }
        int ScrollY => -AutoScrollPosition.Y;
        protected override void OnScroll(ScrollEventArgs se) { base.OnScroll(se); Invalidate(); }
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            int max = Math.Max(0, _total - ClientSize.Height);
            int y = Math.Max(0, Math.Min(max, ScrollY - e.Delta * Theme.Px(120) / 120));
            if (y != ScrollY) { AutoScrollPosition = new Point(0, y); Invalidate(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            if (_cards.Count == 0)
            {
                string msg = EmptyText ?? (_models.Count == 0 ? I18n.T("Sin vehículos todavía.") : I18n.T("Nada coincide con el filtro."));
                TextRenderer.DrawText(g, msg, _fMsg, new Rectangle(0, Theme.Px(30), ClientSize.Width, Theme.Px(30)), Theme.Subtle, TextFormatFlags.HorizontalCenter);
                return;
            }
            int top = ScrollY, bottom = top + ClientSize.Height;
            var visible = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in _cards)
            {
                if (c.Y + c.H < top || c.Y > bottom) continue;
                visible.Add(c.M.Path ?? "");
                Paint1(g, c, top);
            }
            foreach (var p in visible)
                _mem.Request(Thumbs, p, Theme.Px(56), ThumbW - Theme.Px(14), () => Invalidate(), k => visible.Contains(k));
        }

        void Paint1(Graphics g, Card c, int top)
        {
            var rc = new Rectangle(Theme.Px(2), c.Y - top, _layoutW - Theme.Px(2), c.H);
            bool sel = c.Shown.Exists(u => u.Id == SelectedId);
            CardPaint.FillRound(g, rc, Theme.Px(10), sel ? CardPaint.Sel : Color.FromArgb(52, 56, 60));
            if (sel) { g.SmoothingMode = SmoothingMode.AntiAlias; Theme.DrawRoundBorder(g, rc, Theme.Px(10), Theme.Accent, 1.5f); g.SmoothingMode = SmoothingMode.None; }
            int pad = Theme.Px(10);
            var tr = new Rectangle(rc.X + pad, rc.Y + pad, ThumbW, Math.Min(rc.Height - pad * 2, Theme.Px(74)));
            CardPaint.Rail_(g, tr, _mem.Get(c.M.Path), _mem.Failed(c.M.Path) || string.IsNullOrEmpty(c.M.Path) ? "🚆" : null, _fSub);
            int x = tr.Right + Theme.Px(14), right = rc.Right - pad;
            // cabecera: nombre · datos · nº de unidades
            string cnt = c.M.Units.Count.ToString("N0", CultureInfo.GetCultureInfo("es-ES"));
            int cw = TextRenderer.MeasureText(cnt, _fCnt, Size.Empty, CardPaint.Measure).Width + Theme.Px(16);
            var cr = new Rectangle(right - cw, rc.Y + pad + Theme.Px(2), cw, Theme.Px(20));
            CardPaint.FillRound(g, cr, Theme.Px(10), Theme.Surface2);
            TextRenderer.DrawText(g, cnt, _fCnt, cr, Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            int tw = Math.Min(TextRenderer.MeasureText(c.M.Title, _fTitle, Size.Empty, CardPaint.Measure).Width, Math.Max(0, cr.X - x - Theme.Px(80)));
            TextRenderer.DrawText(g, c.M.Title, _fTitle, new Rectangle(x, rc.Y + pad, tw, Theme.Px(24)), Theme.Text, CardPaint.Line);
            if (cr.X - x - tw > Theme.Px(30))
                TextRenderer.DrawText(g, c.M.Sub, _fSub, new Rectangle(x + tw + Theme.Px(8), rc.Y + pad, cr.X - x - tw - Theme.Px(16), Theme.Px(24)), Theme.Subtle, CardPaint.Line);
            // chips de las unidades
            for (int i = 0; i < c.Shown.Count; i++)
            {
                var u = c.Shown[i];
                var r = c.Chips[i]; r.Offset(rc.X, rc.Y);
                bool us = u.Id == SelectedId, uh = u == _hover;
                CardPaint.FillRound(g, r, Theme.Px(7), us ? Color.FromArgb(44, 72, 48) : uh ? Theme.Surface2 : CardPaint.Rail);
                if (us) { g.SmoothingMode = SmoothingMode.AntiAlias; Theme.DrawRoundBorder(g, r, Theme.Px(7), Theme.Accent); g.SmoothingMode = SmoothingMode.None; }
                int d = Theme.Px(8);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var b = new SolidBrush(StateColors[Math.Max(0, Math.Min(3, u.State))])) g.FillEllipse(b, r.X + Theme.Px(9), r.Y + (r.Height - d) / 2, d, d);
                g.SmoothingMode = SmoothingMode.None;
                TextRenderer.DrawText(g, u.Label, _fChip, new Rectangle(r.X + Theme.Px(22), r.Y, r.Width - Theme.Px(24), r.Height), Theme.Text, CardPaint.Line);
            }
        }

        FleetUnit UnitAt(Point p, out Card card)
        {
            card = null;
            int y = p.Y + ScrollY;
            foreach (var c in _cards)
            {
                if (y < c.Y || y >= c.Y + c.H) continue;
                card = c;
                for (int i = 0; i < c.Chips.Count; i++)
                {
                    var r = c.Chips[i]; r.Offset(Theme.Px(2), c.Y);
                    if (r.Contains(p.X, y)) return c.Shown[i];
                }
                return null;
            }
            return null;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var u = UnitAt(e.Location, out var c);
            Cursor = c != null ? Cursors.Hand : Cursors.Default;
            if (u != _hover) { _hover = u; Invalidate(); }
        }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (_hover != null) { _hover = null; Invalidate(); } }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            var u = UnitAt(e.Location, out var c);
            if (u == null && c != null && !c.Shown.Exists(x => x.Id == SelectedId)) u = c.Shown[0];   // clic en la tarjeta: su primera unidad
            if (u != null) Select(u.Id, false);
        }

        protected override bool IsInputKey(Keys k) => k == Keys.Up || k == Keys.Down || k == Keys.Left || k == Keys.Right || base.IsInputKey(k);
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            var all = new List<FleetUnit>();
            foreach (var c in _cards) all.AddRange(c.Shown);
            if (all.Count == 0) return;
            int i = all.FindIndex(u => u.Id == SelectedId);
            if (e.KeyCode == Keys.Right || e.KeyCode == Keys.Down) i = Math.Min(all.Count - 1, i + 1);
            else if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Up) i = Math.Max(0, i - 1);
            else return;
            e.Handled = true;
            Select(all[Math.Max(0, i)].Id, true);
        }

        public void Select(string id, bool scrollTo)
        {
            if (SelectedId != id) { SelectedId = id; Invalidate(); SelectionChanged?.Invoke(); }
            if (!scrollTo) return;
            foreach (var c in _cards)
                if (c.Shown.Exists(u => u.Id == id))
                {
                    int y = ScrollY;
                    if (c.Y < y) y = c.Y; else if (c.Y + c.H > y + ClientSize.Height) y = c.Y + c.H - ClientSize.Height;
                    if (y != ScrollY) { AutoScrollPosition = new Point(0, Math.Max(0, y)); Invalidate(); }
                    break;
                }
        }
    }

    // ------------------------------------------------------------------ COMPRA
    public sealed class MachineInfo
    {
        public double Kw, Kmh, Mass, Capacity, Price, Rent;
        public bool Freight;
        public string Traction = "";
    }

    public class BuyCardGrid : Panel
    {
        // --- lo que se usaba del ListBox ---
        public readonly List<object> Items = new List<object>();
        int _sel = -1;
        public event EventHandler SelectedIndexChanged;
        public object SelectedItem { get => _sel >= 0 && _sel < Items.Count ? Items[_sel] : null; set => SelectedIndex = value == null ? -1 : Items.IndexOf(value); }
        public int SelectedIndex
        {
            get => _sel < Items.Count ? _sel : -1;
            set
            {
                int v = value >= -1 && value < Items.Count ? value : -1;
                if (v == _sel) return;
                _sel = v; Invalidate(); EnsureVisible(v);
                SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        public int TopIndex
        {
            get { int row = Math.Max(0, ScrollY / RowH); return Math.Min(Items.Count == 0 ? 0 : Items.Count - 1, row * Cols); }
            set { int row = Math.Max(0, value) / Math.Max(1, Cols); AutoScrollPosition = new Point(0, row * RowH); Invalidate(); }
        }
        int _updating;
        public void BeginUpdate() => _updating++;
        public void EndUpdate() { if (--_updating <= 0) { _updating = 0; if (_sel >= Items.Count) _sel = -1; Relayout(); } }

        // --- datos de cada tarjeta (los da Compra) ---
        public Func<object, string> PathOf, TitleOf, FolderOf;
        public Func<object, int> OwnedOf, CarsOf;
        public Func<object, Task<MachineInfo>> InfoOf;     // se llama en el hilo de la interfaz; el trabajo va dentro, aparte
        public VehicleThumbs Thumbs;
        public string EmptyText;
        readonly Dictionary<string, MachineInfo> _info = new Dictionary<string, MachineInfo>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> _infoPending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly ThumbMemory _mem = new ThumbMemory(150);
        int _infoBusy, _hover = -1, _total;
        static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-ES");
        Font _fTitle, _fSub, _fTag, _fSpec, _fSpecB, _fPrice, _fRent, _fMsg;

        public BuyCardGrid()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            AutoScroll = true; TabStop = true; BackColor = Theme.Surface;
            _fTitle = Theme.Font(9.75f, FontStyle.Bold); _fSub = Theme.Font(8f); _fTag = Theme.Font(7.75f); _fSpec = Theme.Font(8.5f); _fSpecB = Theme.Font(8.5f, FontStyle.Bold);
            _fPrice = Theme.Font(11f, FontStyle.Bold); _fRent = Theme.Font(8.25f); _fMsg = Theme.Font(10f);
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Native.UseDarkScrollBars(this); }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { _mem.Dispose(); foreach (var f in new[] { _fTitle, _fSub, _fTag, _fSpec, _fSpecB, _fPrice, _fRent, _fMsg }) f.Dispose(); }
            base.Dispose(disposing);
        }

        // Los precios dependen de las tarifas: si cambian, se vuelven a calcular.
        public void ClearInfo() { _info.Clear(); Invalidate(); }

        int MinCardW => Theme.Px(220);
        int Gap => Theme.Px(10);
        int Cols => Math.Max(1, (ClientSize.Width - Theme.Px(4) + Gap) / (MinCardW + Gap));
        int CardW => (ClientSize.Width - Theme.Px(4) - Gap * (Cols - 1)) / Cols;
        int CardH => Theme.Px(204);
        int RowH => CardH + Gap;
        int ScrollY => -AutoScrollPosition.Y;

        void Relayout()
        {
            int rows = (Items.Count + Cols - 1) / Cols;
            _total = rows * RowH + Theme.Px(4);
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

        Rectangle CardRect(int i)
        {
            int c = i % Cols, r = i / Cols;
            return new Rectangle(Theme.Px(2) + c * (CardW + Gap), Theme.Px(2) + r * RowH - ScrollY, CardW, CardH);
        }

        void EnsureVisible(int i)
        {
            if (i < 0) return;
            int y = (i / Cols) * RowH, top = ScrollY;
            if (y < top) AutoScrollPosition = new Point(0, y);
            else if (y + RowH > top + ClientSize.Height) AutoScrollPosition = new Point(0, y + RowH - ClientSize.Height);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            if (Items.Count == 0)
            {
                TextRenderer.DrawText(g, EmptyText ?? I18n.T("Nada coincide con el filtro."), _fMsg, new Rectangle(0, Theme.Px(30), ClientSize.Width, Theme.Px(30)), Theme.Subtle, TextFormatFlags.HorizontalCenter);
                return;
            }
            int first = Math.Max(0, (ScrollY / RowH) * Cols), last = Math.Min(Items.Count - 1, ((ScrollY + ClientSize.Height) / RowH + 1) * Cols - 1);
            var visible = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = first; i <= last; i++)
            {
                string p = PathOf?.Invoke(Items[i]) ?? "";
                visible.Add(p);
                PaintCard(g, i, CardRect(i), p);
            }
            // datos y vistas 2D de lo visible (y de la fila siguiente, para que al bajar ya estén)
            int pre = Math.Min(Items.Count - 1, last + Cols);
            for (int i = first; i <= pre; i++)
            {
                var it = Items[i]; string p = PathOf?.Invoke(it) ?? "";
                RequestInfo(it, p);
                _mem.Request(Thumbs, p, Theme.Px(56), CardW - Theme.Px(30), () => Invalidate(), k => visible.Contains(k));
            }
        }

        async void RequestInfo(object it, string path)
        {
            if (InfoOf == null || string.IsNullOrEmpty(path) || _info.ContainsKey(path) || _infoPending.Contains(path) || _infoBusy >= 2) return;
            _infoPending.Add(path); _infoBusy++;
            MachineInfo mi = null;
            try { mi = await InfoOf(it); } catch { }
            _infoBusy--; _infoPending.Remove(path);
            if (IsDisposed) return;
            _info[path] = mi ?? new MachineInfo();
            Invalidate();
        }

        void PaintCard(Graphics g, int i, Rectangle rc, string path)
        {
            var it = Items[i];
            bool sel = i == _sel, hov = i == _hover;
            CardPaint.FillRound(g, rc, Theme.Px(10), sel ? CardPaint.Sel : hov ? Color.FromArgb(56, 60, 64) : Color.FromArgb(52, 56, 60));
            if (sel) { g.SmoothingMode = SmoothingMode.AntiAlias; Theme.DrawRoundBorder(g, rc, Theme.Px(10), Theme.Accent, 1.5f); g.SmoothingMode = SmoothingMode.None; }
            int pad = Theme.Px(10), x = rc.X + pad, w = rc.Width - pad * 2;
            var rail = new Rectangle(x, rc.Y + pad, w, Theme.Px(74));
            CardPaint.Rail_(g, rail, _mem.Get(path), _mem.Failed(path) ? "🚆" : null, _fSub);
            int y = rail.Bottom + Theme.Px(7);
            TextRenderer.DrawText(g, TitleOf?.Invoke(it) ?? "", _fTitle, new Rectangle(x, y, w, Theme.Px(20)), Theme.Text, CardPaint.Line);
            y += Theme.Px(19);
            TextRenderer.DrawText(g, FolderOf?.Invoke(it) ?? "", _fSub, new Rectangle(x, y, w, Theme.Px(16)), Color.FromArgb(128, 136, 142), CardPaint.Line);
            y += Theme.Px(19);
            // etiquetas: tipo de servicio · automotor · en tu flota
            _info.TryGetValue(path, out var mi);
            var tags = new List<(string t, Color c, Color bg)>();
            int cars = CarsOf?.Invoke(it) ?? 1;
            tags.Add((I18n.T(cars > 1 ? "Automotor" : "Locomotora"), Theme.Subtle, Theme.Surface2));
            if (mi != null && (mi.Kw > 0 || mi.Price > 0)) tags.Add(mi.Freight ? (I18n.T("Mercancías"), Color.FromArgb(251, 146, 60), Theme.Surface2) : (I18n.T("Viajeros"), Color.FromArgb(120, 144, 226), Theme.Surface2));
            int owned = OwnedOf?.Invoke(it) ?? 0;
            if (owned > 0) tags.Add((string.Format(I18n.T("en tu flota · {0}"), owned), Theme.AccentHi, Color.FromArgb(44, 72, 48)));
            int tx = x;
            foreach (var (t, c, bg) in tags)
            {
                int tw = TextRenderer.MeasureText(t, _fTag, Size.Empty, CardPaint.Measure).Width + Theme.Px(12);
                if (tx + tw > x + w) break;
                var r = new Rectangle(tx, y, tw, Theme.Px(18));
                CardPaint.FillRound(g, r, Theme.Px(5), bg);
                TextRenderer.DrawText(g, t, _fTag, r, c, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                tx += tw + Theme.Px(5);
            }
            y += Theme.Px(24);
            // datos y precio
            if (mi == null)
                TextRenderer.DrawText(g, I18n.T("Calculando…"), _fSpec, new Rectangle(x, y, w, Theme.Px(18)), Theme.Subtle, CardPaint.Line);
            else
            {
                var segs = new List<(string, bool)>();
                if (mi.Kw > 0) { segs.Add((mi.Kw.ToString("N0", Es), true)); segs.Add((" kW   ", false)); }
                if (mi.Kmh > 0) { segs.Add((mi.Kmh.ToString("N0", Es), true)); segs.Add((" km/h   ", false)); }
                if (mi.Capacity > 0) { segs.Add((mi.Capacity.ToString("N0", Es), true)); segs.Add((" " + I18n.T("plazas"), false)); }
                else if (mi.Mass > 0) { segs.Add((mi.Mass.ToString("N0", Es), true)); segs.Add((" t", false)); }
                int sx = x;
                foreach (var (t, b) in segs)
                {
                    var f = b ? _fSpecB : _fSpec;
                    int sw = TextRenderer.MeasureText(t, f, Size.Empty, CardPaint.Measure).Width;
                    if (sx >= x + w) break;
                    TextRenderer.DrawText(g, t, f, new Rectangle(sx, y, Math.Min(sw, x + w - sx), Theme.Px(18)), b ? Theme.Text : Theme.Subtle, CardPaint.Line);
                    sx += sw;
                }
            }
            y = rc.Bottom - pad - Theme.Px(26);
            using (var p = new Pen(Theme.Surface2)) g.DrawLine(p, x, y, x + w, y);
            y += Theme.Px(3);
            if (mi != null && mi.Price > 0)
            {
                string rent = mi.Rent.ToString("N0", Es) + " €/" + I18n.T("serv.");
                int rw = TextRenderer.MeasureText(rent, _fRent, Size.Empty, CardPaint.Measure).Width;
                TextRenderer.DrawText(g, mi.Price.ToString("N0", Es) + " €", _fPrice, new Rectangle(x, y, w - rw - Theme.Px(6), Theme.Px(24)), Theme.AccentHi, CardPaint.Line);
                TextRenderer.DrawText(g, rent, _fRent, new Rectangle(x + w - rw, y, rw, Theme.Px(24)), Color.FromArgb(96, 165, 250), CardPaint.Line);
            }
        }

        int IndexAt(Point p)
        {
            for (int i = Math.Max(0, (ScrollY / RowH) * Cols); i < Items.Count; i++)
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
            if (i != _hover) { _hover = i; Invalidate(); }
        }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (_hover >= 0) { _hover = -1; Invalidate(); } }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); Focus(); int i = IndexAt(e.Location); if (i >= 0) SelectedIndex = i; }

        protected override bool IsInputKey(Keys k) => k == Keys.Up || k == Keys.Down || k == Keys.Left || k == Keys.Right || k == Keys.Home || k == Keys.End || base.IsInputKey(k);
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (Items.Count == 0) return;
            int i = Math.Max(0, _sel);
            switch (e.KeyCode)
            {
                case Keys.Left: i = Math.Max(0, i - 1); break;
                case Keys.Right: i = Math.Min(Items.Count - 1, i + 1); break;
                case Keys.Up: i = Math.Max(0, i - Cols); break;
                case Keys.Down: i = Math.Min(Items.Count - 1, i + Cols); break;
                case Keys.Home: i = 0; break;
                case Keys.End: i = Items.Count - 1; break;
                default: return;
            }
            e.Handled = true; SelectedIndex = i;
        }
    }

    // ------------------------------------------------------------------ visor 2D / 3D
    // La vista 2D sale al momento (del disco); el 3D de siempre solo al pulsar «Ver en 3D».
    public class VehicleViewport : Panel
    {
        readonly TrainPreviewPanel _3d;
        readonly RoundButton _toggle;
        readonly Control _2d;
        Bitmap _img; string _path, _caption = "", _empty;
        public VehicleThumbs Thumbs;
        public bool Is3D { get; private set; }
        public event Action<bool> ModeChanged;      // true = se ha pasado al 3D (hay que dibujarlo)
        int _token;

        sealed class View2D : Control
        {
            public Func<Bitmap> Img; public Func<string> Caption, Empty;
            readonly Font _f = Theme.Font(10f, FontStyle.Bold), _fe = Theme.Font(10f);
            public View2D() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); }
            protected override void Dispose(bool d) { if (d) { _f.Dispose(); _fe.Dispose(); } base.Dispose(d); }
            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics; g.Clear(BackColor);
                var area = new Rectangle(Theme.Px(10), Theme.Px(10), Width - Theme.Px(20), Height - Theme.Px(44));
                var img = Img?.Invoke();
                string empty = Empty?.Invoke();
                if (img == null && empty != null) { TextRenderer.DrawText(g, empty, _fe, ClientRectangle, Theme.Subtle, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter); return; }
                if (img != null)
                {
                    int w = img.Width, h = img.Height;
                    double k = Math.Min(1.0, Math.Min(area.Width / (double)w, area.Height / (double)h));
                    w = (int)(w * k); h = (int)(h * k);
                    using (var p = new Pen(CardPaint.RailLine, Math.Max(1, Theme.Px(2))))
                        g.DrawLine(p, area.X + Theme.Px(20), area.Y + (area.Height + h) / 2 + Theme.Px(2), area.Right - Theme.Px(20), area.Y + (area.Height + h) / 2 + Theme.Px(2));
                    g.InterpolationMode = k >= 0.999 ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.DrawImage(img, new Rectangle(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h));
                }
                TextRenderer.DrawText(g, Caption?.Invoke() ?? "", _f, new Rectangle(0, Height - Theme.Px(32), Width, Theme.Px(24)), Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
        }

        public VehicleViewport(TrainPreviewPanel preview3d)
        {
            BackColor = Theme.Bg;
            _3d = preview3d; _3d.Dock = DockStyle.Fill; _3d.Visible = false;
            _2d = new View2D { Dock = DockStyle.Fill, BackColor = Theme.Bg, Img = () => _img, Caption = () => _caption, Empty = () => _empty };
            _toggle = new RoundButton { Text = "🧊  " + I18n.T("Ver en 3D"), Height = Theme.Px(28), Radius = 8, FontSize = 8.5f, BaseColor = Theme.Surface2, HoverColor = Theme.SurfaceHi, TextColor = Theme.Text, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            using (var f = Theme.Font(8.5f)) _toggle.Width = TextRenderer.MeasureText("🧊  " + I18n.T("Ver en 2D") + "xx", f).Width + Theme.Px(20);
            _toggle.Click += (s, e) => Set3D(!Is3D);
            Controls.Add(_toggle); Controls.Add(_2d); Controls.Add(_3d);
            _toggle.BringToFront();
            Resize += (s, e) => _toggle.Location = new Point(Width - _toggle.Width - Theme.Px(8), Theme.Px(8));
        }

        protected override void Dispose(bool disposing) { if (disposing) { _img?.Dispose(); _img = null; } base.Dispose(disposing); }

        public void Set3D(bool on)
        {
            if (on == Is3D) return;
            Is3D = on;
            _3d.Visible = on; _2d.Visible = !on;
            _toggle.Text = on ? "🖼  " + I18n.T("Ver en 2D") : "🧊  " + I18n.T("Ver en 3D");
            _toggle.BringToFront(); _toggle.Invalidate();
            if (on) ModeChanged?.Invoke(true);
        }

        // Vehículo que se enseña (null = ninguno, con el aviso «empty»).
        public async void Show(string path, string caption, string empty = null)
        {
            _caption = caption ?? ""; _empty = string.IsNullOrEmpty(path) ? empty : null;
            _toggle.Visible = !string.IsNullOrEmpty(path);
            if (string.Equals(path, _path, StringComparison.OrdinalIgnoreCase) && _img != null) { _2d.Invalidate(); return; }
            _path = path;
            int token = ++_token;
            _img?.Dispose(); _img = null;
            _2d.Invalidate();
            if (string.IsNullOrEmpty(path) || Thumbs == null) return;
            string file = await Thumbs.FileAsync(path);
            int h = Math.Max(40, Math.Min(Theme.Px(150), Height - Theme.Px(60)));
            var bmp = file == null ? null : await Task.Run(() => VehicleThumbs.Decode(file, h));
            if (token != _token || IsDisposed) { bmp?.Dispose(); return; }
            _img = bmp;
            if (bmp == null) _empty = "🚆";
            _2d.Invalidate();
        }
    }
}
