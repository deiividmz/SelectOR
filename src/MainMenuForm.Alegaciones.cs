// ALEGACIONES y QUITAR UNA ANULACIÓN (1.2.65, sql/alegaciones.sql).
//  · El maquinista alega una sanción de su servicio (una infracción) o la anulación de un servicio suyo, desde el
//    informe del servicio (ServiceResultDialog: al acabar de conducir o desde Servicios). Una alegación por cada una.
//  · El administrador las ve en Revisión → «Alegaciones» y las ACEPTA (anula la sanción y devuelve los puntos, o quita
//    la anulación del servicio) o las RECHAZA, siempre con una respuesta. Al maquinista le llega el aviso.
//  · El administrador puede quitar la anulación de un servicio también desde Servicios o desde su informe: vuelve a
//    estar normal y su dinero vuelve a la tesorería.
// (Sustituye a la revisión de Viajeros, Plazas, Por empresa y Tipo cambiado, que se quitaron a petición.)

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SelectOR
{
    public partial class MainMenuForm
    {
        Label _revIntro; FlowLayoutPanel _revTabBar; RoundedInput _revSearch;
        static readonly string RevIntroInfr = "Todas las infracciones del carné de todos los usuarios. En cualquier momento puedes aprobarlas (los puntos quedan restados), anularlas (se devuelven los puntos) o eliminarlas (se devuelven los puntos y se borran). Los excesos de velocidad (B6 y B7) llegan pendientes de revisión.";
        static readonly string RevIntroAp = "Alegaciones de los maquinistas a una sanción de su servicio o a la anulación de un servicio. Acéptala (se anula la sanción y se devuelven los puntos, o el servicio vuelve a estar normal) o recházala; en los dos casos, con una respuesta que le llega al maquinista.";
        const int RevTabAppeals = 4;

        sealed class AppealRow
        {
            public string Id, Kind, Status, Body, Answer, Driver, Company, Route, Consist, Code, InfrStatus, AnnulReason, ServiceId, InfractionId;
            public string Created, Answered, Detail; public int Points; public double Km, Net; public bool Annulled;
            public string What => Kind == "infraction"
                ? Carne.Label(Code) + (Points != 0 ? " (" + Carne.PointsText(Points) + ")" : "")
                : I18n.T("Anulación del servicio") + (Km > 0 ? " · " + Km.ToString("N1", EsEs) + " km" : "");
        }
        readonly List<AppealRow> _algRows = new();
        Panel _algPanel; CardTable _algList; Label _algDetail, _algMsg; RoundButton _algAccept, _algReject;
        int _algOpen;

        // ---------------------------------------------------------------- Revisión → Alegaciones (administrador)
        Panel BuildAppealsPanel()
        {
            _algPanel = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Visible = false };
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Theme.Bg };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
            _algList = new CardTable
            {
                Dock = DockStyle.Fill, Margin = new Padding(0, 0, 8, 0), Icon = "✉", CardHeight = 92, MinWidth = 380,
                TitleCol = 0, SubCols = new[] { 1, 2 }, QuoteCol = 3, PillCol = 4
            };
            _algList.SelectedIndexChanged += (s, e) => ShowAppeal();
            var right = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Padding = new Padding(16, 12, 16, 12), Margin = new Padding(0) };
            _algDetail = new Label { Dock = DockStyle.Fill, ForeColor = Theme.Text, Font = Theme.Font(9.5f), Text = Tr("Elige una alegación de la lista.") };
            var btns = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, BackColor = Theme.Surface };
            _algAccept = EmpButton(Tr("Aceptar…"), primary: true); _algAccept.Width = 150;
            _algAccept.Click += (s, e) => AnswerAppeal(true);
            _algReject = EmpButton(Tr("Rechazar…")); _algReject.Width = 150; _algReject.Margin = new Padding(8, 10, 2, 2);
            _algReject.Click += (s, e) => AnswerAppeal(false);
            _algMsg = EmpMsg(); _algMsg.MaximumSize = new Size(420, 0);
            btns.Controls.Add(_algAccept); btns.Controls.Add(_algReject); btns.Controls.Add(_algMsg);
            right.Controls.Add(_algDetail); right.Controls.Add(btns);
            t.Controls.Add(_algList, 0, 0); t.Controls.Add(right, 1, 0);
            _algPanel.Controls.Add(t);
            return _algPanel;
        }

        // Al cambiar de pestaña en Revisión: las infracciones o las alegaciones.
        void RevTabChanged(int i)
        {
            bool ap = i == RevTabAppeals;
            if (_algPanel != null) _algPanel.Visible = ap;
            if (_revList != null) _revList.Visible = !ap;
            if (_revBtns != null) _revBtns.Visible = !ap;
            if (_revIntro != null) _revIntro.Text = Tr(ap ? RevIntroAp : RevIntroInfr);
            if (ap) LoadAppeals();
        }

        async void LoadAppeals()
        {
            if (!Supa.IsSuperadmin || _algList == null) return;
            var (json, err) = await Supa.RpcAsync("admin_appeals", new { p_limit = 1000 });
            _algRows.Clear();
            if (err != null)
            {
                _algList.SetEmpty(err.IndexOf("PGRST202", StringComparison.OrdinalIgnoreCase) >= 0 || err.IndexOf("Could not find", StringComparison.OrdinalIgnoreCase) >= 0
                    ? Tr("El servidor aún no tiene las alegaciones (falta alegaciones.sql).") : Tr("Error: ") + err);
                return;
            }
            try
            {
                using var d = JsonDocument.Parse(json);
                foreach (var e in d.RootElement.EnumerateArray())
                    _algRows.Add(new AppealRow
                    {
                        Id = Str(e, "id"), Kind = Str(e, "kind"), Status = Str(e, "status"), Body = Str(e, "body"), Answer = Str(e, "answer"),
                        Driver = Str(e, "username") is { Length: > 0 } u ? u : "—", Company = Str(e, "company_name") is { Length: > 0 } c ? c : "—",
                        Route = Str(e, "route"), Consist = Str(e, "consist"), Code = Str(e, "code"), InfrStatus = Str(e, "infr_status"),
                        AnnulReason = Str(e, "annul_reason"), ServiceId = Str(e, "service_id"), InfractionId = Str(e, "infraction_id"),
                        Created = FmtDate(Str(e, "created_at")), Answered = FmtDate(Str(e, "answered_at")),
                        Detail = e.TryGetProperty("detail", out var dd) && Str(e, "code").Length > 0 ? Carne.Detail(Str(e, "code"), dd) : "",
                        Points = (int)Num(e, "points"), Km = Num(e, "km"), Net = Num(e, "net"),
                        Annulled = e.TryGetProperty("annulled", out var an) && an.ValueKind == JsonValueKind.True
                    });
            }
            catch { }
            _algOpen = _algRows.Count(r => r.Status == "open");
            FillAppeals();
        }

        static string AppealStatusName(string st) => st == "accepted" ? I18n.T("Aceptada") : st == "rejected" ? I18n.T("Rechazada") : I18n.T("Sin contestar");
        static Color AppealStatusColor(string st) => st == "accepted" ? Color.FromArgb(129, 199, 132) : st == "rejected" ? Color.FromArgb(229, 115, 115) : Color.FromArgb(255, 196, 84);

        // (también con el filtro de Revisión)
        void FillAppeals()
        {
            if (_algList == null) return;
            string keep = SelectedAppeal()?.Id;
            var words = _revQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            _algList.ClearRows(); _algShown.Clear();
            foreach (var r in _algRows)
            {
                string all = (r.Driver + " " + r.Company + " " + r.Route + " " + r.What + " " + r.Body + " " + r.Answer).ToLowerInvariant();
                if (words.Any(w => all.IndexOf(w, StringComparison.Ordinal) < 0)) continue;
                _algShown.Add(r);
                _algList.AddRow(new[] { r.Driver + " · " + r.Company, r.What, (r.Route.Length > 0 ? r.Route + " · " : "") + r.Created, r.Body, AppealStatusName(r.Status) },
                               new Color?[] { null, null, null, null, AppealStatusColor(r.Status) }, null, r.Id);
            }
            if (_algShown.Count == 0) _algList.SetEmpty(Tr("No hay alegaciones."));
            if (keep != null) { int k = _algShown.FindIndex(x => x.Id == keep); if (k >= 0) _algList.SelectRow(k); }
            ShowAppeal();
        }
        readonly List<AppealRow> _algShown = new();

        AppealRow SelectedAppeal() { int i = _algList?.SelectedRow ?? -1; return i >= 0 && i < _algShown.Count ? _algShown[i] : null; }

        void ShowAppeal()
        {
            if (_algDetail == null) return;
            var r = SelectedAppeal();
            bool open = r != null && r.Status == "open";
            _algAccept.Enabled = _algReject.Enabled = open;
            if (r == null) { _algDetail.Text = Tr("Elige una alegación de la lista."); return; }
            var l = new List<string>
            {
                r.Driver + " · " + r.Company + " · " + r.Created,
                "",
                (r.Kind == "infraction" ? Tr("Sanción: ") : Tr("Servicio anulado: ")) + r.What,
            };
            if (r.Route.Length > 0 || r.Consist.Length > 0) l.Add(r.Route + (r.Consist.Length > 0 ? " · " + r.Consist : ""));
            if (r.Detail.Length > 0) l.Add(r.Detail);
            if (r.Kind == "infraction" && r.InfrStatus == "annulled") l.Add(Tr("(la sanción ya está anulada)"));
            if (r.Kind == "annulment") l.Add(r.Annulled ? Tr("Motivo de la anulación: ") + (r.AnnulReason.Length > 0 ? r.AnnulReason : "—") : Tr("(el servicio ya no está anulado)"));
            l.Add(""); l.Add(Tr("ALEGACIÓN")); l.Add("«" + r.Body + "»");
            if (!open)
            {
                l.Add(""); l.Add(AppealStatusName(r.Status).ToUpperInvariant() + (r.Answered.Length > 0 ? " · " + r.Answered : ""));
                if (r.Answer.Length > 0) l.Add("«" + r.Answer + "»");
            }
            else
            {
                l.Add("");
                l.Add(r.Kind == "infraction" ? Tr("Aceptar = anular la sanción y devolverle los puntos.") : Tr("Aceptar = quitar la anulación: el servicio vuelve a estar normal y su dinero vuelve a la tesorería."));
            }
            _algDetail.Text = string.Join(Environment.NewLine, l);
        }

        async void AnswerAppeal(bool accept)
        {
            var r = SelectedAppeal();
            if (r == null || r.Status != "open") return;
            string answer;
            using (var dlg = new TextPromptDialog(accept ? Tr("Aceptar la alegación") : Tr("Rechazar la alegación"),
                       string.Format(Tr("Alegación de {0}: {1}. Tu respuesta (la verá el maquinista):"), r.Driver, r.What),
                       "", accept ? Tr("Por ejemplo: revisado, tenías razón") : Tr("Por ejemplo: el exceso está claro en el registro"),
                       accept ? Tr("Aceptar") : Tr("Rechazar"), "✉"))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                answer = (dlg.Value ?? "").Trim();
            }
            if (answer.Length < 3) { Msg(_algMsg, Tr("Escribe la respuesta."), true); return; }
            Msg(_algMsg, Tr("Enviando…"), false);
            var (_, err) = await Supa.RpcAsync("admin_appeal_answer", new { p_id = r.Id, p_decision = accept ? "accept" : "reject", p_answer = answer });
            if (err != null) { Msg(_algMsg, Tr("Error: ") + err, true); return; }
            Msg(_algMsg, accept ? Tr("Alegación aceptada. El maquinista ha recibido la respuesta.") : Tr("Alegación rechazada. El maquinista ha recibido la respuesta."), false);
            LoadAppeals();
            LoadReview(onlyCount: true);
        }

        async Task<int> AppealsOpenCount()
        {
            if (!Supa.IsSuperadmin) return 0;
            try
            {
                var (json, err) = await Supa.RpcAsync("admin_appeals_open", new { });
                if (err == null && int.TryParse(json?.Trim(), out int n)) { _algOpen = n; return n; }
            }
            catch { }
            return 0;
        }

        // ---------------------------------------------------------------- el maquinista
        // Alegar: pide el texto y lo manda. null = enviada · "" = cancelada · otro = el error.
        async Task<string> AppealCreate(IWin32Window owner, string kind, string refId, string what)
        {
            string body;
            using (var dlg = new TextPromptDialog(kind == "infraction" ? Tr("Alegar la sanción") : Tr("Alegar la anulación"),
                       string.Format(Tr("{0}. Explica por qué no estás de acuerdo: lo leerá el administrador y te contestará. Solo se puede alegar una vez."), what),
                       "", Tr("Tu alegación"), Tr("Enviar"), "✉"))
            {
                if (dlg.ShowDialog(owner ?? this) != DialogResult.OK) return "";
                body = (dlg.Value ?? "").Trim();
            }
            if (body.Length < 3) return Tr("Escribe tu alegación.");
            var (_, err) = await Supa.RpcAsync("appeal_create", new { p_kind = kind, p_ref = refId, p_text = body });
            if (err != null)
                return err.IndexOf("PGRST202", StringComparison.OrdinalIgnoreCase) >= 0 || err.IndexOf("Could not find", StringComparison.OrdinalIgnoreCase) >= 0
                    ? Tr("El servidor aún no tiene las alegaciones (falta alegaciones.sql).") : Tr("Error: ") + err;
            return null;
        }

        // El informe de un servicio mío: lo que ya alegué y cómo alegar (sin alegaciones.sql en el servidor, nada).
        async Task AttachAppeals(ServiceResultDialog.Data d)
        {
            var m = await MyAppealsMap();
            if (m == null) return;
            d.Appeals = m;
            d.AppealHook = (owner, kind, refId, what) => AppealCreate(owner, kind, refId, what);
        }

        // Mis alegaciones: por la sanción (su id) o por el servicio anulado («svc:» + su id). null = el servidor no las tiene.
        async Task<Dictionary<string, (string status, string answer)>> MyAppealsMap()
        {
            var m = new Dictionary<string, (string, string)>();
            try
            {
                var (json, err) = await Supa.RpcAsync("my_appeals", new { });
                if (err != null) return null;
                using var d = JsonDocument.Parse(json);
                foreach (var e in d.RootElement.EnumerateArray())
                {
                    string key = Str(e, "kind") == "infraction" ? Str(e, "infraction_id") : "svc:" + Str(e, "service_id");
                    if (key.Length > 0) m[key] = (Str(e, "status"), Str(e, "answer"));
                }
            }
            catch { }
            return m;
        }

        // Las infracciones de un servicio (el informe desde el historial; my_service_infractions).
        async Task<List<Carne.Item>> ServiceInfractions(string serviceId)
        {
            try
            {
                var (json, err) = await Supa.RpcAsync("my_service_infractions", new { p_service = serviceId });
                if (err == null) { using var d = JsonDocument.Parse(json); return Carne.ParseItems(d.RootElement); }
            }
            catch { }
            return new List<Carne.Item>();
        }

        // ---------------------------------------------------------------- quitar la anulación (administrador)
        // null = hecho · "" = cancelado · otro = el error.
        async Task<string> UnannulService(IWin32Window owner, string serviceId, string label)
        {
            if (!Supa.IsSuperadmin) return "";
            string reason;
            using (var dlg = new TextPromptDialog(Tr("Quitar la anulación"),
                       string.Format(Tr("{0}. El servicio vuelve a estar normal (cuenta para el ingreso, la liga, el ranking y el rango) y su dinero vuelve a la tesorería. Motivo (opcional, lo verá el maquinista):"), label),
                       "", Tr("Por ejemplo: revisado, era correcto"), Tr("Quitar la anulación"), "↩"))
            {
                if (dlg.ShowDialog(owner ?? this) != DialogResult.OK) return "";
                reason = (dlg.Value ?? "").Trim();
            }
            var (_, err) = await Supa.RpcAsync("unannul_service", new { p_service = serviceId, p_reason = reason.Length > 0 ? reason : null });
            if (err != null)
                return err.IndexOf("PGRST202", StringComparison.OrdinalIgnoreCase) >= 0 || err.IndexOf("Could not find", StringComparison.OrdinalIgnoreCase) >= 0
                    ? Tr("El servidor aún no permite quitar anulaciones (falta alegaciones.sql).") : Tr("Error: ") + err;
            LoadCompanies();
            if (_empSel != null) LoadServices(_empSel);
            return null;
        }

        // Servicios → «Quitar la anulación…» (el servicio elegido).
        async void UnannulServiceRow()
        {
            if (!Supa.IsSuperadmin || _svcCards == null) return;
            int i = SelectedServiceIndex();
            if (i < 0 || i >= _svcRows.Count) { Msg(_empHomeMsg, Tr("Selecciona un servicio de la lista."), true); return; }
            var s = _svcRows[i];
            if (!s.Annulled) { Msg(_empHomeMsg, Tr("Ese servicio no está anulado."), true); return; }
            string err = await UnannulService(this, s.Id, string.Format(Tr("Servicio de {0} · {1} · {2}"), s.Driver, s.Route, s.Date));
            if (err == null) Msg(_empHomeMsg, Tr("Anulación quitada: el servicio vuelve a estar normal. El maquinista ha recibido un aviso."), false);
            else if (err.Length > 0) Msg(_empHomeMsg, err, true);
        }
    }
}
