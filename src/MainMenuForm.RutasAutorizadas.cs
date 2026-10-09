// Rutas autorizadas (servidor: rutas-autorizadas.sql; datos y plano: RouteAuth.cs; dibujo del plano: RoutePlanView.cs).
//  · Al ponerse de servicio se pide permiso para la ruta (RouteID + huella del .tdb). En modo «Aviso» se avisa una
//    vez; en «Obligatorio» no hay servicio si la ruta (o esa versión) no está autorizada. Si es la primera vez que
//    el servidor ve esa versión, se le sube en segundo plano su ficha y su plano.
//  · Empresas → Rutas (todos los socios): las rutas que afectan a la empresa —las pedidas y las que usan sus
//    maquinistas— y las instaladas en este equipo, con su estado. El gerente y los gestores las solicitan.
//  · Empresas → Catálogo de rutas (solo el superadministrador): todas las rutas, con su ficha, alertas, el plano
//    (aunque no tenga la ruta), sus versiones y las empresas a las que afecta; autorizar, rechazar o retirar.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SelectOR
{
    public partial class MainMenuForm
    {
        const int RutasSubtab = 15, CatalogoSubtab = 16;

        // ============================ al ponerse de servicio ============================
        readonly HashSet<string> _routeWarned = new(), _routePlanSent = new();

        // null: se puede seguir. Si no, el motivo por el que no hay servicio (modo obligatorio).
        // dir/name: la ruta (por defecto, la elegida). quiet: sin ventanas (con Open Rails en marcha).
        async Task<string> RouteGateAsync(string companyId, string dir = null, string name = null, bool quiet = false)
        {
            dir ??= _curRoute?.Path; name ??= _curRoute?.Name;
            if (string.IsNullOrEmpty(dir) || companyId == null) return null;
            RouteAuth.Ident id = null;
            try { id = await Task.Run(() => RouteAuth.IdentityOf(dir, name)); } catch { }
            if (id == null)
            {
                var (jm, em) = await Supa.RpcAsync("route_mode", new { });
                return em == null && JsonText(jm) == "enforce" ? Tr("No se ha podido leer el archivo de vías (.tdb) de la ruta: sin él no se puede comprobar que esté autorizada.") : null;
            }
            var (json, err) = await Supa.RpcAsync("route_permit", new { p_company = companyId, p_route_id = id.Id, p_hash = id.Hash, p_name = id.Name, p_stats = (object)null });
            if (err != null) return null;   // servidor sin rutas-autorizadas.sql o sin red: decide start_service
            string mode = "collect", state = "none", reason = ""; bool ok = false, needPlan = false, canReq = false;
            try
            {
                using var d = JsonDocument.Parse(json);
                var r = d.RootElement;
                mode = Str(r, "mode"); state = Str(r, "state"); reason = Str(r, "reason");
                ok = r.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True;
                needPlan = r.TryGetProperty("need_plan", out var n) && n.ValueKind == JsonValueKind.True;
                canReq = r.TryGetProperty("can_request", out var c) && c.ValueKind == JsonValueKind.True;
            }
            catch { return null; }
            if (needPlan) _ = UploadRoutePlanAsync(id);
            if (ok || mode == "collect") return null;
            string msg = RouteBlockText(id.Name, state, reason, canReq);
            if (mode == "warn")
            {
                if (!quiet && _routeWarned.Add(id.Id + "|" + id.Hash))
                    ThemedBox.Show(this, msg + "\n\n" + Tr("De momento el servicio se registra igual, marcado como «ruta sin autorizar»."),
                        "SelectOR", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return null;
            }
            return msg;
        }

        string RouteBlockText(string name, string state, string reason, bool canRequest)
        {
            string t = state switch
            {
                "pending" => string.Format(Tr("La ruta «{0}» está pendiente de autorización: todavía no vale para servicios de empresa."), name),
                "version_pending" => string.Format(Tr("Tu versión de la ruta «{0}» no es la autorizada y está pendiente de revisión."), name),
                "version_rejected" => string.Format(Tr("Tu versión de la ruta «{0}» no está aprobada para servicios de empresa."), name),
                "rejected" => string.Format(Tr("La ruta «{0}» no está autorizada para servicios de empresa."), name),
                "revoked" => string.Format(Tr("La ruta «{0}» ya no está autorizada para servicios de empresa."), name),
                _ => string.Format(Tr("La ruta «{0}» no está autorizada para servicios de empresa."), name),
            };
            if (!string.IsNullOrWhiteSpace(reason)) t += "\n" + Tr("Motivo: ") + reason;
            if (state is "none" or "rejected" or "revoked" or "version_rejected")
                t += "\n" + (canRequest ? Tr("Puedes solicitarla en Empresas → Rutas.") : Tr("Puedes proponérsela a tu empresa en Empresas → Rutas."));
            return t;
        }

        // Ficha y plano de la ruta (una vez por versión y sesión).
        async Task<(Dictionary<string, object> stats, RoutePlan plan)> BuildRoutePlanAsync(string dir)
        {
            try
            {
                var g = await RouteGraphFor(dir);
                if (g == null) return (null, null);
                if (!_hudDetailCache.TryGetValue(dir, out var det))
                {
                    det = await Task.Run(() => HudMapDetail.From(g));
                    if (det != null) _hudDetailCache[dir] = det;
                }
                return await Task.Run(() => RouteAuth.BuildPlan(g, det));
            }
            catch { return (null, null); }
        }

        async Task UploadRoutePlanAsync(RouteAuth.Ident id)
        {
            if (!_routePlanSent.Add(id.Id + "|" + id.Hash)) return;
            var (stats, plan) = await BuildRoutePlanAsync(id.Dir);
            if (plan == null || plan.Empty) return;
            string packed = await Task.Run(() => RouteAuth.Pack(plan));
            var (_, err) = await Supa.RpcAsync("route_upload_plan", new
            {
                p_route_id = id.Id, p_hash = id.Hash, p_plan = packed,
                p_stats = JsonDocument.Parse(RouteAuth.StatsJson(stats)).RootElement
            });
            if (err != null) _routePlanSent.Remove(id.Id + "|" + id.Hash);
        }

        // ============================ estado de las rutas instaladas ============================
        // RouteID + huella de las rutas de este equipo (en segundo plano) y su estado (si el modo no es «recopilar»).
        readonly Dictionary<string, RouteAuth.Ident> _localRouteIds = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, (string state, string reason)> _localRouteState = new(StringComparer.OrdinalIgnoreCase);   // por carpeta
        string _routeMode = "collect";
        bool _routeStatesAsked;
        int _localRouteSeq;

        // forList: para la sección «Rutas» (siempre). Si no (los distintivos al entrar), en «solo recopilar» no hace falta
        // nada y ni se calculan las huellas (las ya calculadas se guardan en memoria: repetirlo apenas cuesta).
        async Task RefreshLocalRouteStatesAsync(bool forList = false)
        {
            if (!Supa.IsLoggedIn) return;
            int seq = ++_localRouteSeq;
            _routeStatesAt = DateTime.UtcNow;
            var (jm, em) = await Supa.RpcAsync("route_mode", new { });
            if (em != null || seq != _localRouteSeq) return;   // servidor sin rutas-autorizadas.sql
            string mode = JsonText(jm);
            if (mode != _routeMode) { _routeMode = mode; UpdateSubtabVisibility(); }   // «Rutas» no se ve en «solo recopilar»
            if (mode == "collect" && !forList) { _lstRoutes?.Invalidate(); return; }   // sin distintivos
            var routes = _routesAll.ToList();
            var ids = await Task.Run(() =>
            {
                var l = new List<RouteAuth.Ident>();
                foreach (var r in routes) { var id = RouteAuth.IdentityOf(r.Path, r.Name); if (id != null) l.Add(id); }
                return l;
            });
            if (seq != _localRouteSeq) return;
            lock (_localRouteIds) { _localRouteIds.Clear(); foreach (var i in ids) _localRouteIds[i.Dir] = i; }
            // una misma ruta puede estar instalada en varias carpetas: se pregunta una vez por versión
            var items = ids.GroupBy(i => i.Id + "|" + i.Hash).Select(gr => new { id = gr.First().Id, hash = gr.First().Hash }).ToArray();
            var (json, err) = await Supa.RpcAsync("route_status_many", new { p_items = items });
            if (err != null || seq != _localRouteSeq) return;
            _localRouteState.Clear();
            try
            {
                using var d = JsonDocument.Parse(json);
                var byKey = ids.ToLookup(i => i.Id + "|" + i.Hash, i => i.Dir);
                foreach (var e in d.RootElement.EnumerateArray())
                    foreach (var dir in byKey[Str(e, "route_id") + "|" + Str(e, "hash")])
                        _localRouteState[dir] = (Str(e, "state"), Str(e, "reason"));
            }
            catch { }
            _lstRoutes?.Invalidate();
        }

        // Los distintivos de las tarjetas, al día: tras una solicitud, una decisión, un cambio de modo o un aviso de
        // rutas, al momento (force); al volver a una pestaña con la lista de rutas, como mucho cada 20 s (el modo o
        // una decisión pueden cambiar en otro equipo).
        DateTime _routeStatesAt;
        void RefreshRouteBadges(bool force = false)
        {
            if (!Supa.IsLoggedIn) return;   // con la sesión iniciada (los distintivos solo salen con sesión)
            if (!force && (DateTime.UtcNow - _routeStatesAt).TotalSeconds < 20) return;
            _ = RefreshLocalRouteStatesAsync();
        }

        // Ha llegado un aviso de rutas (solicitud, decisión, propuesta…): distintivos, sección Rutas y catálogo al día.
        void OnRouteNotif()
        {
            RefreshRouteBadges(force: true);
            if (_coRoutesPanel?.Visible == true) LoadCoRoutes();
            if (Supa.IsSuperadmin) LoadCatalog(onlyCount: _catPanel?.Visible != true);
        }

        // Distintivo de la tarjeta de la ruta (solo con sesión y en modo «Aviso» u «Obligatorio»).
        (string text, Color color)? RouteCardBadge(ORTS.Menu.Route r)
        {
            if (r == null || !Supa.IsLoggedIn || _routeMode == "collect") return null;
            if (!_localRouteState.TryGetValue(r.Path, out var s)) return null;
            return s.state switch
            {
                "authorized" => ("✓ " + Tr("Autorizada"), RouteAuth.StateColor("authorized")),
                "pending" or "version_pending" => (Tr("Pendiente"), RouteAuth.StateColor("pending")),
                _ => (Tr("Sin autorizar"), RouteAuth.StateColor("rejected")),
            };
        }

        // ============================ Empresas → Rutas ============================
        sealed class CoRoute
        {
            public string Id, Name, State = "none", Reason = "", Kind = "", Note = "", By = "", At = "", Hash = "";
            public long Services; public bool HasPlan; public RouteAuth.Ident Local;
        }
        Panel _coRoutesPanel;
        CardTable _coRoutesList;
        Label _coRoutesMsg;
        RoundButton _coRouteReqBtn, _coRoutePlanBtn, _coRouteProposeBtn, _coRouteDismissBtn;
        readonly List<CoRoute> _coRoutes = new();
        int _coRoutesFilter, _coRoutesSeq;

        Panel BuildCoRoutesSubpanel()
        {
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, BackColor = Theme.Bg };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var intro = EmpIntro("Las rutas que afectan a la empresa: las que habéis pedido, las que proponen o usan sus maquinistas y las instaladas en este equipo. Para los servicios de empresa solo valen las rutas autorizadas por el administrador. Un maquinista que tiene una ruta la propone con su plano; el gerente y los gestores la solicitan aunque no la tengan instalada.");
            intro.MaximumSize = new Size(900, 0);
            t.Controls.Add(intro);

            var top = new TableLayoutPanel { Anchor = AnchorStyles.Left | AnchorStyles.Right, Height = 42, ColumnCount = 2, RowCount = 1, BackColor = Theme.Bg, Margin = new Padding(0, 0, 0, 6) };
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            top.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var tabs = MakeSubTabs(new[] { "Todas", "Autorizadas", "Pendientes", "Sin autorizar" }, i => { _coRoutesFilter = i; _coRoutesList?.Refilter(); });
            tabs.Dock = DockStyle.Fill; tabs.Margin = new Padding(0);
            _coRoutesList = EmpCards();
            _coRoutesList.TitleCol = 0; _coRoutesList.PillCol = 1; _coRoutesList.SubCols = new[] { 2, 3 }; _coRoutesList.QuoteCol = 4; _coRoutesList.RightCol = 5;
            _coRoutesList.Icon = "🚉"; _coRoutesList.CardHeight = 86; _coRoutesList.MinWidth = 520; _coRoutesList.Columns = 2;
            _coRoutesList.RowFilter = cells => _coRoutesFilter switch
            {
                1 => cells[6] == "authorized",
                2 => cells[6] is "pending" or "version_pending" or "proposal",
                3 => !(cells[6] is "authorized" or "pending" or "version_pending" or "proposal"),
                _ => true,
            };
            _coRoutesList.SelectedIndexChanged += (s, e) => UpdateCoRouteButtons();
            var search = EmpSearch(_coRoutesList, 240); search.Anchor = AnchorStyles.Right; search.Margin = new Padding(10, 3, 0, 3);
            top.Controls.Add(tabs, 0, 0); top.Controls.Add(search, 1, 0);
            t.Controls.Add(top);
            t.Controls.Add(_coRoutesList);

            var btns = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Theme.Bg, Margin = new Padding(0) };
            _coRouteReqBtn = EmpButton(Tr("Solicitar autorización"), primary: true); _coRouteReqBtn.Width = 230;
            _coRouteReqBtn.Click += (s, e) => RequestSelectedRoute();
            _coRouteProposeBtn = EmpButton(Tr("Proponer a la empresa"), primary: true); _coRouteProposeBtn.Width = 230;
            _coRouteProposeBtn.Click += (s, e) => ProposeSelectedRoute();
            _coRouteDismissBtn = EmpButton(Tr("Descartar propuesta")); _coRouteDismissBtn.Width = 200; _coRouteDismissBtn.Margin = new Padding(8, 10, 2, 2);
            _coRouteDismissBtn.Click += (s, e) => DismissSelectedProposal();
            _coRoutePlanBtn = EmpButton(Tr("Ver plano")); _coRoutePlanBtn.Width = 150; _coRoutePlanBtn.Margin = new Padding(8, 10, 2, 2);
            _coRoutePlanBtn.Click += (s, e) => ShowLocalRoutePlan();
            _coRoutesMsg = EmpMsg(); _coRoutesMsg.Margin = new Padding(12, 20, 2, 2); _coRoutesMsg.MaximumSize = new Size(520, 0);
            btns.Controls.AddRange(new Control[] { _coRouteReqBtn, _coRouteProposeBtn, _coRouteDismissBtn, _coRoutePlanBtn, _coRoutesMsg });
            t.Controls.Add(btns);
            _coRoutesPanel = t;
            return t;
        }

        CoRoute SelectedCoRoute()
        {
            int i = _coRoutesList?.SelectedRow ?? -1;
            return i >= 0 && i < _coRoutes.Count ? _coRoutes[i] : null;
        }

        void UpdateCoRouteButtons()
        {
            var r = SelectedCoRoute();
            bool manage = CanManage() || Supa.IsSuperadmin;
            // una versión pendiente también se puede solicitar: el administrador recibe el aviso de versión nueva
            bool open = r != null && r.State is not ("authorized" or "pending");
            if (_coRouteReqBtn != null)
            {
                // el gerente y los gestores: con la ruta instalada o con la versión que ya conoce el servidor (una propuesta)
                _coRouteReqBtn.Visible = manage;
                _coRouteReqBtn.Enabled = open && (r.Local != null || (r.Hash.Length == 64 && r.HasPlan));
            }
            if (_coRouteProposeBtn != null)
            {
                // el maquinista: con la ruta instalada, si la empresa aún no la ha pedido ni propuesto
                _coRouteProposeBtn.Visible = !manage;
                _coRouteProposeBtn.Enabled = open && r.Local != null && r.Kind is not ("request" or "proposal");
            }
            if (_coRouteDismissBtn != null) { _coRouteDismissBtn.Visible = manage; _coRouteDismissBtn.Enabled = r?.Kind == "proposal"; }
            if (_coRoutePlanBtn != null) _coRoutePlanBtn.Enabled = r != null && (r.Local != null || r.HasPlan);
        }

        async void LoadCoRoutes()
        {
            var c = _empSel;
            if (c == null || _coRoutesList == null) return;
            int seq = ++_coRoutesSeq;
            if (_coRoutes.Count == 0) _coRoutesList.ShowLoading(Tr("Cargando…"));
            var (json, err) = await Supa.RpcAsync("route_company_list", new { p_company = c.Id });
            if (seq != _coRoutesSeq || _empSel?.Id != c.Id) return;
            if (err != null)
            {
                _coRoutes.Clear();
                _coRoutesList.SetEmpty(err.Contains("PGRST202") || err.Contains("Could not find") ? Tr("El servidor aún no tiene las rutas autorizadas (falta rutas-autorizadas.sql).") : Tr("Error: ") + err);
                UpdateCoRouteButtons();
                return;
            }
            var list = new List<CoRoute>();
            try
            {
                using var d = JsonDocument.Parse(json);
                foreach (var e in d.RootElement.EnumerateArray())
                    list.Add(new CoRoute
                    {
                        Id = Str(e, "route_id"), Name = Str(e, "name"), State = Str(e, "state"), Reason = Str(e, "reason"),
                        Kind = Str(e, "kind"), Note = Str(e, "note"), By = Str(e, "requested_by"), At = FmtDate(Str(e, "requested_at")),
                        Hash = Str(e, "hash"), Services = (long)Num(e, "services"),
                        HasPlan = e.TryGetProperty("has_plan", out var hp) && hp.ValueKind == JsonValueKind.True
                    });
            }
            catch { }
            // las rutas de este equipo (con su estado para ESTA versión)
            await RefreshLocalRouteStatesAsync(forList: true);
            if (seq != _coRoutesSeq || _empSel?.Id != c.Id) return;
            List<RouteAuth.Ident> locals; lock (_localRouteIds) locals = _localRouteIds.Values.ToList();
            foreach (var li in locals)
            {
                var row = list.Find(x => string.Equals(x.Id, li.Id, StringComparison.OrdinalIgnoreCase));
                if (row == null) { row = new CoRoute { Id = li.Id, Name = li.Name }; list.Add(row); }
                if (row.Local == null) row.Local = li;
                // la versión de este equipo manda (salvo una propuesta de esa misma versión, que se sigue viendo como tal)
                if (_localRouteState.TryGetValue(li.Dir, out var st) && !(row.State == "proposal" && st.state != "authorized" && row.Hash == li.Hash))
                { row.State = st.state; if (st.reason.Length > 0) row.Reason = st.reason; }
            }
            _coRoutes.Clear(); _coRoutes.AddRange(list.OrderBy(x => x.State == "proposal" ? 0 : x.State == "authorized" ? 2 : 1).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase));
            FillCoRoutes();
        }

        void FillCoRoutes()
        {
            _coRoutesList.ClearRows();
            foreach (var r in _coRoutes)
            {
                string who = r.Kind == "request" ? string.Format(Tr("Solicitada por {0}"), r.By.Length > 0 ? r.By : "—") + (r.At.Length > 0 ? " · " + r.At : "")
                           : r.Kind == "proposal" ? string.Format(Tr("Propuesta por {0}"), r.By.Length > 0 ? r.By : "—") + (r.At.Length > 0 ? " · " + r.At : "")
                           : r.Kind == "auto" ? Tr("Usada por la empresa") : Tr("Instalada en este equipo");
                string where = r.Local != null ? Tr("Instalada") : Tr("No instalada en este equipo");
                string quote = !string.IsNullOrWhiteSpace(r.Reason) && r.State != "authorized" ? Tr("Motivo: ") + r.Reason : r.Note;
                string svc = r.Services > 0 ? string.Format(r.Services == 1 ? Tr("{0} servicio") : Tr("{0} servicios"), r.Services.ToString("N0", EsEs)) : "";
                _coRoutesList.AddRow(new[] { r.Name.Length > 0 ? r.Name : r.Id, RouteAuth.StateText(r.State), who, where, quote ?? "", svc, r.State },
                    new Color?[] { null, RouteAuth.StateColor(r.State), Theme.Subtle, r.Local != null ? Theme.AccentHi : Theme.Subtle, null, Theme.Subtle, null });
            }
            if (_coRoutes.Count == 0) _coRoutesList.SetEmpty(Tr("Todavía no hay rutas: aparecen al solicitarlas o al usarlas en un servicio."));
            UpdateCoRouteButtons();
        }

        async void RequestSelectedRoute()
        {
            var r = SelectedCoRoute(); var c = _empSel;
            if (r == null || c == null || !(CanManage() || Supa.IsSuperadmin)) return;
            // sin la ruta en este equipo: la versión que ya conoce el servidor (la que propuso un maquinista, con su plano)
            var li = r.Local ?? (r.Hash.Length == 64 ? new RouteAuth.Ident { Id = r.Id, Name = r.Name, Hash = r.Hash } : null);
            if (li == null) return;
            using var dlg = new FormDialog(Tr("Solicitar autorización de ruta"), Tr("Solicitar"), 600);
            dlg.AddInfo(string.Format(Tr("Ruta: {0}"), li.Name) + "\n" + string.Format(Tr("RouteID: {0} · versión {1}"), li.Id, li.Hash.Substring(0, 8)));
            if (r.Kind == "proposal") dlg.AddInfo(string.Format(Tr("Propuesta por {0}"), r.By) + (r.Note.Length > 0 ? ": «" + r.Note + "»" : ""));
            if (r.State is "version_pending" or "version_rejected")
                dlg.AddInfo(Tr("Es una versión distinta de la autorizada (su archivo de vías ha cambiado): el administrador tiene que aprobarla."));
            dlg.AddMultiline("note", Tr("Comentario para el administrador (opcional)"), "", 110);
            dlg.AddInfo(r.Local != null ? Tr("Con la solicitud se envían la ficha y el plano de la ruta, para que se pueda revisar aunque no se tenga instalada.")
                                        : Tr("No tienes esta ruta instalada: se solicita la versión que propuso el maquinista, con su ficha y su plano."));
            string note = null;
            dlg.Validate = async f => { note = f.Get("note"); if (note.Length > 500) return Tr("El comentario es demasiado largo (500 caracteres como mucho)."); await Task.CompletedTask; return null; };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            Msg(_coRoutesMsg, li.Dir != null ? Tr("Preparando la ficha y el plano de la ruta…") : Tr("Enviando la solicitud…"), false);
            var (stats, plan) = li.Dir != null ? await BuildRoutePlanAsync(li.Dir) : (null, null);
            var statsEl = stats != null ? JsonDocument.Parse(RouteAuth.StatsJson(stats)).RootElement : (JsonElement?)null;
            var (json, err) = await Supa.RpcAsync("route_request", new { p_company = c.Id, p_route_id = li.Id, p_hash = li.Hash, p_name = li.Name, p_stats = statsEl, p_note = note });
            if (err != null) { Msg(_coRoutesMsg, Tr("Error: ") + err, true); return; }
            if (plan != null && !plan.Empty)
            {
                string packed = await Task.Run(() => RouteAuth.Pack(plan));
                var (_, e2) = await Supa.RpcAsync("route_upload_plan", new { p_route_id = li.Id, p_hash = li.Hash, p_plan = packed, p_stats = statsEl });
                if (e2 == null) _routePlanSent.Add(li.Id + "|" + li.Hash);
            }
            Msg(_coRoutesMsg, string.Format(Tr("Solicitud enviada: «{0}» queda pendiente de autorización."), li.Name), false);
            LoadCoRoutes();
        }

        async void ShowLocalRoutePlan()
        {
            var r = SelectedCoRoute(); var c = _empSel;
            if (r == null || c == null) return;
            Msg(_coRoutesMsg, Tr("Preparando el plano…"), false);
            RoutePlan plan = null;
            if (r.Local != null) (_, plan) = await BuildRoutePlanAsync(r.Local.Dir);
            else if (r.HasPlan)
            {
                var (json, err) = await Supa.RpcAsync("route_company_plan", new { p_company = c.Id, p_route_id = r.Id, p_hash = r.Hash });
                if (err != null) { Msg(_coRoutesMsg, Tr("Error: ") + err, true); return; }
                string b64 = JsonText(json);
                plan = await Task.Run(() => RouteAuth.Unpack(b64));
            }
            Msg(_coRoutesMsg, "", false);
            ShowPlanWindow(r.Name.Length > 0 ? r.Name : r.Id, plan);
        }

        // El maquinista propone a su empresa una ruta que tiene instalada: se envían su ficha y su plano, y el gerente y
        // los gestores reciben un aviso para solicitarla (aunque ellos no la tengan) o descartarla.
        async void ProposeSelectedRoute()
        {
            var r = SelectedCoRoute(); var c = _empSel;
            if (r?.Local == null || c == null) return;
            var li = r.Local;
            using var dlg = new FormDialog(Tr("Proponer la ruta a la empresa"), Tr("Proponer"), 600);
            dlg.AddInfo(string.Format(Tr("Ruta: {0}"), li.Name) + "\n" + string.Format(Tr("RouteID: {0} · versión {1}"), li.Id, li.Hash.Substring(0, 8)));
            dlg.AddMultiline("note", Tr("Comentario para el gerente y los gestores (opcional)"), "", 110);
            dlg.AddInfo(Tr("Se envían la ficha y el plano de la ruta: el gerente o un gestor podrán revisarla y solicitarla al administrador aunque no la tengan instalada."));
            string note = null;
            dlg.Validate = async f => { note = f.Get("note"); if (note.Length > 500) return Tr("El comentario es demasiado largo (500 caracteres como mucho)."); await Task.CompletedTask; return null; };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            Msg(_coRoutesMsg, Tr("Preparando la ficha y el plano de la ruta…"), false);
            var (stats, plan) = await BuildRoutePlanAsync(li.Dir);
            var statsEl = stats != null ? JsonDocument.Parse(RouteAuth.StatsJson(stats)).RootElement : (JsonElement?)null;
            var (json, err) = await Supa.RpcAsync("route_propose", new { p_company = c.Id, p_route_id = li.Id, p_hash = li.Hash, p_name = li.Name, p_stats = statsEl, p_note = note });
            if (err != null) { Msg(_coRoutesMsg, Tr("Error: ") + err, true); return; }
            bool already = false, needPlan = true;
            try { using var d = JsonDocument.Parse(json); already = d.RootElement.TryGetProperty("already", out var a) && a.ValueKind == JsonValueKind.True; needPlan = !(d.RootElement.TryGetProperty("need_plan", out var np) && np.ValueKind == JsonValueKind.False); } catch { }
            if (needPlan && plan != null && !plan.Empty)
            {
                string packed = await Task.Run(() => RouteAuth.Pack(plan));
                var (_, e2) = await Supa.RpcAsync("route_upload_plan", new { p_route_id = li.Id, p_hash = li.Hash, p_plan = packed, p_stats = statsEl });
                if (e2 == null) _routePlanSent.Add(li.Id + "|" + li.Hash);
            }
            Msg(_coRoutesMsg, already ? string.Format(Tr("La empresa ya ha solicitado «{0}»."), li.Name)
                                      : string.Format(Tr("Propuesta enviada: el gerente y los gestores ya pueden revisar «{0}»."), li.Name), false);
            LoadCoRoutes();
        }

        async void DismissSelectedProposal()
        {
            var r = SelectedCoRoute(); var c = _empSel;
            if (r?.Kind != "proposal" || c == null || !(CanManage() || Supa.IsSuperadmin)) return;
            string reason = null;
            using (var dlg = new FormDialog(Tr("Descartar propuesta"), Tr("Descartar"), 560))
            {
                dlg.AddInfo(string.Format(Tr("Ruta: {0}"), r.Name) + "\n" + string.Format(Tr("Propuesta por {0}"), r.By));
                dlg.AddText("reason", Tr("Motivo (lo verá quien la propuso, opcional)"), "", Tr("Por ejemplo: ya tenemos una ruta parecida"));
                dlg.Validate = async f => { reason = f.Get("reason"); await Task.CompletedTask; return reason.Length > 300 ? Tr("El motivo es demasiado largo (300 caracteres como mucho).") : null; };
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
            }
            var (_, err) = await Supa.RpcAsync("route_dismiss_proposal", new { p_company = c.Id, p_route_id = r.Id, p_reason = reason });
            if (err != null) { Msg(_coRoutesMsg, Tr("Error: ") + err, true); return; }
            Msg(_coRoutesMsg, string.Format(Tr("Propuesta de «{0}» descartada."), r.Name), false);
            LoadCoRoutes();
        }

        void ShowPlanWindow(string name, RoutePlan plan)
        {
            using var f = new Form
            {
                Text = Tr("Plano de la ruta") + " · " + name, BackColor = Theme.Bg, ForeColor = Theme.Text, StartPosition = FormStartPosition.CenterParent,
                MinimizeBox = false, KeyPreview = true, Padding = new Padding(10)
            };
            var wa = Screen.FromControl(this).WorkingArea;
            f.ClientSize = new Size(Math.Min(1300, wa.Width - 80), Math.Min(860, wa.Height - 80));
            try { f.Icon = Icon; } catch { }
            var v = new RoutePlanView { Dock = DockStyle.Fill, Expandable = false };
            v.SetPlan(plan, Tr("No se ha podido leer el plano de esta ruta."));
            f.Controls.Add(v);
            f.KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) f.Close(); };
            f.Shown += (s, e) => v.Focus();
            f.ShowDialog(this);
        }

        // ============================ Empresas → Catálogo de rutas (superadmin) ============================
        sealed class CatRoute { public string Id, Name, Status, Reason, State, Mode, VerReqCo = "", VerReqAt = ""; public bool Requested; public long Companies, Services, Versions, Pending; public string Last; public JsonElement Stats; }
        sealed class CatVersion { public string Hash, Name, Status, Reason, FirstSeen, ReqBy = "", ReqCo = "", ReqAt = "", ReqNote = ""; public bool HasPlan; public long Services, PlanSize; public JsonElement Stats; }
        Panel _catPanel, _catDetailHost, _catDetailEmpty;
        CardTable _catList;
        FlowLayoutPanel _catModeBar, _catBtns;
        RouteAdminInfo _catInfo;
        RouteAdminCompanies _catCompanies;
        RoutePlanView _catPlan;
        ThemeCombo _catVersion;
        Label _catMsg, _catVerLbl;
        RoundButton _catAuthBtn, _catRejectBtn, _catRevokeBtn, _catDeleteBtn;
        readonly List<CatRoute> _catRows = new();
        readonly List<CatVersion> _catVersions = new();
        readonly Dictionary<string, RoutePlan> _catPlans = new();
        CatRoute _catSel;
        int _catFilter, _catSeq, _catDetSeq;
        string _catDetailStatus = "";

        Panel BuildCatalogSubpanel()
        {
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Theme.Bg };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            // cabecera: explicación y modo
            var head = new TableLayoutPanel { Anchor = AnchorStyles.Left | AnchorStyles.Right, AutoSize = true, ColumnCount = 2, RowCount = 1, BackColor = Theme.Bg, Margin = new Padding(0, 0, 0, 6) };
            head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var intro = EmpIntro("Catálogo global de rutas para los servicios de empresa. Llegan al solicitarlas una empresa o al usarlas un maquinista, con su ficha y su plano. «Solo recopilar»: nadie nota nada; «Aviso»: se avisa y el servicio queda marcado; «Obligatorio»: sin ruta autorizada no hay servicio.");
            intro.MaximumSize = new Size(760, 0);
            head.Controls.Add(intro, 0, 0);
            var modeBox = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Theme.Bg, Margin = new Padding(10, 0, 0, 0), Anchor = AnchorStyles.Right | AnchorStyles.Top };
            modeBox.Controls.Add(new Label { Text = Tr("MODO"), AutoSize = true, ForeColor = Theme.Subtle, Font = Theme.Font(8f, FontStyle.Bold), Margin = new Padding(0, 12, 8, 0) });
            _catModeBar = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Theme.Bg, Margin = new Padding(0) };
            string[] modes = { "collect", "warn", "enforce" };
            for (int k = 0; k < modes.Length; k++)
            {
                string m = modes[k];
                var b = BankChip(RouteAuth.ModeText(m), k == 0);
                b.Click += (s, e) => SetRouteMode(m);
                _catModeBar.Controls.Add(b);
            }
            modeBox.Controls.Add(_catModeBar);
            head.Controls.Add(modeBox, 1, 0);
            t.Controls.Add(head);

            // cuerpo: lista a la izquierda, ficha a la derecha
            var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Theme.Bg, Margin = new Padding(0) };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.Px(440))); body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Theme.Bg, Margin = new Padding(0, 0, 10, 0) };
            left.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            left.RowStyles.Add(new RowStyle(SizeType.AutoSize)); left.RowStyles.Add(new RowStyle(SizeType.AutoSize)); left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var tabs = MakeSubTabs(new[] { "Pendientes", "Autorizadas", "Rechazadas", "Todas" }, i => { _catFilter = i; _catList?.Refilter(); });
            tabs.Dock = DockStyle.Fill; tabs.Margin = new Padding(0);
            _catList = EmpCards();
            _catList.TitleCol = 0; _catList.PillCol = 1; _catList.SubCols = new[] { 2, 3 }; _catList.QuoteCol = 4;
            _catList.Icon = "🚉"; _catList.QuoteMarks = false; _catList.CardHeight = 84; _catList.MinWidth = 300; _catList.Columns = 1;
            _catList.RowFilter = cells => _catFilter switch
            {
                0 => cells[5] is "pending" or "version_pending",
                1 => cells[5] == "authorized",
                2 => cells[5] is "rejected" or "revoked",
                _ => true,
            };
            _catList.SelectedIndexChanged += (s, e) => OnCatSelected();
            var search = EmpSearch(_catList, 360); search.Dock = DockStyle.Fill; search.Margin = new Padding(0, 4, 0, 4);
            left.Controls.Add(tabs, 0, 0); left.Controls.Add(search, 0, 1); left.Controls.Add(_catList, 0, 2);
            body.Controls.Add(left, 0, 0);

            // la ficha, sin desplazarse: arriba sus datos; luego la versión y los botones; y el resto, el plano y las empresas
            _catDetailHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Margin = new Padding(0) };
            var det = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Theme.Bg, Margin = new Padding(0), Padding = new Padding(0, 0, 4, 0) };
            det.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            det.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // nombre, estado, quién, alertas y casillas
            det.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // versión · botones
            det.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // plano · empresas
            _catInfo = new RouteAdminInfo { Dock = DockStyle.Top, Margin = new Padding(0) };
            det.Controls.Add(_catInfo, 0, 0);

            // la versión y, debajo, los botones (de izquierda a derecha, empezando por la decisión)
            var bar = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, RowCount = 2, BackColor = Theme.Bg, Margin = new Padding(0, 6, 0, 8) };
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var verRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Theme.Bg, Margin = new Padding(0), Anchor = AnchorStyles.Left };
            _catVerLbl = new Label { Text = Tr("VERSIÓN"), AutoSize = true, ForeColor = Theme.Subtle, Font = Theme.Font(8f, FontStyle.Bold), Margin = new Padding(0, 9, 10, 0) };
            _catVersion = new ThemeCombo { DropDownStyle = ComboBoxStyle.DropDownList, Width = Theme.Px(400), Margin = new Padding(0, 2, 0, 0) };
            StyleCombo(_catVersion);   // sin su DrawItem, la lista desplegada sale en blanco
            _catVersion.SelectedIndexChanged += (s, e) => OnCatVersionChanged();
            verRow.Controls.Add(_catVerLbl); verRow.Controls.Add(_catVersion);
            bar.Controls.Add(verRow, 0, 0);
            _catBtns = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, BackColor = Theme.Bg, Margin = new Padding(0, 8, 0, 0) };
            RoundButton Act(string text, bool primary = false)
            {
                var b = EmpButton(Tr(text), primary); b.Height = 34; b.Margin = new Padding(0, 0, 8, 0);
                using var fb = Theme.Font(9.5f, FontStyle.Bold); b.Width = TextRenderer.MeasureText(b.Text, fb).Width + 36;
                return b;
            }
            _catAuthBtn = Act("Aprobar esta versión", primary: true); _catAuthBtn.Click += (s, e) => CatDecide("authorize");
            _catRejectBtn = Act("Rechazar esta versión"); _catRejectBtn.Click += (s, e) => CatDecide("reject");
            _catRevokeBtn = Act("Retirar autorización"); _catRevokeBtn.TextColor = RedC; _catRevokeBtn.Click += (s, e) => CatDecide("revoke");
            _catDeleteBtn = Act("Quitar del catálogo");
            _catDeleteBtn.BaseColor = Theme.Surface2; _catDeleteBtn.HoverColor = Color.FromArgb(150, 60, 60); _catDeleteBtn.TextColor = RedC; _catDeleteBtn.Click += (s, e) => CatDelete();
            _catBtns.Controls.AddRange(new Control[] { _catAuthBtn, _catRejectBtn, _catRevokeBtn, _catDeleteBtn });
            bar.Controls.Add(_catBtns, 0, 1);
            det.Controls.Add(bar, 0, 1);

            var split = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Theme.Bg, Margin = new Padding(0) };
            split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); split.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.Px(360)));
            split.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _catPlan = new RoutePlanView { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 10, 0) };
            split.Controls.Add(_catPlan, 0, 0);
            var coHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, AutoScroll = true, Margin = new Padding(0) };
            Native.UseDarkScrollBars(coHost);
            _catCompanies = new RouteAdminCompanies { Dock = DockStyle.Top, Margin = new Padding(0) };
            coHost.Controls.Add(_catCompanies);
            split.Controls.Add(coHost, 1, 0);
            det.Controls.Add(split, 0, 2);
            _catDetailHost.Controls.Add(det);
            _catDetailEmpty = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            var emptyLbl = new Label { Dock = DockStyle.Fill, Text = Tr("Elige una ruta de la lista para ver su ficha y su plano."), ForeColor = Theme.Subtle, Font = Theme.Font(10f), TextAlign = ContentAlignment.MiddleCenter };
            _catDetailEmpty.Controls.Add(emptyLbl);
            var right = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Margin = new Padding(0) };
            right.Controls.Add(_catDetailHost); right.Controls.Add(_catDetailEmpty);
            _catDetailHost.Visible = false;
            body.Controls.Add(right, 1, 0);
            t.Controls.Add(body);

            _catMsg = EmpMsg(); _catMsg.MaximumSize = new Size(900, 0);
            t.Controls.Add(_catMsg);
            _catPanel = t;
            return t;
        }

        // Nº de rutas por revisar en el menú: «Catálogo de rutas (n)».
        void SetCatalogCount(int n)
        {
            if (_empSubtabs == null || _empSubtabs.Length <= CatalogoSubtab || _empSubtabs[CatalogoSubtab] == null) return;
            string txt = Tr(SubNames[CatalogoSubtab]) + (n > 0 ? "  (" + n + ")" : "");
            if (_empSubtabs[CatalogoSubtab].Text != txt) { _empSubtabs[CatalogoSubtab].Text = txt; _empSubtabs[CatalogoSubtab].Invalidate(); }
        }

        async void LoadCatalog(bool onlyCount = false, string selectId = null)
        {
            if (!Supa.IsSuperadmin || _catList == null) return;
            int seq = ++_catSeq;
            if (!onlyCount && _catRows.Count == 0) _catList.ShowLoading(Tr("Cargando…"));
            var (json, err) = await Supa.RpcAsync("route_admin_list", new { });
            if (seq != _catSeq) return;
            if (err != null)
            {
                if (onlyCount) return;
                _catRows.Clear();
                _catList.SetEmpty(err.Contains("PGRST202") || err.Contains("Could not find") ? Tr("El servidor aún no tiene las rutas autorizadas (falta rutas-autorizadas.sql).") : Tr("Error: ") + err);
                return;
            }
            var rows = new List<CatRoute>(); string mode = "collect";
            try
            {
                using var d = JsonDocument.Parse(json);
                foreach (var e in d.RootElement.EnumerateArray())
                {
                    var r = new CatRoute
                    {
                        Id = Str(e, "route_id"), Name = Str(e, "name"), Status = Str(e, "status"), Reason = Str(e, "reason"),
                        Requested = e.TryGetProperty("requested", out var rq) && rq.ValueKind == JsonValueKind.True,
                        Companies = (long)Num(e, "companies"), Services = (long)Num(e, "services"), Versions = (long)Num(e, "versions"),
                        Pending = (long)Num(e, "pending_versions"), Last = FmtDate(Str(e, "last_at")),
                        VerReqCo = Str(e, "version_request_company"), VerReqAt = FmtDate(Str(e, "version_request_at")),   // rutas-versiones.sql
                        Stats = e.TryGetProperty("stats", out var st) ? st.Clone() : default
                    };
                    r.State = r.Status == "authorized" && r.Pending > 0 ? "version_pending" : r.Status;
                    mode = Str(e, "mode");
                    rows.Add(r);
                }
            }
            catch { }
            if (rows.Count == 0) { var (jm, _) = await Supa.RpcAsync("route_mode", new { }); mode = JsonText(jm); }
            SetCatalogCount(rows.Count(r => r.State is "pending" or "version_pending"));
            SetModeChips(mode);
            if (onlyCount) return;
            _catRows.Clear(); _catRows.AddRange(rows);
            string keep = selectId ?? _catSel?.Id;
            _catList.ClearRows();
            int row = 0, selRow = -1;
            foreach (var r in rows)
            {
                var alerts = RouteAuth.Alerts(r.Stats);
                // una versión nueva solicitada por una empresa se dice en la tarjeta (con su fecha)
                string sub1 = r.State == "version_pending" && r.VerReqCo.Length > 0
                    ? string.Format(Tr("Versión nueva solicitada por {0}"), r.VerReqCo) + (r.VerReqAt.Length > 0 ? " · " + r.VerReqAt : "")
                    : (r.Requested ? Tr("Solicitada") : Tr("Detectada al conducir")) + (r.Last.Length > 0 ? " · " + r.Last : "");
                string sub2 = string.Format(r.Companies == 1 ? Tr("{0} empresa") : Tr("{0} empresas"), r.Companies) + " · "
                            + string.Format(r.Services == 1 ? Tr("{0} servicio") : Tr("{0} servicios"), r.Services.ToString("N0", EsEs));
                string quote = r.Status is "rejected" or "revoked" && r.Reason.Length > 0 ? Tr("Motivo: ") + r.Reason
                             : alerts.Count > 0 ? "⚠ " + alerts[0].text + (alerts.Count > 1 ? "  (+" + (alerts.Count - 1) + ")" : "") : "";
                _catList.AddRow(new[] { r.Name.Length > 0 ? r.Name : r.Id, RouteAuth.StateText(r.State), sub1, sub2, quote, r.State },
                    new Color?[] { null, RouteAuth.StateColor(r.State), Theme.Subtle, Theme.Subtle, r.Status is "rejected" or "revoked" ? Theme.Subtle : alerts.Any(a => a.severe) ? RouteAuth.StateColor("rejected") : alerts.Count > 0 ? RouteAuth.StateColor("pending") : (Color?)null, null });
                if (r.Id == keep) selRow = row;
                row++;
            }
            if (rows.Count == 0) _catList.SetEmpty(Tr("Todavía no hay rutas: llegan al solicitarlas una empresa o al usarlas un maquinista en un servicio."));
            if (selRow >= 0) _catList.SelectRow(selRow);
            else if (_catSel != null && !rows.Any(r => r.Id == _catSel.Id)) { _catSel = null; ShowCatDetail(false); }
        }

        void SetModeChips(string mode)
        {
            if (_catModeBar == null) return;
            int idx = mode == "warn" ? 1 : mode == "enforce" ? 2 : 0;
            SetChipActive(_catModeBar, idx);
            if (_routeMode != mode) { _routeMode = mode; UpdateSubtabVisibility(); RefreshRouteBadges(force: true); }   // distintivos según el modo
        }

        async void SetRouteMode(string mode)
        {
            if (mode == _routeMode) return;
            string q = mode switch
            {
                "enforce" => Tr("¿Pasar a «Obligatorio»? Desde ahora nadie podrá ponerse de servicio con una ruta (o una versión) que no esté autorizada."),
                "warn" => Tr("¿Pasar a «Aviso»? Quien use una ruta sin autorizar verá un aviso y su servicio quedará marcado."),
                _ => Tr("¿Pasar a «Solo recopilar»? Las rutas se seguirán recogiendo, pero nadie notará nada."),
            };
            if (ThemedBox.Show(this, q, "SelectOR", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) { SetModeChips(_routeMode); return; }
            var (_, err) = await Supa.RpcAsync("route_set_mode", new { p_mode = mode });
            if (err != null) { Msg(_catMsg, Tr("Error: ") + err, true); SetModeChips(_routeMode); return; }
            SetModeChips(mode);
            Msg(_catMsg, string.Format(Tr("Modo de las rutas: {0}."), RouteAuth.ModeText(mode)), false);
        }

        void ShowCatDetail(bool show)
        {
            if (_catDetailHost == null) return;
            _catDetailHost.Visible = show; _catDetailEmpty.Visible = !show;
        }

        async void OnCatSelected()
        {
            int i = _catList?.SelectedRow ?? -1;
            if (i < 0 || i >= _catRows.Count) return;
            var r = _catRows[i];
            _catSel = r;
            int seq = ++_catDetSeq;
            var (json, err) = await Supa.RpcAsync("route_admin_detail", new { p_route_id = r.Id });
            if (seq != _catDetSeq) return;
            if (err != null) { Msg(_catMsg, Tr("Error: ") + err, true); return; }
            ShowCatDetail(true);
            _catVersions.Clear();
            var companies = new List<RouteAdminCompanies.Row>();
            string requestedNote = "", requestedBy = "", requestedCo = "", requestedAt = "", decidedBy = "", decidedAt = "";
            try
            {
                using var d = JsonDocument.Parse(json);
                var root = d.RootElement;
                _catDetailStatus = Str(root, "status");
                decidedBy = Str(root, "decided_by"); decidedAt = FmtDate(Str(root, "decided_at"));
                foreach (var v in root.GetProperty("versions").EnumerateArray())
                    _catVersions.Add(new CatVersion
                    {
                        Hash = Str(v, "hash"), Name = Str(v, "name"), Status = Str(v, "status"), Reason = Str(v, "reason"), FirstSeen = FmtDate(Str(v, "first_seen")),
                        HasPlan = v.TryGetProperty("has_plan", out var hp) && hp.ValueKind == JsonValueKind.True,
                        Services = (long)Num(v, "services"), PlanSize = (long)Num(v, "plan_size"), Stats = v.TryGetProperty("stats", out var st) ? st.Clone() : default,
                        ReqBy = Str(v, "requested_by"), ReqCo = Str(v, "requested_company"), ReqAt = FmtDate(Str(v, "requested_at")), ReqNote = Str(v, "request_note")
                    });
                foreach (var q in root.GetProperty("requests").EnumerateArray())
                {
                    var row = new RouteAdminCompanies.Row
                    {
                        Company = Str(q, "company"), Requested = Str(q, "kind") == "request", Proposed = Str(q, "kind") == "proposal", User = Str(q, "user"), Note = Str(q, "note"),
                        At = FmtDate(Str(q, "at")), Services = (long)Num(q, "services"),
                        SameVersion = true, Hash = Str(q, "hash")
                    };
                    companies.Add(row);
                    if (row.Requested && requestedBy.Length == 0) { requestedBy = row.User; requestedCo = row.Company; requestedNote = row.Note; requestedAt = row.At; }
                }
            }
            catch { }
            // versión que se enseña: la primera pendiente o, si no, la más reciente
            _catVersion.BeginUpdate();
            _catVersion.Items.Clear();
            foreach (var v in _catVersions)
                _catVersion.Items.Add(v.Hash.Substring(0, 4) + "…" + v.Hash.Substring(v.Hash.Length - 4) + "  ·  " + RouteAuth.StateText(v.Status == "pending" && _catDetailStatus == "authorized" ? "version_pending" : v.Status)
                                      + "  ·  " + string.Format(v.Services == 1 ? Tr("{0} servicio") : Tr("{0} servicios"), v.Services.ToString("N0", EsEs))
                                      + (v.FirstSeen.Length > 0 ? "  ·  " + string.Format(Tr("desde {0}"), v.FirstSeen) : "")
                                      + (v.Status == "pending" && v.ReqCo.Length > 0 ? "  ·  " + string.Format(Tr("solicitada por {0}"), v.ReqCo) : ""));
            int pick = _catVersions.FindIndex(v => v.Status == "pending"); if (pick < 0) pick = 0;
            _catVersion.EndUpdate();
            _catVerLbl.Text = _catVersions.Count > 1 ? string.Format(Tr("VERSIONES ({0})"), _catVersions.Count) : Tr("VERSIÓN");
            foreach (var c in companies) c.SameVersion = _catVersions.Count <= 1 || string.IsNullOrEmpty(c.Hash);
            _catCompanies.SetRows(companies, _catVersions.Select(v => v.Hash).ToList());
            _catInfoBase = (r, requestedBy, requestedCo, requestedNote, requestedAt, decidedBy, decidedAt);
            if (_catVersions.Count > 0) { if (_catVersion.SelectedIndex == pick) OnCatVersionChanged(); else _catVersion.SelectedIndex = pick; }
            else { _catPlan.SetPlan(null, Tr("Esta ruta no tiene ninguna versión registrada.")); FillCatInfo(null); }
        }

        (CatRoute r, string by, string co, string note, string at, string decidedBy, string decidedAt) _catInfoBase;

        CatVersion SelectedCatVersion() { int i = _catVersion?.SelectedIndex ?? -1; return i >= 0 && i < _catVersions.Count ? _catVersions[i] : null; }

        void FillCatInfo(CatVersion v)
        {
            var (r, by, co, note, at, decidedBy, decidedAt) = _catInfoBase;
            if (r == null) return;
            string state = r.Status == "authorized" && v != null && v.Status != "authorized" ? (v.Status == "rejected" ? "version_rejected" : "version_pending") : r.Status;
            bool verReq = state == "version_pending" && v.ReqCo.Length > 0;   // versión nueva pedida por una empresa
            var data = new RouteAdminInfo.Data
            {
                Name = r.Name.Length > 0 ? r.Name : r.Id, StateText = RouteAuth.StateText(state), StateColor = RouteAuth.StateColor(state),
                Sub = "RouteID " + r.Id + (v != null ? "   ·   " + Tr("huella") + " " + v.Hash.Substring(0, 12) + "…" : ""),
                Who = verReq ? string.Format(Tr("Versión nueva solicitada por {0} ({1}) · {2}"), v.ReqBy.Length > 0 ? v.ReqBy : "—", v.ReqCo, v.ReqAt)
                    : by.Length > 0 ? string.Format(Tr("Solicitada por {0} ({1}) · {2}"), by, co, at) : Tr("Detectada al conducir (nadie la ha solicitado todavía)"),
                Note = verReq ? v.ReqNote : note,
                Decision = r.Status is "authorized" or "rejected" or "revoked" && decidedAt.Length > 0
                    ? string.Format(Tr("Decidido por {0} el {1}"), decidedBy.Length > 0 ? decidedBy : "—", decidedAt) + (r.Reason.Length > 0 ? " · " + Tr("Motivo: ") + r.Reason : "")
                    : (v != null && v.Reason.Length > 0 ? Tr("Motivo: ") + v.Reason : ""),
            };
            var stats = v != null && v.Stats.ValueKind == JsonValueKind.Object ? v.Stats : r.Stats;
            data.Alerts.AddRange(RouteAuth.Alerts(stats));
            double N(string k) => stats.ValueKind == JsonValueKind.Object && stats.TryGetProperty(k, out var x) && x.ValueKind == JsonValueKind.Number ? x.GetDouble() : double.NaN;
            string F(double x, string fmt = "N0") => double.IsNaN(x) ? "—" : x.ToString(fmt, EsEs);
            data.Tiles.Add((Tr("KM DE VÍA"), F(N("km"), "N1"), false));
            data.Tiles.Add((Tr("ESTACIONES"), F(N("stations")), N("stations") == 0));
            data.Tiles.Add((Tr("LÍMITES VEL."), F(N("limits")), N("limits") == 0));
            data.Tiles.Add((Tr("ANDENES"), F(N("platforms")), false));
            data.Tiles.Add((Tr("TOPERAS"), F(N("ends")), N("ends") == 0));
            data.Tiles.Add((Tr("EXTENSIÓN"), double.IsNaN(N("span_km")) ? "—" : F(N("span_km"), "N1") + " km", false));
            _catInfo.SetData(data);

            // botones según el estado
            bool authorized = r.Status == "authorized";
            _catAuthBtn.Text = authorized ? Tr("Aprobar esta versión") : Tr("Autorizar ruta");
            _catAuthBtn.Visible = v != null && (!authorized || v.Status != "authorized");
            _catRejectBtn.Text = authorized ? Tr("Rechazar esta versión") : Tr("Rechazar con motivo");
            _catRejectBtn.Visible = v != null && (authorized ? v.Status == "pending" : r.Status != "rejected");
            _catRevokeBtn.Visible = authorized;
            using var fb = Theme.Font(9.5f, FontStyle.Bold);   // a la medida de su texto (cambia según el estado)
            foreach (var b in new[] { _catAuthBtn, _catRejectBtn }) { b.Width = TextRenderer.MeasureText(b.Text, fb).Width + 36; b.Invalidate(); }
        }

        async void OnCatVersionChanged()
        {
            var v = SelectedCatVersion();
            FillCatInfo(v);
            if (v == null) return;
            if (!v.HasPlan) { _catPlan.SetPlan(null, Tr("Todavía no ha llegado el plano de esta versión: se sube la primera vez que alguien la usa o la solicita con SelectOR.")); return; }
            if (_catPlans.TryGetValue(v.Hash, out var hit)) { ShowCatPlan(hit); return; }
            _catPlan.SetPlan(null, Tr("Cargando el plano…"));
            int seq = _catDetSeq; string hash = v.Hash, rid = _catSel?.Id;
            var (json, err) = await Supa.RpcAsync("route_admin_plan", new { p_route_id = rid, p_hash = hash });
            if (seq != _catDetSeq || SelectedCatVersion()?.Hash != hash) return;
            if (err != null) { _catPlan.SetPlan(null, Tr("Error: ") + err); return; }
            string b64 = JsonText(json);
            var plan = await Task.Run(() => RouteAuth.Unpack(b64));
            if (SelectedCatVersion()?.Hash != hash) return;
            if (plan != null) _catPlans[hash] = plan;
            ShowCatPlan(plan);
        }

        void ShowCatPlan(RoutePlan plan)
        {
            _catPlan.WindowTitle = Tr("Plano de la ruta") + " · " + (_catSel?.Name ?? "");
            _catPlan.SetPlan(plan, Tr("No se ha podido leer el plano de esta versión."));
        }

        async void CatDecide(string action)
        {
            var r = _catSel; var v = SelectedCatVersion();
            if (r == null) return;
            string reason = null;
            if (action is "reject" or "revoke")
            {
                using var dlg = new FormDialog(action == "revoke" ? Tr("Retirar la autorización") : Tr("Rechazar"), action == "revoke" ? Tr("Retirar") : Tr("Rechazar"), 560);
                dlg.AddInfo(string.Format(Tr("Ruta: {0}"), r.Name));
                dlg.AddText("reason", Tr("Motivo (lo verán las empresas)"), "", Tr("Por ejemplo: sin límites de velocidad"));
                dlg.Validate = async f => { reason = f.Get("reason"); await Task.CompletedTask; return reason.Length < 3 ? Tr("Indica el motivo.") : reason.Length > 300 ? Tr("El motivo es demasiado largo (300 caracteres como mucho).") : null; };
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
            }
            var (_, err) = await Supa.RpcAsync("route_admin_decide", new { p_route_id = r.Id, p_hash = v?.Hash, p_action = action, p_reason = reason });
            if (err != null) { Msg(_catMsg, Tr("Error: ") + err, true); return; }
            Msg(_catMsg, action switch
            {
                "authorize" => string.Format(r.Status == "authorized" ? Tr("Versión aprobada de «{0}».") : Tr("Ruta «{0}» autorizada."), r.Name),
                "reject" => string.Format(Tr("«{0}» rechazada."), r.Name),
                _ => string.Format(Tr("Autorización de «{0}» retirada."), r.Name),
            }, false);
            LoadCatalog(selectId: r.Id);
            OnCatSelected();
            RefreshRouteBadges(force: true);   // los distintivos de las rutas de este equipo
        }

        async void CatDelete()
        {
            var r = _catSel; if (r == null) return;
            if (ThemedBox.Show(this, string.Format(Tr("¿Quitar «{0}» del catálogo, con sus versiones y solicitudes? Si alguien la vuelve a usar o a pedir, volverá a aparecer."), r.Name),
                    "SelectOR", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            var (_, err) = await Supa.RpcAsync("route_admin_delete", new { p_route_id = r.Id });
            if (err != null) { Msg(_catMsg, Tr("Error: ") + err, true); return; }
            Msg(_catMsg, string.Format(Tr("«{0}» quitada del catálogo."), r.Name), false);
            _catSel = null; ShowCatDetail(false);
            LoadCatalog();
            RefreshRouteBadges(force: true);
        }
    }

    // Cabecera de la ficha de una ruta en el catálogo: nombre y estado, quién la pidió, alertas y datos.
    public sealed class RouteAdminInfo : Control
    {
        public sealed class Data
        {
            public string Name = "", StateText = "", Sub = "", Who = "", Note = "", Decision = "";
            public Color StateColor;
            public readonly List<(bool severe, string text)> Alerts = new();
            public readonly List<(string label, string value, bool warn)> Tiles = new();
        }
        Data _d = new Data();
        readonly Font _fName = Theme.Font(15f, FontStyle.Bold), _fPill = Theme.Font(8.5f, FontStyle.Bold), _fSub = Theme.Font(8.75f),
                      _fCap = Theme.Font(7.75f, FontStyle.Bold), _fVal = Theme.Font(13f, FontStyle.Bold), _fNote = Theme.Font(9f, FontStyle.Italic), _fAl = Theme.Font(9f);

        public RouteAdminInfo()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg; Height = 200;
        }
        protected override void Dispose(bool disposing) { if (disposing) foreach (var f in new[] { _fName, _fPill, _fSub, _fCap, _fVal, _fNote, _fAl }) f.Dispose(); base.Dispose(disposing); }

        public void SetData(Data d) { _d = d ?? new Data(); Height = Measure(null); Invalidate(); }
        protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); int h = Measure(null); if (h != Height) Height = h; }
        protected override void OnPaint(PaintEventArgs e) { e.Graphics.Clear(BackColor); Measure(e.Graphics); }

        // Mide (g = null) o dibuja; devuelve el alto.
        int Measure(Graphics g)
        {
            int w = Math.Max(200, Width), y = 0;
            if (g != null) { g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit; }
            var nameSz = TextRenderer.MeasureText(_d.Name, _fName, Size.Empty, TextFormatFlags.NoPadding);
            if (g != null)
            {
                TextRenderer.DrawText(g, _d.Name, _fName, new Point(0, y), Theme.Text, TextFormatFlags.NoPadding);
                var ps = TextRenderer.MeasureText(_d.StateText, _fPill, Size.Empty, TextFormatFlags.NoPadding);
                var pr = new Rectangle(Math.Min(nameSz.Width + 12, w - ps.Width - 20), y + (nameSz.Height - ps.Height - 6) / 2, ps.Width + 16, ps.Height + 6);
                using (var path = Theme.Round(pr, pr.Height / 2)) using (var b = new SolidBrush(Color.FromArgb(46, _d.StateColor))) g.FillPath(b, path);
                TextRenderer.DrawText(g, _d.StateText, _fPill, pr, _d.StateColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
            y += nameSz.Height + 6;
            foreach (var line in new[] { _d.Sub, _d.Who, _d.Decision })
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var sz = TextRenderer.MeasureText(line, _fSub, new Size(w, 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
                if (g != null) TextRenderer.DrawText(g, line, _fSub, new Rectangle(0, y, w, sz.Height), Theme.Subtle, TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
                y += sz.Height + 4;
            }
            if (!string.IsNullOrWhiteSpace(_d.Note))
            {
                string t = "«" + _d.Note + "»";
                var sz = TextRenderer.MeasureText(t, _fNote, new Size(w - 20, 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
                var r = new Rectangle(0, y + 2, w, sz.Height + 14);
                if (g != null)
                {
                    using (var path = Theme.Round(r, 8)) using (var b = new SolidBrush(Theme.Surface)) g.FillPath(b, path);
                    TextRenderer.DrawText(g, t, _fNote, new Rectangle(10, r.Y + 7, w - 20, sz.Height), Color.FromArgb(205, 210, 214), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
                }
                y = r.Bottom + 4;
            }
            if (_d.Alerts.Count > 0)
            {
                y += 8;
                if (g != null) TextRenderer.DrawText(g, I18n.T("ALERTAS AUTOMÁTICAS"), _fCap, new Point(0, y), Theme.Subtle, TextFormatFlags.NoPadding);
                y += _fCap.Height + 6;
                foreach (var (severe, text) in _d.Alerts)
                {
                    string t = "⚠  " + text;
                    var sz = TextRenderer.MeasureText(t, _fAl, new Size(w - 20, 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
                    var r = new Rectangle(0, y, w, sz.Height + 12);
                    if (g != null)
                    {
                        Color bg = severe ? Color.FromArgb(58, 29, 29) : Color.FromArgb(58, 47, 18), fg = severe ? Color.FromArgb(243, 161, 161) : Color.FromArgb(243, 204, 107);
                        using (var path = Theme.Round(r, 7)) using (var b = new SolidBrush(bg)) g.FillPath(b, path);
                        TextRenderer.DrawText(g, t, _fAl, new Rectangle(10, r.Y + 6, w - 20, sz.Height), fg, TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
                    }
                    y = r.Bottom + 5;
                }
            }
            if (_d.Tiles.Count > 0)
            {
                y += 8;
                if (g != null) TextRenderer.DrawText(g, I18n.T("FICHA DE LA RUTA"), _fCap, new Point(0, y), Theme.Subtle, TextFormatFlags.NoPadding);
                y += _fCap.Height + 6;
                int cols = w >= 760 ? _d.Tiles.Count : 3, gap = 8;
                int tw = (w - gap * (cols - 1)) / cols, th = _fCap.Height + _fVal.Height + 18;
                for (int i = 0; i < _d.Tiles.Count; i++)
                {
                    int cx = (i % cols) * (tw + gap), cy = y + (i / cols) * (th + gap);
                    if (g == null) continue;
                    var r = new Rectangle(cx, cy, tw, th);
                    using (var path = Theme.Round(r, 8)) using (var b = new SolidBrush(Theme.Surface)) g.FillPath(b, path);
                    var (label, value, warn) = _d.Tiles[i];
                    TextRenderer.DrawText(g, label, _fCap, new Point(cx + 10, cy + 7), Theme.Subtle, TextFormatFlags.NoPadding);
                    TextRenderer.DrawText(g, value, _fVal, new Point(cx + 10, cy + 9 + _fCap.Height), warn ? Color.FromArgb(240, 128, 128) : Theme.Text, TextFormatFlags.NoPadding);
                }
                y += ((_d.Tiles.Count + cols - 1) / cols) * (th + gap) - gap;
            }
            return y + 4;
        }
    }

    // Empresas a las que afecta una ruta (las que la pidieron y las que la usan), con sus servicios.
    public sealed class RouteAdminCompanies : Control
    {
        public sealed class Row { public string Company = "", User = "", Note = "", At = "", Hash = ""; public bool Requested, Proposed, SameVersion; public long Services; }
        List<Row> _rows = new();
        List<string> _versions = new();
        readonly Font _fCap = Theme.Font(7.75f, FontStyle.Bold), _fT = Theme.Font(9.5f, FontStyle.Bold), _fS = Theme.Font(8.5f), _fN = Theme.Font(8.5f, FontStyle.Italic);
        const int RowH = 58;

        public RouteAdminCompanies()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg;
        }
        protected override void Dispose(bool disposing) { if (disposing) foreach (var f in new[] { _fCap, _fT, _fS, _fN }) f.Dispose(); base.Dispose(disposing); }

        public void SetRows(List<Row> rows, List<string> versions)
        {
            _rows = rows ?? new List<Row>(); _versions = versions ?? new List<string>();
            Height = _fCap.Height + 10 + Math.Max(1, _rows.Count) * (RowH + 6);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(BackColor); g.SmoothingMode = SmoothingMode.AntiAlias;
            TextRenderer.DrawText(g, string.Format(I18n.T("EMPRESAS A LAS QUE AFECTA ({0})"), _rows.Count), _fCap, new Point(0, 0), Theme.Subtle, TextFormatFlags.NoPadding);
            int y = _fCap.Height + 10, w = Width;
            if (_rows.Count == 0)
            {
                TextRenderer.DrawText(g, I18n.T("Ninguna."), _fS, new Point(0, y + 4), Theme.Subtle, TextFormatFlags.NoPadding);
                return;
            }
            foreach (var r in _rows)
            {
                var rc = new Rectangle(0, y, w - 1, RowH);
                using (var path = Theme.Round(rc, 8)) using (var b = new SolidBrush(Theme.Surface)) g.FillPath(b, path);
                string kind = r.Requested ? I18n.T("La solicitó") : r.Proposed ? I18n.T("Se la propone un maquinista") : I18n.T("La usa");
                Color kc = r.Requested ? Color.FromArgb(240, 180, 41) : r.Proposed ? Color.FromArgb(96, 165, 250) : Theme.Subtle;
                TextRenderer.DrawText(g, r.Company, _fT, new Point(12, y + 8), Theme.Text, TextFormatFlags.NoPadding);
                int kx = 12 + TextRenderer.MeasureText(r.Company, _fT, Size.Empty, TextFormatFlags.NoPadding).Width + 10;
                TextRenderer.DrawText(g, kind, _fS, new Point(kx, y + 10), kc, TextFormatFlags.NoPadding);
                string svc = string.Format(r.Services == 1 ? I18n.T("{0} servicio") : I18n.T("{0} servicios"), r.Services.ToString("N0"));
                TextRenderer.DrawText(g, svc, _fS, new Rectangle(0, y + 9, w - 14, 18), Theme.Subtle, TextFormatFlags.Right | TextFormatFlags.NoPadding);
                int vi = _versions.IndexOf(r.Hash);
                string sub = (r.User.Length > 0 ? r.User + " · " : "") + r.At
                           + (_versions.Count > 1 && vi >= 0 ? " · " + string.Format(I18n.T("versión {0}"), r.Hash.Substring(0, 4) + "…" + r.Hash.Substring(r.Hash.Length - 4)) : "");
                TextRenderer.DrawText(g, sub, _fS, new Point(12, y + 32), Theme.Subtle, TextFormatFlags.NoPadding);
                if (r.Note.Length > 0)
                {
                    int sx = 12 + TextRenderer.MeasureText(sub, _fS, Size.Empty, TextFormatFlags.NoPadding).Width + 14;
                    TextRenderer.DrawText(g, "«" + r.Note + "»", _fN, new Rectangle(sx, y + 32, Math.Max(10, w - sx - 14), 18), Color.FromArgb(205, 210, 214), TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
                }
                y += RowH + 6;
            }
        }
    }
}
