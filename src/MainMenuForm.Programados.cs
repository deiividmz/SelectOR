// SERVICIOS PROGRAMADOS (turnos), en Empresas → Servicios → «Programados» (servicios-programados.sql).
// El gerente, un gestor o el administrador programan servicios: ruta, un ITINERARIO hecho en el planificador (con sus
// paradas), un tren de la flota o cualquiera de viajeros / mercancías, la franja de salida y si se repite. Cualquier
// socio reserva un turno libre; «Preparar» deja Conducción libre con esa ruta y ese tren, y el itinerario del turno
// como «Tu viaje»: al conducir se carga solo en la hoja de ruta. El recorrido (.pat) desde el que sale lo elige cada
// maquinista (no todos tienen los mismos). Si el servicio que se registra encaja con
// el turno reservado, el SERVIDOR paga a la empresa una prima (un % del ingreso, con tope y límite diario) y el
// maquinista suma un turno cumplido en Mi perfil. Un turno reservado que no se hace caduca, sin castigo.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using ORTS.Menu;
using OrPath = ORTS.Menu.Path;

namespace SelectOR
{
    public partial class MainMenuForm
    {
        sealed class SchedRow
        {
            public string Id, SeriesId, Route, Path, TrainId, TrainName, TrainKind, Note, CreatedBy, DriverId, DriverName, ServiceId, State, ItinName;
            public double ItinKm; public bool HasItin;
            public DateTime Start, End; public DateTime? DoneAt; public double Bonus;
            public bool Mine => !string.IsNullOrEmpty(DriverId) && DriverId == Supa.UserId;
            public bool Open => State == "free" || State == "reserved";
        }

        readonly List<SchedRow> _schedRows = new();
        readonly List<SchedRow> _schedShown = new();
        SchedBoard _schedCards; Label _schedMsg; FlowLayoutPanel _schedChips; Control _schedPage;
        RoundButton _schedNewBtn, _schedReserveBtn, _schedReleaseBtn, _schedPrepBtn, _schedDelBtn;
        int _schedFilter, _schedSeq, _schedPending = -1;
        string _schedCompany; DateTime _schedLoadedUtc;
        double _schedPct = 20, _schedMax = 25000; int _schedDaily = 5;
        int _routeLoadedToken;   // último _routeToken cuyas actividades y recorridos ya están en las listas

        static bool NoSchedOnServer(string err) => err != null && (err.Contains("PGRST202") || err.Contains("Could not find the function"));

        // ---------------- pestaña ----------------
        Control BuildSchedPage()
        {
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, BackColor = Theme.Bg, Visible = false, Margin = new Padding(0) };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // filtros
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // turnos
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // acciones
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // mensaje

            _schedChips = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Theme.Bg, Margin = new Padding(0, 0, 0, 4) };
            string[] chips = { "Por hacer", "Mis turnos", "Cumplidos", "Caducados", "Todos" };
            for (int k = 0; k < chips.Length; k++)
            {
                int idx = k;
                var b = BankChip(Tr(chips[k]), k == 0);
                b.Click += (s, e) => { _schedFilter = idx; SetChipActive(_schedChips, idx); FillSched(); };
                _schedChips.Controls.Add(b);
            }
            t.Controls.Add(_schedChips);

            // un teleindicador de estación, con la composición 2D de cada tren (SchedBoard.cs)
            _schedCards = new SchedBoard
            {
                Dock = DockStyle.Fill, Margin = new Padding(2, 4, 2, 4),
                Title = Tr("SALIDAS PROGRAMADAS"), Heads = new[] { Tr("SALIDA"), Tr("DESTINO · RECORRIDO"), Tr("TREN"), Tr("ESTADO"), Tr("PRIMA") },
                LblAnyPax = Tr("cualquier tren de viajeros"), LblAnyFreight = Tr("cualquier tren de mercancías"), LblAny = Tr("cualquier tren"), LblDrawing = Tr("dibujando…"), LblNoImage = Tr("sin composición 2D"), LblNoTrain = Tr("sin tren asignado")
            };
            _schedCards.ImageLoader = SchedImageFileAsync;
            _schedCards.SelectedIndexChanged += (s, e) => UpdateSchedButtons();
            _schedCards.ItemActivated += o => { var r = SelectedSched(); if (r != null && r.Open && (r.Mine || r.State == "free")) PrepareSched(r); };
            t.Controls.Add(_schedCards);

