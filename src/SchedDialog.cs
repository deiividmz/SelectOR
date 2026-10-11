// «Programar servicio» (Empresas → Servicios → Programados): a la izquierda el formulario en cinco pasos (ruta e
// itinerario, tren, salida, repetición y nota) y a la derecha el billete del servicio, que se rellena mientras eliges:
// la ruta, la composición 2D del tren, la salida, cuántas salidas habrá y la prima.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SelectOR
{
    public sealed class SchedDialog : Form
    {
        public sealed class Result
        {
            public string Route, TrainId, Kind, Repeat, Note;   // Kind pax/freight/null · Repeat none/daily/weekly
            public SavedItinerary Itinerary;   // el que se cargará en la hoja de ruta del maquinista (el recorrido lo elige él)
            public DateTime Start, End; public int Count;
        }

        public Func<Result, Task<string>> Submit;            // hace el alta; devuelve el error o null
        public Func<string, List<SavedItinerary>> ItinerariesOf;            // itinerarios guardados de una ruta (por su nombre)
        public Func<string, SavedItinerary, SavedItinerary> PlanItinerary;  // abre el planificador (ruta, el elegido) → el que se usa
        public Func<string, Task<string>> TrainImage;        // archivo con el 2D de un tren de la flota
        public double Pct = 20, Max = 25000;

        readonly List<(string id, string name)> _trains;
        readonly ThemeCombo _route, _itin, _train, _day;
        readonly List<SavedItinerary> _itins = new();
        readonly RoundedInput _from, _to, _count, _note;
        readonly FlowLayoutPanel _kindChips, _repChips;
        readonly Label _countLbl, _err;
        readonly RoundButton _ok;
        readonly Ticket _ticket;
        int _kind, _rep, _imgTok;
        readonly Dictionary<string, Bitmap> _imgs = new();
        static CultureInfo Ci => I18n.English ? CultureInfo.GetCultureInfo("en-GB") : CultureInfo.GetCultureInfo("es-ES");
        static string T(string s) => I18n.T(s);

        public SchedDialog(IList<string> routes, int routeIndex, List<(string id, string name)> trains, string company)
        {
            _trains = trains ?? new();
            Text = T("Programar servicio");
            BackColor = Theme.Bg; ForeColor = Theme.Text; Font = Theme.Font(9.5f);
            StartPosition = FormStartPosition.CenterParent; FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            ClientSize = new Size(Theme.Px(920), Theme.Px(640));
            DialogKit.Dress(this);

            // ---- cabecera ----
            var stripe = new LiveryStripe { Dock = DockStyle.Top };
            var head = new DialogKit.Header(T("Programar servicio"), string.Format(T("Una salida para {0}: cualquier socio podrá reservarla."), company), "🗓");

            // ---- pie ----
            var foot = new Panel { Dock = DockStyle.Bottom, Height = Theme.Px(66), BackColor = Theme.Surface, Padding = new Padding(Theme.Px(24), Theme.Px(14), Theme.Px(24), Theme.Px(14)) };
            _ok = new RoundButton { Text = "🗓  " + T("Programar"), Width = Theme.Px(190), Dock = DockStyle.Right, Radius = 10, BaseColor = Theme.Accent, HoverColor = Theme.AccentHi, GradientTo = Theme.Accent2, TextColor = Color.White, FontSize = 10.5f, FontStyle = FontStyle.Bold };
            var cancel = new RoundButton { Text = T("Cancelar"), Width = Theme.Px(130), Dock = DockStyle.Right, Radius = 10, BaseColor = Theme.Surface2, HoverColor = Theme.SurfaceHi, TextColor = Theme.Text, FontSize = 10.5f };
            _err = new Label { Dock = DockStyle.Fill, ForeColor = Color.FromArgb(229, 115, 115), Font = Theme.Font(9f), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
            foot.Controls.Add(_err); foot.Controls.Add(new Panel { Width = Theme.Px(10), Dock = DockStyle.Right }); foot.Controls.Add(cancel); foot.Controls.Add(new Panel { Width = Theme.Px(10), Dock = DockStyle.Right }); foot.Controls.Add(_ok);
            foot.Paint += (s, e) => { using var p = new Pen(Theme.Border); e.Graphics.DrawLine(p, 0, 0, foot.Width, 0); };
            _ok.Click += async (s, e) => await Accept();
            cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };

            // ---- cuerpo: formulario + billete ----
            var body = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            int x = Theme.Px(28), y = Theme.Px(18), W = Theme.Px(520), half = (W - Theme.Px(16)) / 2;
            Label Step(int n, string text)
            {
                var l = new DialogKit.StepLabel { N = n, Text = text, Location = new Point(x, y), Size = new Size(W, Theme.Px(22)) };
                body.Controls.Add(l); y += Theme.Px(28); return l;
            }
            Label Cap(string text, int cx, int w) { var l = new Label { Text = text, Location = new Point(cx, y), Size = new Size(w, Theme.Px(17)), ForeColor = Theme.Subtle, Font = Theme.Font(8.5f) }; body.Controls.Add(l); return l; }
            ThemeCombo Combo(int cx, int w)
            {
                var c = new ThemeCombo { Location = new Point(cx, y), Width = w, Font = Theme.Font(9.75f), ItemHeight = Theme.Px(24), BackColor = Theme.Surface2, ForeColor = Theme.Text, MaxDropDownItems = 14 };
                c.DrawItem += DrawComboItem; body.Controls.Add(c); return c;
            }
            RoundedInput Input(int cx, int w, string ph) { var i = new RoundedInput(ph) { Location = new Point(cx, y), Width = w, Height = Theme.Px(34) }; body.Controls.Add(i); return i; }

            // 1 · ruta e itinerario (el recorrido .pat lo elige cada maquinista: no todos tienen los mismos)
            Step(1, T("RUTA E ITINERARIO"));
            int bw = Theme.Px(96);
            Cap(T("Ruta"), x, half); Cap(T("Itinerario (se carga en la hoja de ruta)"), x + half + Theme.Px(16), half); y += Theme.Px(19);
            _route = Combo(x, half); foreach (var r in routes) _route.Items.Add(r);
            _itin = Combo(x + half + Theme.Px(16), half - bw - Theme.Px(8));
            var plan = new RoundButton { Text = "🧭  " + T("Crear…"), Location = new Point(x + W - bw, y), Size = new Size(bw, Theme.Px(30)), Radius = 9, FontSize = 9f, FontStyle = FontStyle.Bold,
                                         BaseColor = Theme.Surface2, HoverColor = Theme.SurfaceHi, TextColor = Theme.Text };
            plan.Click += (s, e) => Plan();
            body.Controls.Add(plan);
            y += Theme.Px(48);

            // 2 · tren
            Step(2, T("TREN"));
            _kindChips = Chips(new[] { T("Cualquiera"), T("Viajeros"), T("Mercancías"), T("De la flota") }, i => { _kind = i; _train.Enabled = i == 3; Refresh_(); LoadImage(); });
            _kindChips.Location = new Point(x - Theme.Px(2), y); body.Controls.Add(_kindChips);
            y += Theme.Px(40);
            _train = Combo(x, W);
            if (_trains.Count == 0) { _train.Items.Add(T("La empresa aún no tiene trenes")); _kindChips.Controls[3].Enabled = false; }
            else foreach (var t in _trains) _train.Items.Add(t.name);
            _train.SelectedIndex = 0; _train.Enabled = false;
            y += Theme.Px(48);

            // 3 · salida
            Step(3, T("SALIDA"));
            int tw = (half - Theme.Px(16)) / 2;
            Cap(T("Día"), x, half); Cap(T("Desde"), x + half + Theme.Px(16), tw); Cap(T("Hasta"), x + half + Theme.Px(32) + tw, tw); y += Theme.Px(19);
            _day = Combo(x, half);
            for (int k = 0; k < 15; k++)
            {
                var d = DateTime.Today.AddDays(k);
                _day.Items.Add(k == 0 ? T("Hoy") + " · " + d.ToString("ddd d MMM", Ci) : k == 1 ? T("Mañana") + " · " + d.ToString("ddd d MMM", Ci) : d.ToString("dddd d MMMM", Ci));
            }
            _day.SelectedIndex = 0;
            var now = DateTime.Now; var from = now.AddMinutes(30 - now.Minute % 15);
            if (from.Date != now.Date) _day.SelectedIndex = 1;
            _from = Input(x + half + Theme.Px(16), tw, "18:00"); _from.Box.Text = from.ToString("HH:mm");
            _to = Input(x + half + Theme.Px(32) + tw, tw, "19:00"); _to.Box.Text = from.AddHours(1).ToString("HH:mm");
            foreach (var i in new[] { _from, _to })
            {
                i.Box.Font = new Font("Consolas", 11f * Theme.UiScale * Theme.DpiComp, FontStyle.Bold); i.Box.TextAlign = HorizontalAlignment.Center;
                var box = i.Box; box.Leave += (s, e) => { var t = Hm(box.Text); if (t != null) box.Text = t.Value.ToString(@"hh\:mm"); };   // «19.30» → «19:30»
            }
            y += Theme.Px(48);

            // 4 · repetición
            Step(4, T("REPETICIÓN"));
            _repChips = Chips(new[] { T("Una vez"), T("A diario"), T("Cada semana") }, i => { _rep = i; _count.Enabled = i > 0; if (i == 1 && Cnt() > 14) _count.Box.Text = "14"; if (i == 2 && Cnt() > 8) _count.Box.Text = "8"; Refresh_(); });
            _repChips.Location = new Point(x - Theme.Px(2), y); body.Controls.Add(_repChips);
            _count = Input(x + W - Theme.Px(150), Theme.Px(56), "5"); _count.Box.Text = "5"; _count.Box.TextAlign = HorizontalAlignment.Center; _count.Enabled = false;
            _count.Top = y - Theme.Px(2);
            _countLbl = new Label { Text = T("salidas"), Location = new Point(x + W - Theme.Px(88), y + Theme.Px(6)), Size = new Size(Theme.Px(88), Theme.Px(20)), ForeColor = Theme.Subtle, Font = Theme.Font(9f) };
            body.Controls.Add(_countLbl);
            y += Theme.Px(48);

            // 5 · nota
            Step(5, T("NOTA (OPCIONAL)"));
            _note = Input(x, W, T("p. ej. regional de la tarde, parar en todas"));
            _note.Box.MaxLength = 300;

            // billete
            _ticket = new Ticket { Location = new Point(x + W + Theme.Px(36), Theme.Px(14)), Size = new Size(Theme.Px(320), Theme.Px(470)) };
            body.Controls.Add(_ticket);

            Controls.Add(body); Controls.Add(foot); Controls.Add(head); Controls.Add(stripe);
            AcceptButton = null;

            _route.SelectedIndexChanged += (s, e) => { FillItins(null); Refresh_(); };
            _itin.SelectedIndexChanged += (s, e) => Refresh_();
            _day.SelectedIndexChanged += (s, e) => Refresh_();
            _train.SelectedIndexChanged += (s, e) => { Refresh_(); LoadImage(); };
            foreach (var i in new[] { _from, _to, _count, _note }) i.Box.TextChanged += (s, e) => Refresh_();
            if (_route.Items.Count > 0) _route.SelectedIndex = Math.Max(0, Math.Min(routeIndex, _route.Items.Count - 1));
            Load += (s, e) => { FillItins(null); Refresh_(); };   // (ItinerariesOf, Pct y Max se dan después del constructor)
            Shown += (s, e) => _route.Focus();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) foreach (var b in _imgs.Values) b?.Dispose();
            base.Dispose(disposing);
        }

        // ---- datos ----
        int Cnt() => int.TryParse(_count.Box.Text.Trim(), out var n) ? n : 0;

        static TimeSpan? Hm(string s)
        {
            s = (s ?? "").Trim().Replace('.', ':').Replace('h', ':');
            if (s.EndsWith(":")) s = s.TrimEnd(':');
            if (s.Length > 0 && !s.Contains(':')) s += ":00";
            return TimeSpan.TryParseExact(s, new[] { @"h\:mm", @"hh\:mm" }, CultureInfo.InvariantCulture, out var t) && t < TimeSpan.FromDays(1) ? t : null;
        }

        (DateTime start, DateTime end)? Window()
        {
            var a = Hm(_from.Box.Text); var b = Hm(_to.Box.Text);
            if (a == null || b == null) return null;
            var day = DateTime.Today.AddDays(Math.Max(0, _day.SelectedIndex));
            var st = day + a.Value; var en = day + b.Value;
            if (en <= st) en = en.AddDays(1);   // pasa de medianoche
            return (st, en);
        }

        int Times() => _rep == 0 ? 1 : Math.Max(1, Math.Min(_rep == 1 ? 14 : 8, Cnt()));

        Result Collect()
        {
            var w = Window();
            return new Result
            {
                Route = _route.SelectedItem as string,
                Itinerary = _itin.SelectedIndex > 0 && _itin.SelectedIndex - 1 < _itins.Count ? _itins[_itin.SelectedIndex - 1] : null,
                TrainId = _kind == 3 && _trains.Count > 0 && _train.SelectedIndex >= 0 ? _trains[_train.SelectedIndex].id : null,
                Kind = _kind == 1 ? "pax" : _kind == 2 ? "freight" : null,
                Repeat = _rep == 1 ? "daily" : _rep == 2 ? "weekly" : "none",
                Count = Times(), Note = _note.Box.Text.Trim(),
                Start = w?.start ?? DateTime.MinValue, End = w?.end ?? DateTime.MinValue
            };
        }

        // Los itinerarios guardados de la ruta elegida (y el que se acaba de crear, aunque aún no esté en la lista).
        void FillItins(SavedItinerary select)
        {
            string route = _route.SelectedItem as string;
            _itins.Clear();
            try { if (ItinerariesOf != null && route != null) _itins.AddRange(ItinerariesOf(route) ?? new()); } catch { }
            if (select != null && _itins.All(x => x.Id != select.Id)) _itins.Insert(0, select);
            _itin.BeginUpdate();
            _itin.Items.Clear();
            _itin.Items.Add(_itins.Count == 0 ? T("Sin itinerarios: créalo con «Crear…»") : T("Elige un itinerario…"));
            foreach (var it in _itins) _itin.Items.Add(it.Name + "  ·  " + it.Km.ToString("N1", Ci) + " km");
            _itin.EndUpdate();
            int i = select == null ? (_itins.Count == 1 ? 1 : 0) : _itins.FindIndex(x => x.Id == select.Id) + 1;
            _itin.SelectedIndex = Math.Max(0, i);
            Refresh_();
        }

        // «Crear…»: el planificador de itinerarios sobre la ruta elegida (con el elegido cargado, para retocarlo).
        void Plan()
        {
            string route = _route.SelectedItem as string;
            if (route == null || PlanItinerary == null) return;
            var cur = Collect().Itinerary;
            SavedItinerary got = null;
            try { got = PlanItinerary(route, cur); } catch { }
            FillItins(got ?? cur);
        }

        async void LoadImage()
        {
            int tok = ++_imgTok;
            if (_kind != 3 || _trains.Count == 0 || _train.SelectedIndex < 0) { _ticket.Image = null; _ticket.Loading = false; _ticket.Invalidate(); return; }
            string id = _trains[_train.SelectedIndex].id;
            if (_imgs.TryGetValue(id, out var have)) { _ticket.Image = have; _ticket.Loading = false; _ticket.Invalidate(); return; }
            _ticket.Image = null; _ticket.Loading = true; _ticket.Invalidate();
            Bitmap bmp = null;
            try
            {
                string file = TrainImage == null ? null : await TrainImage(id);
                if (!string.IsNullOrEmpty(file)) bmp = await Task.Run(() => VehicleThumbs.Decode(file, Theme.Px(70)));
            }
            catch { }
            if (IsDisposed) { bmp?.Dispose(); return; }
            _imgs[id] = bmp;
            if (tok != _imgTok) return;
            _ticket.Image = bmp; _ticket.Loading = false; _ticket.Invalidate();
        }

        // El billete, al día con lo elegido.
        void Refresh_()
        {
            if (_ticket == null) return;
            var r = Collect();
            _ticket.Route = r.Route ?? "—";
            _ticket.Path = r.Itinerary != null ? r.Itinerary.Name + "  ·  " + r.Itinerary.Km.ToString("N1", Ci) + " km" : T("sin itinerario todavía");
            _ticket.Kind = r.Kind ?? "";
            _ticket.Fleet = _kind == 3;
            _ticket.TrainText = _kind == 3 ? (_trains.Count > 0 && _train.SelectedIndex >= 0 ? _trains[_train.SelectedIndex].name : "—")
                              : _kind == 1 ? T("cualquier tren de viajeros") : _kind == 2 ? T("cualquier tren de mercancías") : T("cualquier tren");
            if (r.Start == DateTime.MinValue) { _ticket.When = T("Escribe las horas como 18:00."); _ticket.WhenBad = true; _ticket.Day = ""; }
            else
            {
                _ticket.WhenBad = false;
                _ticket.Time = r.Start.ToString("HH:mm", Ci) + " – " + r.End.ToString("HH:mm", Ci) + (r.End.Date != r.Start.Date ? " (+1)" : "");
                _ticket.Day = r.Start.ToString(I18n.English ? "dddd d MMMM" : "dddd d 'de' MMMM", Ci);
                _ticket.When = "";
            }
            int n = r.Count;
            if (r.Repeat == "none" || r.Start == DateTime.MinValue) _ticket.Repeat = T("Una sola salida");
            else
            {
                var last = r.Start.AddDays((r.Repeat == "daily" ? 1 : 7) * (n - 1));
                _ticket.Repeat = string.Format(T(r.Repeat == "daily" ? "{0} salidas, a diario, hasta el {1}" : "{0} salidas, cada semana, hasta el {1}"), n, last.ToString("ddd d MMM", Ci));
            }
            _ticket.Prima = string.Format(T("{0} % del ingreso · máx. {1}"), Pct.ToString("0.##", Ci), Max.ToString("N0", Ci) + " €");
            _ticket.Note = r.Note ?? "";
            _ticket.Invalidate();
        }

        async Task Accept()
        {
            if (!_ok.Enabled) return;
            var r = Collect();
            string err = null;
            if (string.IsNullOrEmpty(r.Route)) err = T("Falta la ruta");
            else if (r.Itinerary == null) err = T("Elige o crea el itinerario del servicio («Crear…»).");
            else if (r.Start == DateTime.MinValue) err = T("Escribe las horas como 18:00.");
            else if ((r.End - r.Start).TotalHours > 12) err = T("La franja de salida no puede pasar de 12 horas.");
            else if (r.End <= DateTime.Now) err = T("Esa franja ya ha pasado.");
            else if (_kind == 3 && r.TrainId == null) err = T("La empresa aún no tiene trenes");
            if (err == null && Submit != null)
            {
                _ok.Enabled = false; _err.ForeColor = Theme.Subtle; _err.Text = T("Programando…");
                try { err = await Submit(r); } catch (Exception ex) { err = ex.Message; }
                if (IsDisposed) return;
                _ok.Enabled = true;
            }
            if (err != null) { _err.ForeColor = Color.FromArgb(229, 115, 115); _err.Text = err; return; }
            DialogResult = DialogResult.OK; Close();
        }

        // ---- piezas ----
        FlowLayoutPanel Chips(string[] names, Action<int> onSelect)
        {
            var bar = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Theme.Bg, Margin = new Padding(0), Padding = new Padding(0) };
            using var f = Theme.Font(9f, FontStyle.Bold);
            for (int k = 0; k < names.Length; k++)
            {
                int idx = k;
                var b = new RoundButton { Text = names[k], Height = Theme.Px(32), Radius = 16, FontSize = 9f, FontStyle = FontStyle.Bold, Margin = new Padding(Theme.Px(2), 0, Theme.Px(6), 0), Active = k == 0, BaseColor = Theme.Surface, HoverColor = Theme.SurfaceHi, TextColor = Theme.Subtle };
                b.Width = TextRenderer.MeasureText(names[k], f).Width + Theme.Px(30);
                b.Click += (s, e) =>
                {
                    foreach (Control c in bar.Controls) if (c is RoundButton rb) { rb.Active = rb == b; rb.Invalidate(); }
                    onSelect(idx);
                };
                bar.Controls.Add(b);
            }
            return bar;
        }

        static void DrawComboItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || sender is not ComboBox c) return;
            bool sel = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
            using (var b = new SolidBrush(sel ? Theme.Accent : Theme.Surface2)) e.Graphics.FillRectangle(b, e.Bounds);
            TextRenderer.DrawText(e.Graphics, c.GetItemText(c.Items[e.Index]), c.Font, new Rectangle(e.Bounds.X + 8, e.Bounds.Y, e.Bounds.Width - 10, e.Bounds.Height),
                sel ? Color.White : Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }

        // El billete del servicio (papel, cabecera verde, 2D del tren, datos, matriz troquelada y código de barras).
        sealed class Ticket : Control
        {
            public string Route = "", Path = "", TrainText = "", Kind = "", Day = "", Time = "", When = "", Repeat = "", Prima = "", Note = "";
            public bool Fleet, Loading, WhenBad;
            public Bitmap Image;
            static readonly Color Paper = Color.FromArgb(248, 246, 238), Ink = Color.FromArgb(42, 42, 42), Faint = Color.FromArgb(118, 112, 100), Dash = Color.FromArgb(201, 194, 176);
            readonly Font _fBand = Theme.Font(8f, FontStyle.Bold), _fRoute = new Font("Consolas", 15f * Theme.UiScale * Theme.DpiComp, FontStyle.Bold),
                          _fPath = Theme.Font(9.5f), _fCap = Theme.Font(7.25f, FontStyle.Bold), _fVal = Theme.Font(9.5f, FontStyle.Bold),
                          _fTime = new Font("Consolas", 14f * Theme.UiScale * Theme.DpiComp, FontStyle.Bold), _fNote = Theme.Font(8.5f, FontStyle.Italic), _fSmall = Theme.Font(8f);
            public Ticket() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); }
            protected override void Dispose(bool disposing) { if (disposing) foreach (var f in new[] { _fBand, _fRoute, _fPath, _fCap, _fVal, _fTime, _fNote, _fSmall }) f.Dispose(); base.Dispose(disposing); }
            const TextFormatFlags L = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics; g.Clear(Theme.Bg);
                var r = new Rectangle(Theme.Px(4), Theme.Px(4), Width - Theme.Px(9), Height - Theme.Px(9));
                g.SmoothingMode = SmoothingMode.AntiAlias;
                // sombra y papel
                Theme.FillRound(g, new Rectangle(r.X + Theme.Px(3), r.Y + Theme.Px(4), r.Width, r.Height), Theme.Px(12), Color.FromArgb(70, 0, 0, 0));
                Theme.FillRound(g, r, Theme.Px(12), Paper);
                // banda superior
                var band = new Rectangle(r.X, r.Y, r.Width, Theme.Px(40));
                using (var path = TopRound(band, Theme.Px(12))) using (var b = new LinearGradientBrush(band, Theme.Accent, Theme.Accent2, LinearGradientMode.Horizontal)) g.FillPath(b, path);
                g.SmoothingMode = SmoothingMode.None;
                int x = r.X + Theme.Px(18), w = r.Width - Theme.Px(36);
                TextRenderer.DrawText(g, I18n.T("BILLETE DE SERVICIO PROGRAMADO"), _fBand, new Rectangle(x, band.Y, w, band.Height), Color.White, L);
                TextRenderer.DrawText(g, "🚆", _fPath, new Rectangle(x, band.Y, w, band.Height), Color.White, L | TextFormatFlags.Right);
                int y = band.Bottom + Theme.Px(14);
                // ruta y recorrido
                TextRenderer.DrawText(g, (Route ?? "").ToUpperInvariant(), _fRoute, new Rectangle(x, y, w, Theme.Px(26)), Ink, L); y += Theme.Px(28);
                TextRenderer.DrawText(g, "→ " + Path, _fPath, new Rectangle(x, y, w, Theme.Px(18)), Faint, L); y += Theme.Px(26);
                // tren: 2D como en Flota (o la silueta)
                TextRenderer.DrawText(g, I18n.T("TREN"), _fCap, new Rectangle(x, y, w, Theme.Px(14)), Faint, L); y += Theme.Px(16);
                TextRenderer.DrawText(g, TrainText, _fVal, new Rectangle(x, y, w, Theme.Px(18)), Ink, L); y += Theme.Px(22);
                var strip = new Rectangle(x, y, w, Theme.Px(60));
                CardPaint.Rail_(g, strip, Fleet ? Image : null, Fleet && Loading ? I18n.T("dibujando…") : null, _fSmall);
                if (!Fleet || (Image == null && !Loading))
                    SchedBoard.Silhouette(g, new Rectangle(strip.X + Theme.Px(10), strip.Y + Theme.Px(6), strip.Width - Theme.Px(20), strip.Height - Theme.Px(14)), Fleet ? "" : Kind, false);
                y = strip.Bottom + Theme.Px(14);
                // salida
                TextRenderer.DrawText(g, I18n.T("SALIDA"), _fCap, new Rectangle(x, y, w, Theme.Px(14)), Faint, L); y += Theme.Px(16);
                if (WhenBad) { TextRenderer.DrawText(g, When, _fVal, new Rectangle(x, y, w, Theme.Px(20)), Color.FromArgb(178, 59, 59), L); y += Theme.Px(24); }
                else
                {
                    TextRenderer.DrawText(g, Time, _fTime, new Rectangle(x, y, w, Theme.Px(24)), Color.FromArgb(150, 100, 10), L); y += Theme.Px(26);
                    TextRenderer.DrawText(g, Day, _fPath, new Rectangle(x, y, w, Theme.Px(18)), Ink, L); y += Theme.Px(22);
                }
                TextRenderer.DrawText(g, Repeat, _fSmall, new Rectangle(x, y, w, Theme.Px(16)), Faint, L); y += Theme.Px(24);
                // prima
                TextRenderer.DrawText(g, I18n.T("PRIMA DEL SERVIDOR"), _fCap, new Rectangle(x, y, w, Theme.Px(14)), Faint, L); y += Theme.Px(16);
                TextRenderer.DrawText(g, Prima, _fVal, new Rectangle(x, y, w, Theme.Px(18)), Color.FromArgb(46, 125, 50), L); y += Theme.Px(26);
                // troquel: línea discontinua con muescas a los lados
                int ty = Math.Max(y, r.Bottom - Theme.Px(78));
                using (var p = new Pen(Dash, 1.4f) { DashStyle = DashStyle.Dash }) g.DrawLine(p, r.X + Theme.Px(14), ty, r.Right - Theme.Px(14), ty);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                int nd = Theme.Px(16);
                using (var b = new SolidBrush(Theme.Bg)) { g.FillEllipse(b, r.X - nd / 2, ty - nd / 2, nd, nd); g.FillEllipse(b, r.Right - nd / 2, ty - nd / 2, nd, nd); }
                g.SmoothingMode = SmoothingMode.None;
                // matriz: la nota (o quién puede reservarlo) y el código de barras
                string stub = Note.Length > 0 ? "«" + Note + "»" : I18n.T("Cualquier socio podrá reservarlo.");
                TextRenderer.DrawText(g, stub, Note.Length > 0 ? _fNote : _fSmall, new Rectangle(x, ty + Theme.Px(10), w, Theme.Px(18)), Faint, L);
                var rnd = new Random((Route + Time + Day).GetHashCode());
                int bx = x, by = ty + Theme.Px(34), bh = Theme.Px(26);
                using (var b = new SolidBrush(Ink))
                    while (bx < x + w)
                    {
                        int bw = rnd.Next(1, 4);
                        if (rnd.Next(3) > 0) g.FillRectangle(b, bx, by, bw, bh);
                        bx += bw + rnd.Next(1, 3);
                    }
            }

            static GraphicsPath TopRound(Rectangle r, int rad)
            {
                var p = new GraphicsPath(); int d = rad * 2;
                p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                p.AddLine(r.Right, r.Bottom, r.X, r.Bottom); p.CloseFigure();
                return p;
            }
        }
    }
}
