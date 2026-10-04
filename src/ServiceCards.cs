// Lista de SERVICIOS en tarjetas: una por servicio, agrupadas por día (con el total de cada día). Cada
// tarjeta lleva el maquinista, la ruta, la composición 2D del tren (la imagen que subió su SelectOR al
// ponerse de servicio), los datos del viaje y el neto.
// Solo los 5 días más recientes van en tarjetas; los anteriores, en filas finas sin la composición
// («ANTERIORES»), y el calendario de al lado (ServiceCalendar) salta a cualquier día.
// Los servicios anulados por el superadmin llevan el sello «ANULADO».
//
// Rendimiento:
//  · Se dibuja solo lo visible (búsqueda binaria de la primera fila) y en doble búfer.
//  · Los textos de cada tarjeta se preparan una vez al cargar la lista y sus anchos se miden una sola vez.
//  · Las composiciones se descargan y se decodifican fuera del hilo de la interfaz, como mucho 4 a la vez,
//    primero las visibles y luego la pantalla siguiente; quedan en memoria ya reducidas al alto con que se
//    pintan, en formato premultiplicado (el más rápido de dibujar) y con un tope de memoria.
//  · El ratón y la selección solo repintan las tarjetas que cambian.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SelectOR
{
    public class ServiceCardList : Panel
    {
        public sealed class Item
        {
            public string Id = "", Driver = "", Route = "", Path = "", Train = "", Status = "";
            public DateTime Start;               // hora local de salida (MinValue = no se sabe)
            public double Km, DurationS, Pax, Capacity = double.NaN, MassT = double.NaN, Income, Net;
            public int Cars, Engines;
            public bool Valid = true, HasImage, Annulled;
            public string AnnulReason = "";
            public bool Open => Status == "open";
            // Preparado una vez (SetItems): textos y anchos medidos
            internal string Search, Initials, Meta, NetText, IncomeText, StatusText, TimeText, Line2, KmText, DurText;
            internal (string text, bool bold)[] Facts;
            internal int DriverW = -1;
            internal int[] FactW;
        }

        sealed class Row
        {
            public bool Header, Compact, Sep;   // Sep: la línea «ANTERIORES»
            public DateTime Day;
            public string Title, Sub, Totals, TotalsNet;
            public bool TotalsNeg;
            public Item Item;
            public int Y, H;
            public int TitleW = -1, TotalsW = -1, TotalsNetW = -1;
        }

        static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-ES");
        static readonly Color Blue = Color.FromArgb(120, 144, 226);
        static readonly Color Red = Color.FromArgb(229, 115, 115);
        static readonly Color Rail = Color.FromArgb(28, 31, 36);
        static readonly Color HoverFill = Color.FromArgb(50, 54, 57);
        const TextFormatFlags Measure = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
        const TextFormatFlags Line = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;
        const TextFormatFlags LineR = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.Right;

        readonly List<Item> _items = new List<Item>();
        readonly List<Row> _rows = new List<Row>();
        readonly Dictionary<string, Row> _rowById = new Dictionary<string, Row>();
        int _total;
        string _filter = "";
        int _status;                                   // 0 todos · 1 en conducción · 2 completados
        string _message;                               // «Cargando…», «Error…»
        Item _hover;
        public string SelectedId { get; private set; }
        public int RecentDays = 5;                     // días en tarjetas; los demás, en filas finas
        // Servicios y neto de cada día (con el filtro y la pestaña de estado aplicados): para el calendario.
        public readonly Dictionary<DateTime, (int n, double net)> DayTotals = new Dictionary<DateTime, (int, double)>();
        public event Action DaysChanged;
        public event Action<DateTime> TopDayChanged;   // el día que queda arriba al desplazarse
        DateTime _topDay = DateTime.MinValue;

        // Archivo local de la composición de un servicio (lo descarga si hace falta); null = no hay.
        public Func<string, Task<string>> FileLoader;
        readonly Dictionary<string, Bitmap> _img = new Dictionary<string, Bitmap>();
        readonly LinkedList<string> _imgOrder = new LinkedList<string>();
        readonly HashSet<string> _imgPending = new HashSet<string>(), _imgFailed = new HashSet<string>();
        readonly Dictionary<string, (int w, Bitmap bmp)> _fit = new Dictionary<string, (int, Bitmap)>();   // reducida al ancho de la tarjeta
        const int ImgCacheMax = 120, MaxLoads = 4;
        int _loads;

        public event Action<string> ItemActivated;     // doble clic o Intro: abre el detalle
        public event Action SelectionChanged;

        Font _fName, _fMeta, _fFact, _fFactB, _fNet, _fSmall, _fHead, _fHeadSub, _fAvatar, _fMsg, _fStamp, _fRowNet;
        public string LblAnnulled = "ANULADO";
        readonly SolidBrush _bSurface = new SolidBrush(Theme.Surface), _bHover = new SolidBrush(HoverFill), _bRail = new SolidBrush(Rail),
                            _bAvatar = new SolidBrush(Theme.Surface2), _bBlue = new SolidBrush(Blue), _bRed = new SolidBrush(Red), _bGreen = new SolidBrush(Theme.Accent);
        readonly Pen _pSel = new Pen(Theme.Accent, 1.5f), _pHover = new Pen(Theme.Border), _pSep = new Pen(Theme.Surface2);
        GraphicsPath _cardPath, _railPath; Size _cardPathSize, _railPathSize;
        readonly Timer _clock = new Timer { Interval = 30000 };   // «hace X min» de los servicios en conducción

        public ServiceCardList()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            AutoScroll = true;
            TabStop = true;
            BackColor = Theme.Bg;
            _fName = Theme.Font(10f, FontStyle.Bold); _fMeta = Theme.Font(9f); _fFact = Theme.Font(9f); _fFactB = Theme.Font(9f, FontStyle.Bold);
            _fNet = Theme.Font(14f, FontStyle.Bold); _fSmall = Theme.Font(8.25f); _fHead = Theme.Font(9.75f, FontStyle.Bold);
            _fHeadSub = Theme.Font(9f); _fAvatar = Theme.Font(7.5f, FontStyle.Bold); _fMsg = Theme.Font(10f);
            _fStamp = Theme.Font(10f, FontStyle.Bold); _fRowNet = Theme.Font(9.5f, FontStyle.Bold);
            _clock.Tick += (s, e) => { foreach (var it in _items) if (it.Open) { InvalidateItem(it); } };
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Native.UseDarkScrollBars(this); }
        protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); _clock.Enabled = Visible && _items.Exists(i => i.Open); }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _clock.Dispose();
                foreach (var im in _img.Values) im.Dispose();
                foreach (var f in _fit.Values) f.bmp.Dispose();
                _img.Clear(); _fit.Clear();
                foreach (var f in new[] { _fName, _fMeta, _fFact, _fFactB, _fNet, _fSmall, _fHead, _fHeadSub, _fAvatar, _fMsg, _fStamp, _fRowNet }) f?.Dispose();
                foreach (var b in new[] { _bSurface, _bHover, _bRail, _bAvatar, _bBlue, _bRed, _bGreen }) b.Dispose();
                _pSel.Dispose(); _pHover.Dispose(); _pSep.Dispose();
                _cardPath?.Dispose(); _railPath?.Dispose();
            }
            base.Dispose(disposing);
        }

        // ------------------------------------------------------------------ datos
        public void SetItems(IEnumerable<Item> items)
        {
            _items.Clear();
            foreach (var it in items) { Prepare(it); _items.Add(it); }
            _message = null;
            if (SelectedId != null && !_items.Exists(i => i.Id == SelectedId)) SelectedId = null;
            _hover = null;
            _clock.Enabled = Visible && _items.Exists(i => i.Open);
            Rebuild();
        }

        public void ShowMessage(string text) { _items.Clear(); _message = text; Rebuild(); }

        public void Filter(string text)
        {
            string f = (text ?? "").Trim().ToLowerInvariant();
            if (f == _filter) return;
            _filter = f; AutoScrollPosition = Point.Empty; Rebuild();
        }

        public int StatusFilter
        {
            get => _status;
            set { if (_status == value) return; _status = value; AutoScrollPosition = Point.Empty; Rebuild(); }
        }

        // Todos los textos de la tarjeta, una sola vez (al pintar ya no se forma ninguna cadena).
        static void Prepare(Item it)
        {
            it.Search = string.Join(" ", it.Driver, it.Route, it.Path, it.Train,
                                    it.Start == DateTime.MinValue ? "" : it.Start.ToString("dd-MM-yyyy", Es),
                                    it.Annulled ? I18n.T("anulado") + " " + it.AnnulReason : "").ToLowerInvariant();
            it.Initials = Initials(it.Driver);
            var meta = new List<string>(3);
            if (it.Route.Length > 0 && it.Route != "—") meta.Add(it.Route);
            if (it.Path.Length > 0) meta.Add(it.Path);
            if (it.Start != DateTime.MinValue) meta.Add((it.Open ? I18n.T("salió a las") + " " : "") + it.Start.ToString("HH:mm", Es));
            it.Meta = string.Join("  ·  ", meta);
            it.NetText = it.Net.ToString("+#,##0.00;−#,##0.00", Es) + " €";
            it.IncomeText = I18n.T("ingreso") + " " + it.Income.ToString("N0", Es) + " €";
            it.StatusText = it.Annulled ? I18n.T("Anulado") : it.Valid ? "✓ " + I18n.T("completado") : I18n.T("no validado");
            // Fila fina (servicios anteriores)
            it.TimeText = it.Start == DateTime.MinValue ? "" : it.Start.ToString("HH:mm", Es);
            var l2 = new List<string>(3);
            if (it.Train.Length > 0) l2.Add(it.Train);
            if (it.Route.Length > 0 && it.Route != "—") l2.Add(it.Route);
            if (it.Path.Length > 0) l2.Add(it.Path);
            it.Line2 = string.Join("  ·  ", l2);
            it.KmText = it.Open ? "" : it.Km.ToString("N0", Es) + " km";
            int ts = (int)Math.Max(0, it.DurationS);
            it.DurText = it.Open ? "" : $"{ts / 3600}h {(ts % 3600) / 60:00}m";
            var segs = new List<(string, bool)>(10) { (it.Train.Length > 0 ? it.Train : "—", true) };
            if (it.Cars > 0) segs.Add(("  ·  " + it.Cars.ToString("N0", Es) + " " + I18n.T(it.Cars == 1 ? "coche" : "coches") + (it.Engines > 0 ? " (" + it.Engines + "M)" : ""), false));
            if (!it.Open)
            {
                segs.Add(("      " + it.Km.ToString("N0", Es), true)); segs.Add((" km", false));
                int t = (int)Math.Max(0, it.DurationS);
                segs.Add(($"      {t / 3600}h {(t % 3600) / 60:00}m", true));
                double v = it.DurationS > 0 ? it.Km / (it.DurationS / 3600.0) : 0;
                if (v > 0) { segs.Add(("      " + v.ToString("N0", Es), true)); segs.Add((" km/h", false)); }
            }
            if (it.Capacity > 0)
            {
                if (!it.Open && it.Pax > 0) { segs.Add(("      🧍 " + it.Pax.ToString("N0", Es), true)); segs.Add((" " + I18n.T("viajeros") + " · " + it.Capacity.ToString("N0", Es) + " " + I18n.T("plazas"), false)); }
                else segs.Add(("      " + it.Capacity.ToString("N0", Es) + " " + I18n.T("plazas"), false));
            }
            else if (it.MassT > 0) segs.Add(("      ⚖ " + it.MassT.ToString("N0", Es) + " t", true));
            else if (!it.Open && it.Pax > 0) { segs.Add(("      🧍 " + it.Pax.ToString("N0", Es), true)); segs.Add((" " + I18n.T("viajeros"), false)); }
            it.Facts = segs.ToArray();
            it.FactW = null; it.DriverW = -1;
        }

        bool Pass(Item i)
        {
            if (_status == 1 && !i.Open) return false;
            if (_status == 2 && i.Open) return false;
            if (_filter.Length == 0) return true;
            foreach (var w in _filter.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                if (i.Search.IndexOf(w, StringComparison.Ordinal) < 0) return false;
            return true;
        }

        // Filas: cabecera de día + sus tarjetas (en el orden en que llegan: el más reciente primero).
        void Rebuild()
        {
            _rows.Clear(); _rowById.Clear(); DayTotals.Clear();
            int y = Theme.Px(2), cardH = Theme.Px(116), headH = Theme.Px(34), gap = Theme.Px(8);
            int rowH = Theme.Px(30), rowGap = Theme.Px(3), headSmallH = Theme.Px(30), sepH = Theme.Px(44);
            int i = 0, days = 0;
            // Cuántos servicios quedan en filas finas (para la línea «ANTERIORES»)
            int olderCount = 0;
            {
                int dd = 0; DateTime last = DateTime.MaxValue; bool any = false;
                foreach (var it in _items)
                {
                    if (!Pass(it)) continue;
                    var d = DayOf(it);
                    if (!any || d != last) { dd++; last = d; any = true; }
                    if (dd > RecentDays) olderCount++;
                }
            }
            while (i < _items.Count)
            {
                var day = DayOf(_items[i]);
                int j = i, n = 0, done = 0; double net = 0, km = 0;
                for (; j < _items.Count && DayOf(_items[j]) == day; j++)
                {
                    var it = _items[j];
                    if (!Pass(it)) continue;
                    n++;
                    if (!it.Open && !it.Annulled) { done++; net += it.Net; km += it.Km; }
                }
                if (n > 0)
                {
                    days++;
                    bool compact = days > RecentDays;
                    if (day != DateTime.MinValue) DayTotals[day] = (n, net);
                    if (compact && days == RecentDays + 1)
                    {
                        _rows.Add(new Row { Sep = true, Title = I18n.T("ANTERIORES"),
                                            Totals = string.Format(I18n.T(olderCount == 1 ? "{0} servicio" : "{0} servicios"), olderCount.ToString("N0", Es)),
                                            Y = y, H = sepH });
                        y += sepH;
                    }
                    _rows.Add(new Row
                    {
                        Header = true, Compact = compact, Day = day, Title = DayTitle(day, out string sub), Sub = sub,
                        Totals = string.Format(I18n.T(n == 1 ? "{0} servicio" : "{0} servicios"), n) + (done > 0 ? "  ·  " + km.ToString("N0", Es) + " km  ·  " : ""),
                        TotalsNet = done > 0 ? net.ToString("+#,##0.00;−#,##0.00", Es) + " €" : "", TotalsNeg = net < 0,
                        Y = y, H = compact ? headSmallH : headH
                    });
                    y += compact ? headSmallH : headH;
                    for (int k = i; k < j; k++)
                    {
                        if (!Pass(_items[k])) continue;
                        var r = new Row { Item = _items[k], Compact = compact, Day = day, Y = y, H = compact ? rowH : cardH };
                        _rows.Add(r); _rowById[_items[k].Id] = r;
                        y += compact ? rowH + rowGap : cardH + gap;
                    }
                }
                i = j;
            }
            _total = y + Theme.Px(6);
            AutoScrollMinSize = new Size(0, _rows.Count == 0 ? 0 : _total);
            _topDay = DateTime.MinValue;
            Invalidate();
            DaysChanged?.Invoke();
        }

        // Lleva la lista a un día (su cabecera arriba). false si ese día no tiene servicios a la vista.
        public bool ScrollToDay(DateTime day)
        {
            day = day.Date;
            foreach (var r in _rows)
            {
                if (!r.Header || r.Day != day) continue;
                int max = Math.Max(0, _total - ClientSize.Height);
                AutoScrollPosition = new Point(0, Math.Min(max, Math.Max(0, r.Y - Theme.Px(2))));
                Invalidate();
                return true;
            }
            return false;
        }

        static DateTime DayOf(Item i) => i.Start == DateTime.MinValue ? DateTime.MinValue : i.Start.Date;

        static string DayTitle(DateTime day, out string sub)
        {
            sub = "";
            if (day == DateTime.MinValue) return I18n.T("Sin fecha");
            var ci = I18n.English ? CultureInfo.GetCultureInfo("en-GB") : Es;
            string full = day.ToString(I18n.English ? "dddd d MMMM" : "dddd d 'de' MMMM", ci);
            var today = DateTime.Today;
            if (day == today) { sub = full; return I18n.T("Hoy"); }
            if (day == today.AddDays(-1)) { sub = full; return I18n.T("Ayer"); }
            if (day.Year != today.Year) full += " " + day.Year;
            return ci.TextInfo.ToUpper(full[0]) + full.Substring(1);
        }

        // ------------------------------------------------------------------ dibujo
        int ScrollY => -AutoScrollPosition.Y;

        protected override void OnScroll(ScrollEventArgs se) { base.OnScroll(se); Invalidate(); }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            int max = Math.Max(0, _total - ClientSize.Height);
            int y = Math.Max(0, Math.Min(max, ScrollY - e.Delta * Theme.Px(120) / 120));
            if (y == ScrollY) return;
            AutoScrollPosition = new Point(0, y);
            Invalidate();
            UpdateHover(e.Location);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            foreach (var f in _fit.Values) f.bmp.Dispose();   // cambia el ancho de la tarjeta
            _fit.Clear();
        }

        // Primera fila que llega a «y» (las filas van ordenadas por Y).
        int FirstRowAt(int y)
        {
            int lo = 0, hi = _rows.Count - 1, ans = _rows.Count;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (_rows[mid].Y + _rows[mid].H >= y) { ans = mid; hi = mid - 1; } else lo = mid + 1;
            }
            return ans;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            if (_rows.Count == 0)
            {
                string msg = _message ?? (_items.Count == 0 ? I18n.T("Sin servicios todavía.") : I18n.T("Nada coincide con el filtro."));
                TextRenderer.DrawText(g, msg, _fMsg, new Rectangle(0, Theme.Px(30), ClientSize.Width, Theme.Px(30)), Theme.Subtle,
                                      TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }
            int top = ScrollY, clipTop = top + e.ClipRectangle.Top, clipBottom = top + e.ClipRectangle.Bottom;
            int w = ClientSize.Width - Theme.Px(4);
            for (int k = FirstRowAt(clipTop); k < _rows.Count && _rows[k].Y <= clipBottom; k++)
            {
                var r = _rows[k];
                var rc = new Rectangle(Theme.Px(2), r.Y - top, w - Theme.Px(2), r.H);
                if (r.Sep) PaintSep(g, r, rc);
                else if (r.Header) PaintHeader(g, r, rc);
                else if (r.Compact) PaintRow(g, r.Item, rc);
                else PaintCard(g, r.Item, rc);
            }
            QueueImages();
            // Día que queda arriba (para el calendario)
            int fk = FirstRowAt(top + Theme.Px(4));
            var td = fk < _rows.Count ? _rows[fk].Day : DateTime.MinValue;
            if (fk < _rows.Count && _rows[fk].Sep && fk + 1 < _rows.Count) td = _rows[fk + 1].Day;
            if (td != _topDay) { _topDay = td; if (td != DateTime.MinValue) TopDayChanged?.Invoke(td); }
        }

        void PaintHeader(Graphics g, Row r, Rectangle rc)
        {
            var flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.Bottom;
            int baseY = rc.Bottom - Theme.Px(8), h = baseY - rc.Y;
            if (r.TitleW < 0)
            {
                r.TitleW = TextRenderer.MeasureText(g, r.Title, _fHead, Size.Empty, Measure).Width;
                r.TotalsW = TextRenderer.MeasureText(g, r.Totals, _fHeadSub, Size.Empty, Measure).Width;
                r.TotalsNetW = r.TotalsNet.Length > 0 ? TextRenderer.MeasureText(g, r.TotalsNet, _fHead, Size.Empty, Measure).Width : 0;
            }
            int x0 = rc.X + Theme.Px(2);
            TextRenderer.DrawText(g, r.Title, _fHead, new Rectangle(x0, rc.Y, r.TitleW + 4, h), Theme.Text, flags);
            if (r.Sub.Length > 0)
                TextRenderer.DrawText(g, r.Sub, _fHeadSub, new Rectangle(x0 + r.TitleW + Theme.Px(10), rc.Y, rc.Width / 2, h), Theme.Subtle, flags);
            int right = rc.Right - Theme.Px(4);
            if (r.TotalsNetW > 0)
            {
                TextRenderer.DrawText(g, r.TotalsNet, _fHead, new Rectangle(right - r.TotalsNetW, rc.Y, r.TotalsNetW + 2, h), r.TotalsNeg ? Red : Theme.AccentHi, flags);
                right -= r.TotalsNetW;
            }
            TextRenderer.DrawText(g, r.Totals, _fHeadSub, new Rectangle(right - r.TotalsW, rc.Y, r.TotalsW + 2, h), Theme.Subtle, flags);
        }

        static GraphicsPath RoundPath(Size s, int radius) => Theme.Round(new Rectangle(Point.Empty, s), radius);

        void FillAt(Graphics g, ref GraphicsPath path, ref Size size, Rectangle rc, int radius, Brush b, Pen border = null)
        {
            if (path == null || size != rc.Size) { path?.Dispose(); path = RoundPath(rc.Size, radius); size = rc.Size; }
            g.TranslateTransform(rc.X, rc.Y);
            g.FillPath(b, path);
            if (border != null) g.DrawPath(border, path);
            g.ResetTransform();   // TextRenderer no usa la transformación: se dibuja siempre sin ella
        }

        void PaintCard(Graphics g, Item it, Rectangle rc)
        {
            bool sel = it.Id == SelectedId, hov = it == _hover;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            FillAt(g, ref _cardPath, ref _cardPathSize, rc, Theme.Px(10), sel || hov ? _bHover : _bSurface, sel ? _pSel : hov ? _pHover : null);
            g.SmoothingMode = SmoothingMode.None;
            // Franja de estado: azul en conducción, rojo con pérdidas, verde con ganancias
            g.FillRectangle(it.Open ? _bBlue : (it.Annulled || it.Net < 0 ? _bRed : _bGreen), rc.X, rc.Y + Theme.Px(12), Theme.Px(3), rc.Height - Theme.Px(24));

            int pad = Theme.Px(14), moneyW = Theme.Px(170);
            int x0 = rc.X + pad + Theme.Px(2), right = rc.Right - moneyW;
            int y = rc.Y + Theme.Px(10);

            // 1) maquinista y viaje
            int av = Theme.Px(24), lineH = Theme.Px(24);
            var avr = new Rectangle(x0, y, av, av);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.FillEllipse(_bAvatar, avr);
            g.SmoothingMode = SmoothingMode.None;
            TextRenderer.DrawText(g, it.Initials, _fAvatar, avr, Theme.AccentHi, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            int x = avr.Right + Theme.Px(9);
            if (it.DriverW < 0) it.DriverW = TextRenderer.MeasureText(g, it.Driver, _fName, Size.Empty, Measure).Width;
            int nw = Math.Min(it.DriverW, Math.Max(0, right - x - Theme.Px(20)));
            TextRenderer.DrawText(g, it.Driver, _fName, new Rectangle(x, y, nw, lineH), Theme.Text, Line);
            x += nw + Theme.Px(10);
            if (x < right - Theme.Px(30))
                TextRenderer.DrawText(g, it.Meta, _fMeta, new Rectangle(x, y, right - x - Theme.Px(12), lineH), Theme.Subtle, Line);
            y += lineH + Theme.Px(6);

            // 2) composición 2D
            var sr = new Rectangle(x0 - Theme.Px(2), y, right - x0 - Theme.Px(10), Theme.Px(44));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            FillAt(g, ref _railPath, ref _railPathSize, sr, Theme.Px(6), _bRail);
            g.SmoothingMode = SmoothingMode.None;
            var img = StripFor(it.Id, sr.Width - Theme.Px(16));
            if (img != null)
            {
                g.InterpolationMode = InterpolationMode.NearestNeighbor;   // ya está a su tamaño exacto
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(img, new Rectangle(sr.X + Theme.Px(8), sr.Y + (sr.Height - img.Height) / 2, img.Width, img.Height));
                g.PixelOffsetMode = PixelOffsetMode.Default;
            }
            else if (!it.HasImage || _imgFailed.Contains(it.Id))
                TextRenderer.DrawText(g, "🚆  " + I18n.T("Sin composición guardada"), _fSmall,
                                      new Rectangle(sr.X + Theme.Px(10), sr.Y, sr.Width - Theme.Px(20), sr.Height), Theme.Subtle, Line);
            y = sr.Bottom + Theme.Px(6);

            // 3) datos del viaje en una línea
            if (it.FactW == null)
            {
                it.FactW = new int[it.Facts.Length];
                for (int k = 0; k < it.Facts.Length; k++)
                    it.FactW[k] = TextRenderer.MeasureText(g, it.Facts[k].text, it.Facts[k].bold ? _fFactB : _fFact, Size.Empty, Measure).Width;
            }
            int fx = x0 - Theme.Px(2), fh = Theme.Px(20);
            for (int k = 0; k < it.Facts.Length && fx < right - Theme.Px(14); k++)
            {
                var (text, bold) = it.Facts[k];
                int avail = right - Theme.Px(12) - fx;
                TextRenderer.DrawText(g, text, bold ? _fFactB : _fFact, new Rectangle(fx, y, Math.Min(it.FactW[k], avail), fh), bold ? Theme.Text : Theme.Subtle, Line);
                fx += it.FactW[k];
            }

            // 4) economía, a la derecha
            g.DrawLine(_pSep, right, rc.Y + Theme.Px(12), right, rc.Bottom - Theme.Px(12));
            var mr = new Rectangle(right + Theme.Px(12), rc.Y, rc.Right - right - Theme.Px(26), rc.Height);
            int my = mr.Y + (mr.Height - Theme.Px(70)) / 2;
            if (it.Open)
            {
                TextRenderer.DrawText(g, I18n.T("En conducción"), _fFactB, new Rectangle(mr.X, my, mr.Width, Theme.Px(30)), Blue, LineR);
                if (it.Start != DateTime.MinValue)
                    TextRenderer.DrawText(g, Ago(DateTime.Now - it.Start), _fSmall, new Rectangle(mr.X, my + Theme.Px(30), mr.Width, Theme.Px(18)), Theme.Subtle, LineR);
                TextRenderer.DrawText(g, "●  " + I18n.T("en directo"), _fSmall, new Rectangle(mr.X, my + Theme.Px(50), mr.Width, Theme.Px(18)), Blue, LineR);
            }
            else if (it.Annulled)
            {
                TextRenderer.DrawText(g, it.NetText, _fNet, new Rectangle(mr.X, my, mr.Width, Theme.Px(30)), Theme.Subtle, LineR);
                TextRenderer.DrawText(g, it.IncomeText, _fSmall, new Rectangle(mr.X, my + Theme.Px(30), mr.Width, Theme.Px(18)), Theme.Subtle, LineR);
                Stamp(g, LblAnnulled, mr.X + mr.Width / 2 - Theme.Px(6), my + Theme.Px(42));
            }
            else
            {
                TextRenderer.DrawText(g, it.NetText, _fNet, new Rectangle(mr.X, my, mr.Width, Theme.Px(30)), it.Net < 0 ? Red : Theme.AccentHi, LineR);
                TextRenderer.DrawText(g, it.IncomeText, _fSmall, new Rectangle(mr.X, my + Theme.Px(30), mr.Width, Theme.Px(18)), Theme.Subtle, LineR);
                TextRenderer.DrawText(g, it.StatusText, _fSmall, new Rectangle(mr.X, my + Theme.Px(50), mr.Width, Theme.Px(18)), it.Valid ? Theme.AccentHi : Red, LineR);
            }
        }

        // Sello girado, como el «SUSPENDIDO» de Socios.
        void Stamp(Graphics g, string text, int cx, int cy)
        {
            var sz = TextRenderer.MeasureText(g, text, _fStamp, Size.Empty, Measure);
            var box = new Rectangle(-sz.Width / 2 - Theme.Px(9), -sz.Height / 2 - Theme.Px(4), sz.Width + Theme.Px(18), sz.Height + Theme.Px(8));
            var st = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TranslateTransform(cx, cy); g.RotateTransform(-10);
            using (var pen = new Pen(Color.FromArgb(220, Red), 2f))
            using (var path = Theme.Round(box, Theme.Px(5))) g.DrawPath(pen, path);
            using (var br = new SolidBrush(Color.FromArgb(230, Red)))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString(text, _fStamp, br, new RectangleF(box.X, box.Y, box.Width, box.Height), sf);
            g.Restore(st);
        }

        // Línea «ANTERIORES · N servicios»
        void PaintSep(Graphics g, Row r, Rectangle rc)
        {
            int y = rc.Bottom - Theme.Px(14);
            if (r.TitleW < 0)
            {
                r.TitleW = TextRenderer.MeasureText(g, r.Title, _fSmall, Size.Empty, Measure).Width;
                r.TotalsW = TextRenderer.MeasureText(g, r.Totals, _fSmall, Size.Empty, Measure).Width;
            }
            int x = rc.X + Theme.Px(2);
            TextRenderer.DrawText(g, r.Title, _fSmall, new Rectangle(x, y - Theme.Px(10), r.TitleW + 2, Theme.Px(20)), Theme.AccentHi, Line);
            x += r.TitleW + Theme.Px(8);
            TextRenderer.DrawText(g, "·  " + r.Totals, _fSmall, new Rectangle(x, y - Theme.Px(10), r.TotalsW + Theme.Px(30), Theme.Px(20)), Theme.Subtle, Line);
            x += r.TotalsW + Theme.Px(36);
            using var pen = new Pen(Theme.Surface2) { DashStyle = DashStyle.Dash };
            if (x < rc.Right) g.DrawLine(pen, x, y, rc.Right - Theme.Px(4), y);
        }

        // Fila fina de un servicio anterior: hora · maquinista · tren, ruta y recorrido · km · duración · neto
        void PaintRow(Graphics g, Item it, Rectangle rc)
        {
            bool sel = it.Id == SelectedId, hov = it == _hover;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Theme.Round(rc, Theme.Px(6)))
            {
                g.FillPath(sel || hov ? _bHover : _bSurface, path);
                if (sel) g.DrawPath(_pSel, path);
            }
            g.SmoothingMode = SmoothingMode.None;
            g.FillRectangle(it.Open ? _bBlue : (it.Annulled || it.Net < 0 ? _bRed : _bGreen), rc.X, rc.Y + Theme.Px(7), Theme.Px(3), rc.Height - Theme.Px(14));
            int x = rc.X + Theme.Px(12), h = rc.Height, y = rc.Y;
            int netW = Theme.Px(128), durW = Theme.Px(62), kmW = Theme.Px(70);
            int right = rc.Right - Theme.Px(10);
            // derecha: neto (o estado)
            var nr = new Rectangle(right - netW, y, netW, h);
            if (it.Open) TextRenderer.DrawText(g, I18n.T("En conducción"), _fFactB, nr, Blue, LineR);
            else if (it.Annulled) TextRenderer.DrawText(g, LblAnnulled, _fRowNet, nr, Red, LineR);
            else TextRenderer.DrawText(g, it.NetText, _fRowNet, nr, it.Net < 0 ? Red : Theme.AccentHi, LineR);
            right -= netW + Theme.Px(8);
            if (right - x > Theme.Px(420))
            {
                TextRenderer.DrawText(g, it.DurText, _fFact, new Rectangle(right - durW, y, durW, h), Theme.Subtle, LineR);
                right -= durW + Theme.Px(6);
                TextRenderer.DrawText(g, it.KmText, _fFactB, new Rectangle(right - kmW, y, kmW, h), it.Annulled ? Theme.Subtle : Theme.Text, LineR);
                right -= kmW + Theme.Px(12);
            }
            TextRenderer.DrawText(g, it.TimeText, _fFact, new Rectangle(x, y, Theme.Px(44), h), Theme.Subtle, Line);
            x += Theme.Px(48);
            int dw = Math.Min(Theme.Px(150), Math.Max(0, right - x));
            TextRenderer.DrawText(g, it.Driver, _fFactB, new Rectangle(x, y, dw, h), it.Annulled ? Theme.Subtle : Theme.Text, Line);
            x += dw + Theme.Px(10);
            if (x < right - Theme.Px(20))
                TextRenderer.DrawText(g, it.Line2, _fFact, new Rectangle(x, y, right - x, h), Theme.Subtle, Line);
        }

        static string Initials(string name)
        {
            var p = (name ?? "").Split(new[] { ' ', '_', '.', '-' }, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length == 0) return "?";
            string s = p.Length == 1 ? p[0].Substring(0, Math.Min(2, p[0].Length)) : "" + p[0][0] + p[1][0];
            return s.ToUpperInvariant();
        }

        static string Ago(TimeSpan t)
        {
            if (t.TotalMinutes < 1) return I18n.T("ahora mismo");
            if (t.TotalHours < 1) return string.Format(I18n.T("hace {0} min"), (int)t.TotalMinutes);
            return string.Format(I18n.T("hace {0} h {1} min"), (int)t.TotalHours, t.Minutes);
        }

        // ------------------------------------------------------------------ imágenes
        // La imagen tal como se pinta: a su alto; si no cabe a lo ancho, una copia reducida (se hace una vez).
        Bitmap StripFor(string id, int maxW)
        {
            if (!_img.TryGetValue(id, out var img)) return null;
            if (img.Width <= maxW) return img;
            if (_fit.TryGetValue(id, out var f) && f.w == maxW) return f.bmp;
            if (f.bmp != null) f.bmp.Dispose();
            int h = Math.Max(1, (int)Math.Round(img.Height * (maxW / (double)img.Width)));
            var bmp = new Bitmap(maxW, h, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(img, new Rectangle(0, 0, maxW, h));
            }
            _fit[id] = (maxW, bmp);
            return bmp;
        }

        // Pide las composiciones que faltan: primero las visibles, después la pantalla siguiente y la anterior.
        void QueueImages()
        {
            if (FileLoader == null || _loads >= MaxLoads || _rows.Count == 0) return;
            int top = ScrollY, h = ClientSize.Height;
            foreach (var (from, to) in new[] { (top, top + h), (top + h, top + 2 * h), (top - h, top) })
            {
                for (int k = FirstRowAt(Math.Max(0, from)); k < _rows.Count && _rows[k].Y <= to; k++)
                {
                    var it = _rows[k].Item;
                    if (it == null || _rows[k].Compact || !it.HasImage || _img.ContainsKey(it.Id) || _imgPending.Contains(it.Id) || _imgFailed.Contains(it.Id)) continue;
                    LoadImage(it.Id);
                    if (_loads >= MaxLoads) return;
                }
            }
        }

        async void LoadImage(string id)
        {
            _imgPending.Add(id); _loads++;
            Bitmap small = null;
            try
            {
                string file = await FileLoader(id);
                int h = Theme.Px(40);
                // Leer, decodificar y reducir fuera del hilo de la interfaz
                if (file != null) small = await Task.Run(() => Decode(file, h));
            }
            catch { small = null; }
            _loads--; _imgPending.Remove(id);
            if (IsDisposed) { small?.Dispose(); return; }
            if (small == null) _imgFailed.Add(id);
            else
            {
                _img[id] = small; _imgOrder.AddLast(id);
                Trim();
            }
            if (_rowById.ContainsKey(id)) InvalidateItem(_rowById[id].Item);
            QueueImages();
        }

        static Bitmap Decode(string file, int h)
        {
            try
            {
                using var src = ServiceImages.Load(file);
                if (src == null) return null;
                int w = Math.Max(1, (int)Math.Round(src.Width * (h / (double)src.Height)));
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

        // Tope de memoria: fuera las más antiguas que no se están viendo.
        void Trim()
        {
            if (_img.Count <= ImgCacheMax) return;
            int top = ScrollY, bottom = top + ClientSize.Height;
            var node = _imgOrder.First;
            while (_img.Count > ImgCacheMax && node != null)
            {
                var next = node.Next;
                bool seen = _rowById.TryGetValue(node.Value, out var r) && r.Y + r.H >= top && r.Y <= bottom;
                if (!seen)
                {
                    _img[node.Value].Dispose(); _img.Remove(node.Value);
                    if (_fit.TryGetValue(node.Value, out var f)) { f.bmp.Dispose(); _fit.Remove(node.Value); }
                    _imgOrder.Remove(node);
                }
                node = next;
            }
        }

        // ------------------------------------------------------------------ ratón y teclado
        void InvalidateItem(Item it)
        {
            if (it == null || !_rowById.TryGetValue(it.Id, out var r)) return;
            Invalidate(new Rectangle(0, r.Y - ScrollY - 2, ClientSize.Width, r.H + 4));
        }

        Row RowAt(Point p)
        {
            int y = p.Y + ScrollY;
            int k = FirstRowAt(y);
            if (k < _rows.Count && !_rows[k].Header && !_rows[k].Sep && y >= _rows[k].Y && y < _rows[k].Y + _rows[k].H) return _rows[k];
            return null;
        }

        void UpdateHover(Point p)
        {
            var it = RowAt(p)?.Item;
            if (it == _hover) return;
            var old = _hover; _hover = it;
            Cursor = it != null ? Cursors.Hand : Cursors.Default;
            InvalidateItem(old); InvalidateItem(it);
        }

        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); UpdateHover(e.Location); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); var old = _hover; _hover = null; InvalidateItem(old); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            var r = RowAt(e.Location);
            if (r != null) Select(r.Item.Id, false);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            var r = RowAt(e.Location);
            if (r != null) ItemActivated?.Invoke(r.Item.Id);
        }

        protected override bool IsInputKey(Keys k) => k == Keys.Up || k == Keys.Down || k == Keys.Enter || k == Keys.Home || k == Keys.End || base.IsInputKey(k);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            var cards = _rows.FindAll(r => r.Item != null);
            if (cards.Count == 0) return;
            int i = cards.FindIndex(r => r.Item.Id == SelectedId);
            switch (e.KeyCode)
            {
                case Keys.Down: i = Math.Min(cards.Count - 1, i + 1); break;
                case Keys.Up: i = Math.Max(0, i < 0 ? 0 : i - 1); break;
                case Keys.Home: i = 0; break;
                case Keys.End: i = cards.Count - 1; break;
                case Keys.Enter: if (SelectedId != null) ItemActivated?.Invoke(SelectedId); e.Handled = true; return;
                default: return;
            }
            e.Handled = true;
            Select(cards[i].Item.Id, true);
        }

        public void Select(string id, bool scrollTo)
        {
            if (SelectedId != id)
            {
                Item old = SelectedId != null && _rowById.TryGetValue(SelectedId, out var ro) ? ro.Item : null;
                SelectedId = id;
                InvalidateItem(old);
                if (id != null && _rowById.TryGetValue(id, out var rn)) InvalidateItem(rn.Item);
                SelectionChanged?.Invoke();
            }
            if (scrollTo && id != null && _rowById.TryGetValue(id, out var r))
            {
                int y = ScrollY;
                if (r.Y < y) y = r.Y - Theme.Px(34);
                else if (r.Y + r.H > y + ClientSize.Height) y = r.Y + r.H - ClientSize.Height + Theme.Px(8);
                if (y != ScrollY) { AutoScrollPosition = new Point(0, Math.Max(0, y)); Invalidate(); }
            }
        }
    }
}
