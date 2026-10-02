// Liga mensual de empresas (liga-mensual.sql): clasificación del mes, campeones, aviso de premio y
// ajustes del superadmin. Las cuentas y el reparto de premios los hace SIEMPRE el servidor.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SelectOR
{
    public partial class MainMenuForm
    {
        PodiumBoard _leagueBoard; ChampionsBoard _champBoard;
        Control[] _rankViews;
        FlowLayoutPanel _rankTabBar;
        int _rankTab;                 // 0 = Liga del mes · 1 = Campeones · 2 = Empresas · 3 = Maquinistas
        bool _leagueChecked;          // aviso de premios: una vez por sesión

        static CultureInfo LeagueCul => new CultureInfo(I18n.English ? "en-GB" : "es-ES");
        // «noviembre de 2026» / «November 2026»; capital = mayúscula inicial (al empezar una celda o frase).
        static string MonthName(DateTime m, bool capital = false)
        {
            string n = LeagueCul.DateTimeFormat.GetMonthName(m.Month);
            string s = I18n.English ? n + " " + m.Year : n + " de " + m.Year;
            return capital || I18n.English ? s.Substring(0, 1).ToUpper(LeagueCul) + s.Substring(1) : s;
        }
        static bool NoLeagueOnServer(string err) => err != null && (err.IndexOf("Could not find the function", StringComparison.OrdinalIgnoreCase) >= 0
                                                                   || err.IndexOf("PGRST202", StringComparison.OrdinalIgnoreCase) >= 0);

        // ---- Pestañas de Ranking: Liga del mes · Campeones · Empresas · Maquinistas ----
        Panel WrapRankingTabs(Control companies, Control drivers)
        {
            var league = BuildLeaguePage();
            var champs = BuildChampionsPage();
            var body = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(0, 6, 0, 0) };
            companies.Visible = false; drivers.Visible = false; champs.Visible = false;
            body.Controls.Add(drivers); body.Controls.Add(companies); body.Controls.Add(champs); body.Controls.Add(league);
            _rankViews = new Control[] { league, champs, companies, drivers };
            var tabs = MakeSubTabs(new[] { "Liga del mes", "Campeones", "Empresas", "Maquinistas" }, i =>
            {
                _rankTab = i;
                for (int k = 0; k < _rankViews.Length; k++) _rankViews[k].Visible = k == i;
                LoadRankTab();
            });
            _rankTabBar = tabs;
            var outer = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            outer.Controls.Add(body); outer.Controls.Add(tabs);
            return outer;
        }

        // Abre una pestaña de Ranking por código (0 = Liga del mes · 1 = Campeones · 2 = Empresas · 3 = Maquinistas).
        void SelectRankTab(int i)
        {
            if (_rankViews == null || i < 0 || i >= _rankViews.Length) return;
            if (_rankTabBar != null)
                for (int k = 0; k < _rankTabBar.Controls.Count; k++)
                    if (_rankTabBar.Controls[k] is RoundButton b) { b.Active = k == i; b.Invalidate(); }
            _rankTab = i;
            for (int k = 0; k < _rankViews.Length; k++) _rankViews[k].Visible = k == i;
            LoadRankTab();
        }

        void LoadRankTab()
        {
            switch (_rankTab)
            {
                case 0: LoadLeague(); break;
                case 1: LoadChampions(); break;
                default: LoadRankings(); break;   // Empresas y Maquinistas
            }
        }

        Panel BuildLeaguePage()
        {
            var host = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            _leagueBoard = new PodiumBoard { Dock = DockStyle.Fill, MineTag = Tr("tu empresa"), EmptyText = Tr("Cargando…") };
            host.Controls.Add(_leagueBoard);
            return host;
        }

        Panel BuildChampionsPage()
        {
            var host = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            _champBoard = new ChampionsBoard { Dock = DockStyle.Fill, EmptyText = Tr("Cargando…") };
            host.Controls.Add(_champBoard);
            return host;
        }

        sealed class LeagueCfg
        {
            public bool Enabled; public DateTime Start, Month, EndsUtc; public int Cap, Min;
            public double[] General = new double[0]; public double Viajeros, Mercancias, Participacion;
            public double GeneralPrize(int pos) => pos >= 1 && pos <= General.Length ? General[pos - 1] : 0;
        }

        static double FirstOrValue(JsonElement p, string cat)
        {
            if (!p.TryGetProperty(cat, out var v)) return 0;
            if (v.ValueKind == JsonValueKind.Array) return v.GetArrayLength() > 0 && v[0].TryGetDouble(out var a) ? a : 0;
            return v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : 0;
        }

        async Task<(LeagueCfg cfg, string err)> ReadLeagueCfg()
        {
            var (json, err) = await Supa.RpcAsync("league_settings", new { });
            if (err != null) return (null, err);
            try
            {
                using var d = JsonDocument.Parse(json);
                var r = d.RootElement;
                if (r.ValueKind == JsonValueKind.Array && r.GetArrayLength() > 0) r = r[0];
                var c = new LeagueCfg
                {
                    Enabled = r.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.True,
                    Cap = (int)Num(r, "daily_cap"), Min = (int)Num(r, "min_services")
                };
                DateTime.TryParse(Str(r, "start"), CultureInfo.InvariantCulture, DateTimeStyles.None, out c.Start);
                DateTime.TryParse(Str(r, "current_month"), CultureInfo.InvariantCulture, DateTimeStyles.None, out c.Month);
                DateTime.TryParse(Str(r, "ends_at"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out c.EndsUtc);
                if (r.TryGetProperty("prizes", out var p) && p.ValueKind == JsonValueKind.Object)
                {
                    if (p.TryGetProperty("general", out var g) && g.ValueKind == JsonValueKind.Array)
                    {
                        var l = new List<double>();
                        foreach (var x in g.EnumerateArray()) l.Add(x.TryGetDouble(out var v) ? v : 0);
                        c.General = l.ToArray();
                    }
                    c.Viajeros = FirstOrValue(p, "viajeros"); c.Mercancias = FirstOrValue(p, "mercancias"); c.Participacion = FirstOrValue(p, "participacion");
                }
                return (c, null);
            }
            catch (Exception ex) { return (null, ex.Message); }
        }

        static string Eur(double v) => v.ToString("N0", EsEs) + " €";

        // Premios del mes como chips: «1.000.000 € · 1.º general»…
        List<(string, string)> PrizeChips(LeagueCfg c)
        {
            var l = new List<(string, string)>();
            for (int i = 0; i < c.General.Length; i++) if (c.General[i] > 0) l.Add((Eur(c.General[i]), string.Format(Tr("{0} general"), Ordinal(i + 1))));
            if (c.Viajeros > 0) l.Add((Eur(c.Viajeros), Tr("Líder en viajeros")));
            if (c.Mercancias > 0) l.Add((Eur(c.Mercancias), Tr("Líder en mercancías")));
            if (c.Participacion > 0) l.Add((Eur(c.Participacion), Tr("Participación")));
            return l;
        }

        static string Countdown(DateTime endsUtc)
        {
            var t = endsUtc - DateTime.UtcNow;
            if (t.TotalSeconds <= 0) return Tr("cerrando…");
            return t.TotalDays >= 1 ? string.Format(Tr("termina en {0} d {1} h"), (int)t.TotalDays, t.Hours)
                                    : string.Format(Tr("termina en {0} h {1} min"), (int)t.TotalHours, t.Minutes);
        }

        async void LoadLeague()
        {
            if (_leagueBoard == null || !Supa.IsLoggedIn) return;
            var b = _leagueBoard;
            if (b.Entries.Count == 0) { b.EmptyText = Tr("Cargando…"); b.Invalidate(); }
            await Supa.RpcAsync("league_close_pending", new { });   // cierra los meses terminados (idempotente)
            var (c, err) = await ReadLeagueCfg();
            if (c == null)
            {
                b.Entries = new List<PodiumBoard.Entry>();
                b.EmptyText = NoLeagueOnServer(err) ? Tr("El servidor aún no tiene la liga mensual.") : Tr("Error: ") + err;
                b.Relayout(true); return;
            }
            bool pre = c.Month < c.Start;
            var (json, err2) = await Supa.RpcAsync("league_standings", new { });
            if (err2 != null) { b.Entries = new List<PodiumBoard.Entry>(); b.EmptyText = Tr("Error: ") + err2; b.Relayout(true); return; }
            var mias = new HashSet<string>();
            foreach (var co in _empCompanies) mias.Add(co.Id);
            var entries = new List<PodiumBoard.Entry>();
            var leaders = new List<(string, string, string, string)>();
            try
            {
                using var d = JsonDocument.Parse(json);
                foreach (var e in d.RootElement.EnumerateArray())
                {
                    string id = Str(e, "company_id"), name = Str(e, "name");
                    int serv = (int)Num(e, "services"); double net = Num(e, "net"), pax = Num(e, "pax"), tkm = Num(e, "tkm");
                    bool ok = e.TryGetProperty("eligible", out var el) && el.ValueKind == JsonValueKind.True;
                    int pg = (int)Num(e, "pos_general"), pv = (int)Num(e, "pos_viajeros"), pm = (int)Num(e, "pos_mercancias");
                    if (serv == 0 && !mias.Contains(id)) continue;   // sin servicios este mes: no ocupa sitio
                    double premio = 0;
                    if (ok && !pre && c.Enabled)
                    {
                        premio += c.GeneralPrize(pg) + c.Participacion;
                        if (pv == 1) premio += c.Viajeros;
                        if (pm == 1) premio += c.Mercancias;
                    }
                    if (pv == 1) leaders.Add(("🧍", Tr("LÍDER EN VIAJEROS"), name, string.Format(Tr("{0} viajeros"), pax.ToString("N0", EsEs))));
                    if (pm == 1) leaders.Add(("⚖", Tr("LÍDER EN MERCANCÍAS"), name, string.Format(Tr("{0} t·km"), tkm.ToString("N0", EsEs))));
                    Image logo = null;
                    try { logo = LogoFor(id, Str(e, "logo")); } catch { }
                    var parts = new List<string> { string.Format(Tr("{0} servicios"), serv.ToString("N0", EsEs)) };
                    if (pax > 0) parts.Add(string.Format(Tr("{0} viajeros"), pax.ToString("N0", EsEs)));
                    if (tkm > 0) parts.Add(string.Format(Tr("{0} t·km"), tkm.ToString("N0", EsEs)));
                    int faltan = Math.Max(0, c.Min - serv);
                    entries.Add(new PodiumBoard.Entry
                    {
                        Id = id, Name = name, Logo = logo, Pos = ok ? pg : 0, Out = !ok, Mine = mias.Contains(id),
                        Value = Eur(net), ValueColor = net < 0 ? RedC : (Color?)null, Detail = string.Join(" · ", parts),
                        Prize = premio > 0 ? Tr("premio") + " " + Eur(premio) : "",
                        OutNote = string.Format(Tr(faltan == 1 ? "le falta {0} servicio para clasificar" : "le faltan {0} servicios para clasificar"), faltan),
                        OutProgress = !ok && c.Min > 0 ? Math.Min(1, serv / (double)c.Min) : -1,
                        Cols = new (string, string, Color?)[]
                        {
                            (Tr("SERVICIOS"), serv.ToString("N0", EsEs), null), (Tr("BENEFICIO"), Eur(net), net < 0 ? RedC : (Color?)null),
                            (Tr("VIAJEROS"), pax.ToString("N0", EsEs), null), (Tr("T·KM"), tkm.ToString("N0", EsEs), null)
                        }
                    });
                }
            }
            catch { }
            b.Title = !c.Enabled ? Tr("La liga mensual está desactivada.")
                    : pre ? string.Format(Tr("Pretemporada · la liga empieza en {0}. Esta clasificación es de prueba: no reparte premios."), MonthName(c.Start))
                    : "🏆  " + string.Format(Tr("Liga de {0}"), MonthName(c.Month));
            b.Pills = new List<string>();
            if (c.Enabled && !pre) b.Pills.Add("⏱  " + Countdown(c.EndsUtc));
            b.Pills.Add(string.Format(Tr("hasta {0} servicios por maquinista y día · mínimo {1} para clasificar"), c.Cap, c.Min));
            b.Prizes = c.Enabled && !pre ? PrizeChips(c) : new List<(string, string)>();
            b.Leaders = leaders;
            b.Entries = entries;
            b.EmptyText = null;
            b.Footnote = entries.Count == 0 ? Tr("Aún no hay servicios este mes.") : "";
            b.Relayout(true);
        }

        static string Ordinal(int n) => !I18n.English ? n + ".º"
            : n + (n % 100 is 11 or 12 or 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" });

        static string CategoryName(string c) => c switch
        {
            "general" => Tr("General"), "viajeros" => Tr("Viajeros"), "mercancias" => Tr("Mercancías"), "participacion" => Tr("Participación"), _ => c
        };

        async void LoadChampions()
        {
            if (_champBoard == null || !Supa.IsLoggedIn) return;
            var b = _champBoard;
            if (b.Months.Count == 0) { b.EmptyText = Tr("Cargando…"); b.Invalidate(); }
            await Supa.RpcAsync("league_close_pending", new { });
            var (json, err) = await Supa.RpcAsync("league_awards_list", new { p_limit = 300 });
            if (err != null)
            {
                b.Months = new List<ChampionsBoard.Month>(); b.SummaryTitle = "";
                b.EmptyText = NoLeagueOnServer(err) ? Tr("El servidor aún no tiene la liga mensual.") : Tr("Error: ") + err;
                b.Relayout(true); return;
            }
            var mias = new HashSet<string>();
            foreach (var co in _empCompanies) mias.Add(co.Id);
            var months = new List<ChampionsBoard.Month>();
            var byKey = new Dictionary<string, ChampionsBoard.Month>();
            int premios = 0, primeros = 0; double total = 0;
            try
            {
                using var d = JsonDocument.Parse(json);
                foreach (var e in d.RootElement.EnumerateArray())
                {
                    DateTime.TryParse(Str(e, "month"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var m);
                    string cat = Str(e, "category"), id = Str(e, "company_id");
                    int pos = (int)Num(e, "place"); double amt = Num(e, "amount");
                    string key = m.ToString("yyyy-MM");
                    if (!byKey.TryGetValue(key, out var mo)) { mo = new ChampionsBoard.Month { Title = MonthName(m, true) }; byKey[key] = mo; months.Add(mo); }
                    string icon = cat switch
                    {
                        "general" => pos == 1 ? "🥇" : pos == 2 ? "🥈" : pos == 3 ? "🥉" : "🏅",
                        "viajeros" => "🧍", "mercancias" => "⚖", _ => "⭐"
                    };
                    string label = Str(e, "company_name") + (cat == "general" || cat == "participacion" ? "" : "  ·  " + CategoryName(cat));
                    bool mia = mias.Contains(id);
                    if (cat == "participacion" && !mia) continue;   // la participación de los demás no aporta nada aquí
                    mo.Rows.Add((icon, cat == "participacion" ? Str(e, "company_name") + "  ·  " + CategoryName(cat) : label, Eur(amt), mia));
                    if (mia) { premios++; total += amt; if (cat == "general" && pos == 1) primeros++; }
                }
            }
            catch { }
            b.Months = months;
            b.SummaryTitle = months.Count > 0 ? Tr("Palmarés de tus empresas") : "";
            b.SummaryText = string.Format(Tr("{0} premios · {1} primeros puestos · {2} en premios"), premios, primeros, Eur(total));
            b.EmptyText = months.Count == 0 ? Tr("Aún no hay campeones: se conocerán al terminar la primera temporada.") : null;
            b.Relayout(true);
        }

        // Aviso al abrir Empresas si alguna de mis empresas ha cobrado premios desde la última vez.
        async Task CheckLeagueAwards()
        {
            try
            {
                var (nj, e0) = await Supa.RpcAsync("league_close_pending", new { });
                if (e0 != null) return;   // servidor sin la liga
                if (int.TryParse((nj ?? "").Trim().Trim('"'), out int cerrados) && cerrados > 0) LoadCompanies(soft: true);   // saldos con el premio
                var (json, err) = await Supa.RpcAsync("league_awards_list", new { p_limit = 100 });
                if (err != null) return;
                var mias = new Dictionary<string, string>();
                foreach (var co in _empCompanies) mias[co.Id] = co.Name;
                string visto = _prefs.LeagueSeenMonth ?? "", ultimo = visto;
                var porEmpresa = new Dictionary<string, List<LeagueAwardsDialog.Prize>>();
                var totales = new Dictionary<string, double>();
                DateTime mesAviso = DateTime.MinValue;
                using (var d = JsonDocument.Parse(json))
                    foreach (var e in d.RootElement.EnumerateArray())
                    {
                        string mes = Str(e, "month"); if (mes.Length >= 7) mes = mes.Substring(0, 7);
                        if (string.CompareOrdinal(mes, ultimo) > 0) ultimo = mes;
                        if (string.CompareOrdinal(mes, visto) <= 0) continue;
                        string id = Str(e, "company_id");
                        if (!mias.ContainsKey(id)) continue;
                        DateTime.TryParse(Str(e, "month"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var m);
                        if (m > mesAviso) mesAviso = m;
                        string cat = Str(e, "category"); int pos = (int)Num(e, "place"); double amt = Num(e, "amount");
                        string txt = cat == "general" ? string.Format(Tr("{0} puesto general"), Ordinal(pos)) : CategoryName(cat);
                        if (!porEmpresa.TryGetValue(id, out var l)) porEmpresa[id] = l = new List<LeagueAwardsDialog.Prize>();
                        var (ico, tinte) = cat switch
                        {
                            "general" => pos == 1 ? ("🥇", Theme.Gold) : pos == 2 ? ("🥈", LeagueAwardsDialog.Silver) : pos == 3 ? ("🥉", LeagueAwardsDialog.Bronze) : ("🏅", Theme.Gold),
                            "viajeros" => ("🧳", Color.FromArgb(120, 170, 235)),
                            "mercancias" => ("📦", LeagueAwardsDialog.Bronze),
                            _ => ("⭐", Theme.AccentHi)
                        };
                        l.Add(new LeagueAwardsDialog.Prize { Icon = ico, Label = txt, Amount = amt, Tint = tinte });
                        totales[id] = (totales.TryGetValue(id, out var t) ? t : 0) + amt;
                    }
                if (ultimo != visto) { _prefs.LeagueSeenMonth = ultimo; _prefs.Save(); }
                if (porEmpresa.Count == 0) return;
                // Aviso propio (trofeo, medallas y lo cobrado por cada empresa), la que más ha ganado primero.
                var lista = new List<LeagueAwardsDialog.CompanyPrizes>();
                foreach (var kv in porEmpresa)
                    lista.Add(new LeagueAwardsDialog.CompanyPrizes { Name = mias[kv.Key], Total = totales[kv.Key], Prizes = kv.Value });
                lista.Sort((a, b) => b.Total.CompareTo(a.Total));
                using var dlg = new LeagueAwardsDialog(MonthName(mesAviso), lista, Eur);
                dlg.ShowDialog(this);
                if (dlg.WantsChampions)
                {
                    OpenEmpresasAt(null, 4);   // Ranking
                    SelectRankTab(1);           // → Campeones
                }
            }
            catch { }
        }

        // ---- Ajustes (superadmin): configuración de la liga ----
        CheckBox _lgEnabled;
        RoundedInput _lgG1, _lgG2, _lgG3, _lgPax, _lgFreight, _lgPart, _lgCap, _lgMin;

        TableLayoutPanel BuildLeagueSettingsPage()
        {
            var page = FormPage(false);
            // Como «Economía de flota»: columna interior de ancho propio (los campos no se estiran).
            var p = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, BackColor = Theme.Bg, Margin = new Padding(0) };
            page.Controls.Add(p);
            p.Controls.Add(EmpHeader("LIGA MENSUAL DE EMPRESAS (GLOBAL, SUPERADMIN)"));
            p.Controls.Add(EmpIntro("Cada mes natural es una temporada. Al terminar, el servidor ingresa los premios en la tesorería de las empresas ganadoras y los apunta en su Banca."));
            _lgEnabled = ParamCheck(Tr("Liga activada")); _lgEnabled.Checked = true; p.Controls.Add(_lgEnabled);
            RoundedInput Campo(string etiqueta, string ph) { p.Controls.Add(EmpFieldLabel(etiqueta)); var i = EmpInput(ph); i.Width = 240; i.Anchor = AnchorStyles.Left; p.Controls.Add(i); return i; }
            _lgG1 = Campo(Tr("Premio general · 1.º puesto (€)"), "1000000");
            _lgG2 = Campo(Tr("Premio general · 2.º puesto (€)"), "500000");
            _lgG3 = Campo(Tr("Premio general · 3.º puesto (€)"), "250000");
            _lgPax = Campo(Tr("Premio de viajeros (€)"), "400000");
            _lgFreight = Campo(Tr("Premio de mercancías (€)"), "400000");
            _lgPart = Campo(Tr("Premio de participación, por empresa (€)"), "50000");
            _lgCap = Campo(Tr("Servicios que puntúan por maquinista y día"), "8");
            _lgMin = Campo(Tr("Servicios mínimos para clasificar"), "10");
            var save = EmpButton(Tr("Guardar liga"), primary: true); save.Width = 240;
            save.Click += (s, e) => SaveLeagueSettings();
            p.Controls.Add(save);
            return page;
        }

        async void LoadLeagueSettings()
        {
            if (_lgG1 == null || !Supa.IsSuperadmin) return;
            var (c, err) = await ReadLeagueCfg();
            if (c == null) { if (NoLeagueOnServer(err)) Msg(_tariffMsg, Tr("El servidor aún no tiene la liga mensual."), true); return; }
            _lgEnabled.Checked = c.Enabled;
            _lgG1.Box.Text = c.GeneralPrize(1).ToString("0.##", EsEs); _lgG2.Box.Text = c.GeneralPrize(2).ToString("0.##", EsEs); _lgG3.Box.Text = c.GeneralPrize(3).ToString("0.##", EsEs);
            _lgPax.Box.Text = c.Viajeros.ToString("0.##", EsEs); _lgFreight.Box.Text = c.Mercancias.ToString("0.##", EsEs); _lgPart.Box.Text = c.Participacion.ToString("0.##", EsEs);
            _lgCap.Box.Text = c.Cap.ToString(); _lgMin.Box.Text = c.Min.ToString();
        }

        async void SaveLeagueSettings()
        {
            if (!Supa.IsSuperadmin) { Msg(_tariffMsg, Tr("Solo el superadministrador puede configurar la liga."), true); return; }
            Msg(_tariffMsg, Tr("Guardando liga…"), false);
            var prizes = new
            {
                general = new[] { ParseNum(_lgG1.Box.Text), ParseNum(_lgG2.Box.Text), ParseNum(_lgG3.Box.Text) },
                viajeros = new[] { ParseNum(_lgPax.Box.Text) },
                mercancias = new[] { ParseNum(_lgFreight.Box.Text) },
                participacion = ParseNum(_lgPart.Box.Text)
            };
            var (_, err) = await Supa.RpcAsync("set_league_settings", new
            {
                p_enabled = _lgEnabled.Checked,
                p_daily_cap = (int)ParseNum(_lgCap.Box.Text),
                p_min_services = (int)ParseNum(_lgMin.Box.Text),
                p_prizes = prizes
            });
            if (err != null) { Msg(_tariffMsg, NoLeagueOnServer(err) ? Tr("El servidor aún no tiene la liga mensual.") : Tr("Error: ") + err, true); return; }
            Msg(_tariffMsg, Tr("Liga guardada."), false);
        }
    }
}