            var btns = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, BackColor = Theme.Bg, Margin = new Padding(0) };
            _schedNewBtn = EmpButton(Tr("Programar servicio…"), primary: true); _schedNewBtn.Width = 220;
            _schedNewBtn.Click += (s, e) => NewSchedUi();
            _schedReserveBtn = EmpButton(Tr("Reservar")); _schedReserveBtn.Width = 150;
            _schedReserveBtn.Click += (s, e) => SchedAction("sched_reserve", Tr("Turno reservado. Con «Preparar» dejas listo Conducción libre."));
            _schedReleaseBtn = EmpButton(Tr("Soltar")); _schedReleaseBtn.Width = 130;
            _schedReleaseBtn.Click += (s, e) => SchedAction("sched_release", Tr("Turno libre de nuevo."));
            _schedPrepBtn = EmpButton(Tr("Preparar")); _schedPrepBtn.Width = 150;
            _schedPrepBtn.Click += (s, e) => { var r = SelectedSched(); if (r != null) PrepareSched(r); };
            _schedDelBtn = EmpButton(Tr("Quitar")); _schedDelBtn.Width = 130;
            _schedDelBtn.BaseColor = Theme.Surface2; _schedDelBtn.HoverColor = Color.FromArgb(150, 60, 60); _schedDelBtn.TextColor = RedC;
            _schedDelBtn.Click += (s, e) => DeleteSchedRow();
            foreach (var b in new[] { _schedNewBtn, _schedReserveBtn, _schedReleaseBtn, _schedPrepBtn, _schedDelBtn }) btns.Controls.Add(b);
            t.Controls.Add(btns);
            _schedMsg = EmpMsg(); _schedMsg.MaximumSize = new Size(900, 0);
            t.Controls.Add(_schedMsg);
            _schedPage = t;
            UpdateSchedButtons();
            return t;
        }

        SchedRow SelectedSched()
        {
            int i = _schedCards?.SelectedIndex ?? -1;
            return i >= 0 && i < _schedShown.Count ? _schedShown[i] : null;
        }

        bool SchedCanManage => CanManage() || Supa.IsSuperadmin;

        void UpdateSchedButtons()
        {
            if (_schedNewBtn == null) return;
            var r = SelectedSched();
            _schedNewBtn.Visible = SchedCanManage;
            _schedReserveBtn.Visible = r != null && r.State == "free";
            _schedReleaseBtn.Visible = r != null && r.State == "reserved" && (r.Mine || SchedCanManage);
            _schedPrepBtn.Visible = r != null && r.Open && (r.Mine || r.State == "free");
            _schedDelBtn.Visible = r != null && SchedCanManage && r.State != "done";
        }

        // Cambio de empresa o de pestaña: la lista de esa empresa.
        void ClearSched()
        {
            _schedRows.Clear(); _schedShown.Clear(); _schedCompany = null; _schedPending = -1;
            if (_schedCards != null) { _schedCards.EmptyText = ""; _schedCards.SetRows(Array.Empty<SchedBoard.Row>()); }
            UpdateServiceTabCounts();
        }

        async void LoadSched(EmpCompany c = null)
        {
            c ??= _empSel;
            if (c == null || !Supa.IsLoggedIn || _schedCards == null) return;
            int seq = ++_schedSeq;
            _schedLoadedUtc = DateTime.UtcNow;
            if (_schedCompany != c.Id) { _schedRows.Clear(); _schedShown.Clear(); _schedCards.EmptyText = Tr("Cargando…"); _schedCards.SetRows(Array.Empty<SchedBoard.Row>()); }
            var tCfg = Supa.RpcAsync("sched_settings", new { });
            var (json, err) = await Supa.RpcAsync("sched_list", new { p_company = c.Id, p_days = 14 });
            if (seq != _schedSeq || (_empSel != null && _empSel.Id != c.Id)) return;
            _schedMissing = NoSchedOnServer(err);
            if (err != null)
            {
                _schedRows.Clear(); _schedShown.Clear(); _schedPending = -1;
                _schedCards.EmptyText = _schedMissing ? Tr("El servidor aún no tiene los servicios programados (falta servicios-programados.sql).") : Tr("Error: ") + err;
                _schedCards.SetRows(Array.Empty<SchedBoard.Row>());
                UpdateSchedButtons(); UpdateServiceTabCounts();
                return;
            }
            try
            {
                var (cj, ce) = await tCfg;
                if (ce == null && !string.IsNullOrWhiteSpace(cj))
                {
                    using var d = JsonDocument.Parse(cj);
                    var root = d.RootElement;
                    if (root.ValueKind == JsonValueKind.Object)
                    {
                        _schedPct = Num(root, "pct"); _schedMax = Num(root, "max"); _schedDaily = (int)Num(root, "daily_limit");
                    }
                }
            }
            catch { }
            var rows = new List<SchedRow>();
            try
            {
                using var d = JsonDocument.Parse(json);
                foreach (var e in d.RootElement.EnumerateArray())
                {
                    var r = new SchedRow
                    {
                        Id = Str(e, "id"), SeriesId = Str(e, "series_id"), Route = Str(e, "route"), Path = Str(e, "path"),
                        TrainId = Str(e, "train_id"), TrainName = Str(e, "train_name"), TrainKind = Str(e, "train_kind"), Note = Str(e, "note"),
                        CreatedBy = Str(e, "created_by_name"), DriverId = Str(e, "driver_id"), DriverName = Str(e, "driver_name"),
                        ServiceId = Str(e, "service_id"), State = Str(e, "state"), Bonus = Num(e, "bonus"),
                        Start = SchedTime(Str(e, "starts_at")) ?? DateTime.MinValue, End = SchedTime(Str(e, "ends_at")) ?? DateTime.MinValue,
                        DoneAt = SchedTime(Str(e, "done_at")),
                        ItinName = Str(e, "itinerary_name"), ItinKm = Num(e, "itinerary_km"),
                        HasItin = e.TryGetProperty("has_itinerary", out var hi) && hi.ValueKind == JsonValueKind.True
                    };
                    rows.Add(r);
                }
            }
            catch { }
            _schedRows.Clear(); _schedRows.AddRange(rows);
            _schedCompany = c.Id;
            _schedPending = rows.Count(r => r.Open);
            FillSched();
            UpdateServiceTabCounts();
        }
        bool _schedMissing;

        static DateTime? SchedTime(string s)
            => !string.IsNullOrEmpty(s) && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d) ? d.ToLocalTime() : null;

        // «hoy 18:00 – 19:30», «mañana …» o «sáb 18 oct 18:00 – 19:30»
        static string SchedWindow(DateTime a, DateTime b)
        {
            var ci = I18n.English ? CultureInfo.GetCultureInfo("en-GB") : CultureInfo.GetCultureInfo("es-ES");
            string day = a.Date == DateTime.Today ? Tr("hoy") : a.Date == DateTime.Today.AddDays(1) ? Tr("mañana")
                       : a.Date == DateTime.Today.AddDays(-1) ? Tr("ayer") : a.ToString(I18n.English ? "ddd d MMM" : "ddd d MMM", ci);
            string end = b.Date == a.Date ? b.ToString("HH:mm", ci) : b.ToString("HH:mm", ci) + " (+1)";
            return day + " " + a.ToString("HH:mm", ci) + " – " + end;
        }

        // El itinerario del turno («🧭 Setenil → Parchite · 42,5 km»); los de antes, con su recorrido.
        string SchedWhere(SchedRow r)
        {
            var ci = I18n.English ? CultureInfo.GetCultureInfo("en-GB") : CultureInfo.GetCultureInfo("es-ES");
            if (r.HasItin) return "🧭 " + (string.IsNullOrEmpty(r.ItinName) ? Tr("Itinerario") : r.ItinName) + (r.ItinKm > 0 ? "  ·  " + r.ItinKm.ToString("N1", ci) + " km" : "");
            return string.IsNullOrEmpty(r.Path) ? Tr("cualquier recorrido") : r.Path;
        }

        string SchedTrainText(SchedRow r)
            => !string.IsNullOrEmpty(r.TrainId) || !string.IsNullOrEmpty(r.TrainName) ? "🚆 " + (string.IsNullOrEmpty(r.TrainName) ? Tr("(tren vendido)") : r.TrainName)
             : r.TrainKind == "pax" ? "🚆 " + Tr("cualquier tren de viajeros")
             : r.TrainKind == "freight" ? "🚆 " + Tr("cualquier tren de mercancías")
             : "🚆 " + Tr("cualquier tren");

        void FillSched()
        {
            if (_schedCards == null) return;
            _schedShown.Clear();
            foreach (var r in _schedRows)
            {
                bool ok = _schedFilter switch
                {
                    0 => r.Open,
                    1 => r.Mine,
                    2 => r.State == "done",
                    3 => r.State == "expired",
                    _ => true
                };
                if (ok) _schedShown.Add(r);
            }
            // por hacer: el más cercano arriba; el historial, el más reciente arriba
            if (_schedFilter == 0) _schedShown.Sort((a, b) => a.Start.CompareTo(b.Start));
            else _schedShown.Sort((a, b) => b.Start.CompareTo(a.Start));
            string selId = SelectedSched()?.Id;
            var ci = I18n.English ? CultureInfo.GetCultureInfo("en-GB") : CultureInfo.GetCultureInfo("es-ES");
            var rows = new List<SchedBoard.Row>();
            foreach (var r in _schedShown)
            {
                string who = r.State switch
                {
                    "free" => "",   // (ya lo dice la paleta)
                    "reserved" => r.Mine ? Tr("Reservado por ti") : string.Format(Tr("Reservado por {0}"), r.DriverName),
                    "done" => string.Format(Tr("Cumplido por {0}"), r.DriverName),
                    _ => string.IsNullOrEmpty(r.DriverName) ? Tr("Nadie lo reservó") : string.Format(Tr("Lo tenía {0}"), r.DriverName)
                };
                string flap = r.State switch
                {
                    "free" => Tr("Libre"), "reserved" => r.Mine ? Tr("Tuyo") : Tr("Reservado"),
                    "done" => "✓ " + Tr("Cumplido"), _ => Tr("Caducado")
                };
                Color flapC = r.State switch
                {
                    "free" => Color.FromArgb(120, 220, 120), "reserved" => r.Mine ? ColBlue : Color.FromArgb(255, 196, 64),
                    "done" => ColGold, _ => Color.FromArgb(150, 150, 150)
                };
                string day = r.Open && DateTime.Now >= r.Start && DateTime.Now <= r.End ? Tr("ahora")
                           : r.Start.Date == DateTime.Today ? Tr("hoy") : r.Start.Date == DateTime.Today.AddDays(1) ? Tr("mañana")
                           : r.Start.Date == DateTime.Today.AddDays(-1) ? Tr("ayer") : r.Start.ToString("ddd d MMM", ci);
                rows.Add(new SchedBoard.Row
                {
                    Id = r.Id, Time = r.Start.ToString("HH:mm", ci),
                    UntilLabel = string.Format(Tr("hasta {0}"), "").Trim(), UntilTime = r.End.ToString("HH:mm", ci), NextDay = r.End.Date != r.Start.Date, Day = day,
                    Route = r.Route ?? "", Path = SchedWhere(r), Note = r.Note ?? "",
                    HasTrain = !string.IsNullOrEmpty(r.TrainId) || !string.IsNullOrEmpty(r.TrainName),
                    Train = string.IsNullOrEmpty(r.TrainName) ? Tr("(tren vendido)") : r.TrainName, Kind = r.TrainKind ?? "",
                    State = r.State, StateText = flap, StateColor = flapC, Who = who, Past = !r.Open,
                    Now = r.Open && DateTime.Now >= r.Start && DateTime.Now <= r.End,
                    Right = r.State == "done" ? (r.Bonus > 0 ? "+" + r.Bonus.ToString("N2", ci) + " €" : Tr("sin prima"))
                          : r.State == "expired" ? "—" : _schedPct.ToString("0.##", ci) + " %",
                    RightSub = r.State == "done" ? Tr("prima del servidor") : r.State == "expired" ? "" : Tr("del ingreso")
                });
            }
            _schedCards.EmptyText = _schedFilter switch
            {
                0 => SchedCanManage ? Tr("No hay servicios programados por hacer. Crea uno con «Programar servicio…».")
                                    : Tr("No hay servicios programados por hacer. Los programan el gerente y los gestores."),
                1 => Tr("No tienes turnos. Reserva uno libre en «Por hacer»."),
                2 => Tr("Ningún turno cumplido en las dos últimas semanas."),
                3 => Tr("Ningún turno caducado en las dos últimas semanas."),
                _ => Tr("No hay servicios programados.")
            };
            _schedCards.SetRows(rows);
            _schedCards.SelectedIndex = selId == null ? -1 : _schedShown.FindIndex(x => x.Id == selId);
            UpdateSchedButtons();
            Msg(_schedMsg, string.Format(Tr("Prima: el {0} % del ingreso del servicio, hasta {1} por servicio y {2} primas al día por empresa. La paga el servidor: no sale de la tesorería ni cuenta como beneficio en la liga."),
                _schedPct.ToString("0.##", ci), _schedMax.ToString("N0", ci) + " €", _schedDaily), false);
        }

        // La composición 2D del tren de un turno: la del .con de tu contenido si lo tienes; si no, la que se guardó al darlo de alta.
        readonly Dictionary<string, Task<string>> _schedImg = new();
        Task<string> SchedImageFileAsync(SchedBoard.Row row)
        {
            var s = _schedRows.Find(x => x.Id == row.Id);
            if (s == null || string.IsNullOrEmpty(s.TrainId)) return Task.FromResult<string>(null);
            string key = _schedCompany + "|" + s.TrainId;
            if (_schedImg.TryGetValue(key, out var t) && !(t.IsCompleted && (t.IsFaulted || t.Result == null))) return t;
            return _schedImg[key] = SchedTrainImageAsync(_schedCompany, s.TrainId);
        }

        async Task<string> SchedTrainImageAsync(string companyId, string trainId)
        {
            if (_trainCon.TryGetValue(trainId, out var con) && !string.IsNullOrEmpty(con))
            {
                string f = await ConsistStripFileAsync(con);
                if (!string.IsNullOrEmpty(f)) return f;
            }
            string saved = SavedImageFile("tren", trainId);
            if (System.IO.File.Exists(saved)) return saved;
            return await CoTrainImageFileAsync(companyId, new CoTrain { Id = trainId, HasImage = true });
        }

        // Reservar / soltar el turno elegido.
        async void SchedAction(string rpc, string okText)
        {
            var r = SelectedSched();
            if (r == null) { Msg(_schedMsg, Tr("Elige un turno de la lista."), true); return; }
            var (_, err) = await Supa.RpcAsync(rpc, new { p_id = r.Id });
            if (err != null) { Msg(_schedMsg, NoSchedOnServer(err) ? Tr("El servidor aún no tiene los servicios programados (falta servicios-programados.sql).") : err, true); return; }
            LoadSched();
            Msg(_schedMsg, okText, false);
        }

        async void DeleteSchedRow()
        {
            var r = SelectedSched();
            if (r == null || !SchedCanManage) return;
            bool series = false;
            if (!string.IsNullOrEmpty(r.SeriesId) && _schedRows.Count(x => x.SeriesId == r.SeriesId && x.State != "done" && x.Start > r.Start) > 0)
            {
                var a = ThemedBox.Show(this, Tr("Este servicio se repite. ¿Quitar también los siguientes de la serie?\n\nSí: este y los siguientes · No: solo este"),
                                       "SelectOR", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (a == DialogResult.Cancel) return;
                series = a == DialogResult.Yes;
            }
            else if (ThemedBox.Show(this, string.Format(Tr("¿Quitar el servicio programado de {0} ({1})?"), r.Route, SchedWindow(r.Start, r.End)),
                                    "SelectOR", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            var (json, err) = await Supa.RpcAsync("sched_delete", new { p_id = r.Id, p_series = series });
            if (err != null) { Msg(_schedMsg, err, true); return; }
            LoadSched();
            int n = int.TryParse((json ?? "").Trim(), out var k) ? k : 1;
            Msg(_schedMsg, n == 1 ? Tr("Servicio programado quitado.") : string.Format(Tr("{0} servicios programados quitados."), n), false);
        }

        // ---------------- programar ----------------
        async void NewSchedUi()
        {
            var co = _empSel;
            if (co == null || !SchedCanManage) return;
            // trenes de la empresa (la misma lista guardada que usan Flota y Compra)
            var trains = new List<(string id, string name)>();
            try
            {
                var (tj, te, _) = await TrainListJsonAsync(co.Id);
                if (te == null)
                {
                    using var d = JsonDocument.Parse(tj);
                    foreach (var e in d.RootElement.EnumerateArray()) trains.Add((Str(e, "id"), Str(e, "name")));
                }
            }
            catch { }
            trains.Sort((a, b) => string.Compare(a.name, b.name, StringComparison.CurrentCultureIgnoreCase));

            var routes = _routesAll.Select(r => r.Name).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase)
                                   .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();
            if (routes.Count == 0) { Msg(_schedMsg, Tr("No hay rutas en esta carpeta de contenido."), true); return; }
            int ri = Math.Max(0, routes.FindIndex(n => string.Equals(n, _curRoute?.Name, StringComparison.OrdinalIgnoreCase)));

            using var dlg = new SchedDialog(routes, ri, trains, co.Name) { Pct = _schedPct, Max = _schedMax };
            // itinerarios guardados de la ruta (en este equipo) y el planificador sobre esa ruta
            dlg.ItinerariesOf = name =>
            {
                var route = _routesAll.Find(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
                string key = route == null ? "" : RouteKey(route.Path);
                return (_prefs.SavedItineraries ?? new List<SavedItinerary>()).Where(x => string.Equals(x.Route, key, StringComparison.OrdinalIgnoreCase))
                                                                             .OrderByDescending(x => x.Created).ToList();
            };
            dlg.ItineraryDeleted = id => _deletedItins.Contains(id);
            dlg.PlanItinerary = (name, cur) =>
            {
                var route = _routesAll.Find(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
                return route == null ? null : OpenItineraryPlanner(route, cur, true);
            };
            dlg.TrainImage = id => SchedTrainImageAsync(co.Id, id);
            string done = null;
            dlg.Submit = async r =>
            {
                var itin = new { r.Itinerary.Name, r.Itinerary.Km, r.Itinerary.Points, r.Itinerary.Halts };
                string st = r.Start.ToUniversalTime().ToString("o"), en = r.End.ToUniversalTime().ToString("o");
                // (ciertos días: con los días; las demás, como antes, para que valga también sin servicios-programados-dias.sql)
                var (json, err) = r.Repeat == "days"
                    ? await Supa.RpcAsync("sched_create", new
                    {
                        p_company = co.Id, p_route = r.Route, p_path = (string)null, p_train = r.TrainId, p_kind = r.Kind,
                        p_start = st, p_end = en, p_repeat = r.Repeat, p_count = r.Count, p_note = r.Note, p_itinerary = itin, p_offsets = r.Offsets
                    })
                    : await Supa.RpcAsync("sched_create", new
                    {
                        p_company = co.Id, p_route = r.Route, p_path = (string)null, p_train = r.TrainId, p_kind = r.Kind,
                        p_start = st, p_end = en, p_repeat = r.Repeat, p_count = r.Count, p_note = r.Note, p_itinerary = itin
                    });
                if (err != null && r.Repeat == "days" && (err.Contains("p_offsets") || err.Contains("PGRST202")))
                    return Tr("El servidor aún no programa en ciertos días de la semana (falta servicios-programados-dias.sql).");
                if (err != null && (err.Contains("p_itinerary") || err.Contains("PGRST202")))
                    return Tr("El servidor aún no guarda el itinerario de los turnos (falta servicios-programados-itinerario.sql).");
                if (err != null) return NoSchedOnServer(err) ? Tr("El servidor aún no tiene los servicios programados (falta servicios-programados.sql).") : err;
                int made = int.TryParse((json ?? "").Trim(), out var m) ? m : 1;
                done = made == 1 ? Tr("Servicio programado.") : string.Format(Tr("{0} servicios programados."), made);
                return null;
            };
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                LoadSched();
                Msg(_schedMsg, done ?? "", false);
            }
        }

        // ---------------- preparar ----------------
        // Deja Conducción libre con la ruta, el recorrido y el tren del turno, y de servicio con la empresa.
        async void PrepareSched(SchedRow s)
        {
            var co = _empSel;
            if (co == null) return;
            var route = _routesAll.Find(r => string.Equals(r.Name, s.Route, StringComparison.OrdinalIgnoreCase));
            if (route == null) { Msg(_schedMsg, string.Format(Tr("No tienes la ruta «{0}» en esta carpeta de contenido."), s.Route), true); return; }
            Msg(_schedMsg, Tr("Preparando Conducción libre…"), false);
            // el tren de la flota: el .con de tu contenido con esa composición (se busca mientras carga la ruta)
            Task<TrainItem> tTrain = !string.IsNullOrEmpty(s.TrainId) ? FindLocalTrainAsync(co.Id, s.TrainId) : Task.FromResult<TrainItem>(null);
            if (_curRoute?.Path != route.Path)
            {
                if (!_lstRoutes.Items.Cast<object>().Any(o => o is Route r && r.Path == route.Path))
                {
                    _routeSearch.Box.Text = ""; if (_chkFavOnly.Checked) _chkFavOnly.Checked = false;
                    RefreshRouteList();
                }
                SelectRouteInList(route);
            }
            // de servicio con la empresa del turno
            if (_empOnDutyCompany?.Id != co.Id) { _empOnDutyCompany = co; UpdateDutyUi(); }
            // el itinerario del turno, como «Tu viaje» de esa ruta: al conducir se carga solo en la hoja de ruta
            SavedItinerary itin = s.HasItin ? await SchedInstallItineraryAsync(s.Id, s.Route) : null;
            var t0 = DateTime.UtcNow;
            while ((_curRoute?.Path != route.Path || _routeLoadedToken != _routeToken) && (DateTime.UtcNow - t0).TotalSeconds < 30) await Task.Delay(100);
            var notes = new List<string>();
            if (s.HasItin && itin == null) notes.Add(Tr("No se ha podido cargar el itinerario del turno."));
            if (!s.HasItin && !string.IsNullOrEmpty(s.Path))
            {
                var p = _pathsAll.FirstOrDefault(x => string.Equals(x.Name, s.Path, StringComparison.OrdinalIgnoreCase));
                if (p == null) notes.Add(string.Format(Tr("No encuentro el recorrido «{0}» en tu ruta: elígelo a mano."), s.Path));
                else SelectTripPath(p);   // ese recorrido exacto (aunque otros vayan de la misma salida a la misma llegada)
            }
            TrainItem item = null;
            try { item = await tTrain; } catch { }
            if (!string.IsNullOrEmpty(s.TrainId) || !string.IsNullOrEmpty(s.TrainKind))
            {
                // sin filtros que lo escondan
                if (_consistSearch != null && _consistSearch.Box.Text.Length > 0) _consistSearch.Box.Text = "";
                if (_chkTrainFavOnly != null && _chkTrainFavOnly.Checked) _chkTrainFavOnly.Checked = false;
                int kind = item == null && s.TrainKind == "pax" ? 1 : item == null && s.TrainKind == "freight" ? 2 : 0;
                _trainKind = kind; SetChipActive(_trainChips, kind);
                if (_trainWarnChip != null) { _trainWarnChip.Active = false; _trainWarnChip.Invalidate(); }
                if (_trainFavChip != null) { _trainFavChip.Active = false; _trainFavChip.Invalidate(); }
                if (_cboTrainCompany != null && _cboTrainCompany.SelectedIndex > 0) _cboTrainCompany.SelectedIndex = 0;
                RefreshConsistList();
            }
            if (item != null)
            {
                for (int i = 0; i < _lstConsists.Items.Count; i++)
                    if (((TrainItem)_lstConsists.Items[i]).FilePath == item.FilePath) { _lstConsists.SelectedIndex = i; break; }
            }
            else if (!string.IsNullOrEmpty(s.TrainId))
                notes.Add(string.Format(Tr("No tienes en tu contenido el tren «{0}» con esa composición."), s.TrainName));
            ShowPage(2);
            UpdateStatus();
            UpdateItineraryChip();
            Msg(_schedMsg, notes.Count > 0 ? string.Join(" ", notes)
                         : itin != null ? string.Format(Tr("Listo en Conducción libre con el itinerario «{0}» en la hoja de ruta. Elige el recorrido desde el que sales."), s.ItinName)
                         : Tr("Listo en Conducción libre: ya puedes conducir."), notes.Count > 0);
            if (notes.Count > 0) ThemedBox.Show(this, Tr("Conducción libre está casi lista:") + "\n\n" + string.Join("\n", notes), "SelectOR", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // El .con de este equipo con la composición exacta de un tren de la empresa (primero el de su nombre de archivo).
        async Task<TrainItem> FindLocalTrainAsync(string companyId, string trainId)
        {
            List<TrainVeh> vs = null; string con = null;
            try
            {
                var (json, err, _) = await TrainListJsonAsync(companyId);
                if (err != null) return null;
                using var d = JsonDocument.Parse(json);
                foreach (var e in d.RootElement.EnumerateArray())
                {
                    if (Str(e, "id") != trainId) continue;
                    con = Str(e, "con_file");
                    if (e.TryGetProperty("vehicles", out var v) && v.ValueKind == JsonValueKind.Array) vs = v.EnumerateArray().Select(TrainVeh.FromJson).ToList();
                    break;
                }
            }
            catch { return null; }
            if (vs == null || vs.Count == 0) return null;
            string key = TrainKey(vs), lead = vs.FirstOrDefault(v => !v.Wagon)?.Name;
            string conName = string.IsNullOrEmpty(con) ? null : System.IO.Path.GetFileNameWithoutExtension(con);
            var all = (_consistsAll ?? new List<TrainItem>()).Where(c => c?.FilePath != null).ToList();
            return await Task.Run(() =>
            {
                try
                {
                    if (conName != null)
                        foreach (var c in all)
                            if (string.Equals(System.IO.Path.GetFileNameWithoutExtension(c.FilePath), conName, StringComparison.OrdinalIgnoreCase)
                                && TrainKey(TrainVehiclesOfCached(c)) == key) return c;
                    foreach (var c in all)
                        if (string.Equals(LeadEngineName(c), lead, StringComparison.OrdinalIgnoreCase) && TrainKey(TrainVehiclesOfCached(c)) == key) return c;
                }
                catch { }
                return null;
            });
        }

        // ---------------- al abrir y al cerrar el servicio ----------------
        // ¿Tengo ahora un turno reservado? El que encaja con esta ruta (hit) o, si no, otro que tengo a esta hora (other).
        async Task<(SchedRow hit, SchedRow other)> SchedNowAsync(string companyId, string route, string path)
        {
            try
            {
                var (json, err) = await Supa.RpcAsync("sched_list", new { p_company = companyId, p_days = 0 });
                if (err != null || string.IsNullOrWhiteSpace(json)) return (null, null);
                var now = DateTime.Now;
                SchedRow other = null;
                using var d = JsonDocument.Parse(json);
                foreach (var e in d.RootElement.EnumerateArray())
                {
                    if (Str(e, "state") != "reserved" || Str(e, "driver_id") != Supa.UserId) continue;
                    var a = SchedTime(Str(e, "starts_at")); var b = SchedTime(Str(e, "ends_at"));
                    if (a == null || b == null || now < a.Value.AddMinutes(-1) || now > b.Value) continue;
                    var r = new SchedRow { Id = Str(e, "id"), Route = Str(e, "route"), Path = Str(e, "path"), Start = a.Value, End = b.Value,
                                           ItinName = Str(e, "itinerary_name"), HasItin = e.TryGetProperty("has_itinerary", out var hi) && hi.ValueKind == JsonValueKind.True };
                    bool ok = string.Equals(r.Route?.Trim(), route?.Trim(), StringComparison.OrdinalIgnoreCase)
                           && (r.HasItin || string.IsNullOrEmpty(r.Path) || string.Equals(r.Path.Trim(), path?.Trim(), StringComparison.OrdinalIgnoreCase));
                    if (ok) return (r, null);
                    other ??= r;
                }
                return (null, other);
            }
            catch { return (null, null); }
        }

        // El aviso para el maquinista (o null).
        string SchedNoticeText(SchedRow hit, SchedRow other, bool itinLoaded)
        {
            if (hit != null)
                return "🗓 " + (itinLoaded ? string.Format(Tr("Turno programado: el itinerario «{0}» va en tu hoja de ruta. Si lo completas con el tren del turno, la empresa cobra una prima."), hit.ItinName)
                                           : Tr("Turno programado: si completas este servicio con el tren del turno, la empresa cobra una prima."));
            if (other != null) return "🗓 " + string.Format(Tr("Tienes un turno ahora en {0}: este servicio no lo cumple."), other.Route + (string.IsNullOrEmpty(other.Path) ? "" : " · " + other.Path));
            return null;
        }

        async Task<string> SchedStartNoticeAsync(string companyId, string route, string path)
        {
            var (hit, other) = await SchedNowAsync(companyId, route, path);
            return SchedNoticeText(hit, other, false);
        }

        // Al ponerse de servicio antes de lanzar OR: el itinerario del turno pasa a ser «Tu viaje» de la ruta (así la
        // hoja de ruta lo carga al arrancar) y el aviso sale después en la barra de arriba.
        async Task SchedBeforeLaunchAsync(string companyId, string route, string path)
        {
            var (hit, other) = await SchedNowAsync(companyId, route, path);
            _schedDriving = hit != null;   // prioridad en los desvíos de las salas (MainMenuForm.Agujas.cs)
            SavedItinerary it = hit != null && hit.HasItin ? await SchedInstallItineraryAsync(hit.Id, hit.Route) : null;
            string m = SchedNoticeText(hit, other, it != null);
            if (m == null) return;
            _ = Task.Delay(15000).ContinueWith(_ => { try { BeginInvoke((Action)(() => { try { _driveBar?.ShowMessage(m, false, 12); } catch { } })); } catch { } });
        }

        // Ponerse de servicio con OR ya en marcha: el itinerario del turno, a la hoja de ruta ahora mismo.
        async Task<string> SchedMidRunAsync(string companyId, string route, string path)
        {
            var (hit, other) = await SchedNowAsync(companyId, route, path);
            if (hit != null) _schedDriving = true;
            SavedItinerary it = hit != null && hit.HasItin ? await SchedInstallItineraryAsync(hit.Id, hit.Route) : null;
            if (it != null) { try { string lm = LoadSavedItinerary(it); if (HudAlive && lm != null) _serviceHud.MapNotice(lm); } catch { } }
            return SchedNoticeText(hit, other, it != null);
        }

        // El itinerario de un turno, guardado en este equipo como «Tu viaje» de su ruta (sustituye al de otro turno).
        async Task<SavedItinerary> SchedInstallItineraryAsync(string slotId, string routeName)
        {
            try
            {
                var route = _routesAll.Find(r => string.Equals(r.Name, routeName, StringComparison.OrdinalIgnoreCase));
                if (route == null || string.IsNullOrEmpty(slotId)) return null;
                var (json, err) = await Supa.RpcAsync("sched_itinerary", new { p_id = slotId });
                if (err != null || string.IsNullOrWhiteSpace(json) || json.Trim() == "null") return null;
                var it = JsonSerializer.Deserialize<SavedItinerary>(json);
                if (it?.Points == null || it.Points.Count < 2) return null;
                string key = RouteKey(route.Path);
                it.Id = "turno-" + slotId.Replace("-", "");
                it.Route = key;                                   // la carpeta de la ruta en ESTE equipo
                it.Name = Tr("Turno") + " · " + (string.IsNullOrWhiteSpace(it.Name) ? routeName : it.Name);
                it.Created = DateTime.Now;
                it.Halts = new Dictionary<string, bool>(it.Halts ?? new Dictionary<string, bool>(), StringComparer.OrdinalIgnoreCase);
                _prefs.SavedItineraries ??= new List<SavedItinerary>();
                // los de turnos anteriores no se acumulan
                _prefs.SavedItineraries.RemoveAll(x => x.Id != null && x.Id.StartsWith("turno-") && (x.Id == it.Id || x.Created < DateTime.Now.AddDays(-7)));
                _prefs.SavedItineraries.Add(it);
                _prefs.ExploreItinerary ??= new Dictionary<string, string>();
                _prefs.ExploreItinerary[key] = it.Id;
                try { _prefs.Save(); } catch { }
                UpdateItineraryChip();
                return it;
            }
            catch { return null; }
        }

        // Al registrar el servicio: el turno que ha cumplido (prima y si se pagó) o null.
        async Task<(bool done, double bonus)> SchedOfServiceAsync(string serviceId)
        {
            try
            {
                var (json, err) = await Supa.RpcAsync("sched_of_service", new { p_service = serviceId });
                if (err != null || string.IsNullOrWhiteSpace(json) || json.Trim() == "null") return (false, 0);
                using var d = JsonDocument.Parse(json);
                return d.RootElement.ValueKind == JsonValueKind.Object ? (true, Num(d.RootElement, "bonus")) : (false, 0);
            }
            catch { return (false, 0); }
        }

        // ---------------- Mi perfil ----------------
        int _profSchedDone;
        Label _profSchedVal;
        async Task LoadSchedMeritAsync()
        {
            try
            {
                var (json, err) = await Supa.RpcAsync("sched_driver_stats", new { p_driver = Supa.UserId });
                if (err != null || string.IsNullOrWhiteSpace(json)) { _profSchedDone = 0; SetKpi(_profSchedVal, NoSchedOnServer(err) ? "—" : "0"); return; }
                using var d = JsonDocument.Parse(json);
                _profSchedDone = (int)Num(d.RootElement, "done");
                SetKpi(_profSchedVal, _profSchedDone.ToString("N0", EsEs));
            }
            catch { }
        }

        // ---------------- Ajustes (administrador): la prima ----------------
        RoundedInput _schPct, _schMax, _schDaily;
        void AddSchedSettings(TableLayoutPanel p)
        {
            p.Controls.Add(EmpHeader("PRIMA DE LOS SERVICIOS PROGRAMADOS (GLOBAL, ADMINISTRADOR)"));
            var intro = EmpNote(Tr("Cuando un maquinista cumple un turno que tenía reservado, el servidor paga a su empresa un porcentaje del ingreso del servicio. No sale de la tesorería de nadie ni cuenta como beneficio en la liga."));
            intro.MaximumSize = new Size(360, 0); p.Controls.Add(intro);
            p.Controls.Add(FieldGrid(1, 280,
                (Tr("Prima: % del ingreso del servicio"), _schPct = EmpInput("20")),
                (Tr("Prima máxima por servicio (€)"), _schMax = EmpInput("25000")),
                (Tr("Primas al día por empresa (0 = ninguna)"), _schDaily = EmpInput("5"))));
            var save = EmpButton(Tr("Guardar prima"), primary: true); save.Width = 240;
            save.Click += (s, e) => SaveSchedSettings();
            p.Controls.Add(save);
        }

        async void LoadSchedSettings()
        {
            if (_schPct == null || !Supa.IsSuperadmin) return;
            var (json, err) = await Supa.RpcAsync("sched_settings", new { });
            if (err != null || string.IsNullOrWhiteSpace(json)) return;
            try
            {
                using var d = JsonDocument.Parse(json);
                var r = d.RootElement;
                _schPct.Box.Text = Num(r, "pct").ToString("0.##", EsEs);
                _schMax.Box.Text = Num(r, "max").ToString("0.##", EsEs);
                _schDaily.Box.Text = ((int)Num(r, "daily_limit")).ToString();
            }
            catch { }
        }

        async void SaveSchedSettings()
        {
            if (!Supa.IsSuperadmin) return;
            Msg(_tariffMsg, Tr("Guardando prima…"), false);
            var (_, err) = await Supa.RpcAsync("set_sched_settings", new
            {
                p_pct = ParseNum(_schPct.Box.Text), p_max = ParseNum(_schMax.Box.Text), p_daily_limit = (int)ParseNum(_schDaily.Box.Text)
            });
            if (err != null) { Msg(_tariffMsg, NoSchedOnServer(err) ? Tr("El servidor aún no tiene los servicios programados (falta servicios-programados.sql).") : Tr("Error: ") + err, true); return; }
            Msg(_tariffMsg, Tr("Prima guardada."), false);
            LoadSchedSettings();
        }
    }
}
