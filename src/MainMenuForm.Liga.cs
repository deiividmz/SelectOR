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
        StyledTable _leagueList, _champList;
        Label _leagueInfo, _leaguePrizes, _leagueLeaders, _champInfo;
        Control[] _rankViews;
        int _rankTab;                 // 0 = Liga del mes · 1 = Campeones · 2 = Ranking general
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

        // ---- Pestañas de Ranking: Liga del mes · Campeones · Ranking general ----
        Panel WrapRankingTabs(Panel general)
        {
            var league = BuildLeaguePage();
            var champs = BuildChampionsPage();
            var body = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            general.Visible = false; champs.Visible = false;
            body.Controls.Add(general); body.Controls.Add(champs); body.Controls.Add(league);
            _rankViews = new Control[] { league, champs, general };
            var tabs = MakeSubTabs(new[] { "Liga del mes", "Campeones", "Ranking general" }, i =>
            {
                _rankTab = i;
                for (int k = 0; k < _rankViews.Length; k++) _rankViews[k].Visible = k == i;
                LoadRankTab();
            });
            var outer = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            outer.Controls.Add(body); outer.Controls.Add(tabs);
            return outer;
        }

        void LoadRankTab()
        {
            switch (_rankTab)
            {
                case 0: LoadLeague(); break;
                case 1: LoadChampions(); break;
                default: LoadRankings(); break;
            }
        }

        Panel BuildLeaguePage()
        {
            var host = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            var top = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, BackColor = Theme.Bg };
            _leagueInfo = new Label { AutoSize = true, ForeColor = Theme.Text, Font = Theme.Font(10.5f, FontStyle.Bold), Margin = new Padding(2, 4, 2, 2), UseMnemonic = false };
            _leaguePrizes = new Label { AutoSize = true, MaximumSize = new Size(1100, 0), ForeColor = Theme.Subtle, Font = Theme.Font(9f), Margin = new Padding(2, 2, 2, 6), UseMnemonic = false };
            top.Controls.Add(_leagueInfo); top.Controls.Add(_leaguePrizes);
            _leagueList = EmpTable();
            _leagueList.ImageColumn = 1;
            _leagueList.SetColumns(
                new StyledTable.Col("#", 44),
                new StyledTable.Col("EMPRESA", 0, true),
                new StyledTable.Col("SERV.", 70, false, HorizontalAlignment.Right),
                new StyledTable.Col("BENEFICIO", 140, false, HorizontalAlignment.Right),
                new StyledTable.Col("VIAJEROS", 100, false, HorizontalAlignment.Right),
                new StyledTable.Col("T·KM", 120, false, HorizontalAlignment.Right),
                new StyledTable.Col("PREMIO", 150, false, HorizontalAlignment.Right));
            _leagueLeaders = new Label { Dock = DockStyle.Bottom, AutoSize = false, Height = 44, ForeColor = Theme.Subtle, Font = Theme.Font(9f), Padding = new Padding(2, 8, 2, 0), UseMnemonic = false };
            host.Controls.Add(_leagueList);      // Fill
            host.Controls.Add(_leagueLeaders);   // Bottom
            host.Controls.Add(top);              // Top
            return host;
        }

        Panel BuildChampionsPage()
        {
            var host = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            _champInfo = new Label { Dock = DockStyle.Top, AutoSize = false, Height = 30, ForeColor = Theme.Subtle, Font = Theme.Font(9.5f), UseMnemonic = false };
            _champList = EmpTable();
            _champList.SetColumns(
                new StyledTable.Col("MES", 130),
                new StyledTable.Col("CATEGORÍA", 150),
                new StyledTable.Col("PUESTO", 80, false, HorizontalAlignment.Center),
                new StyledTable.Col("EMPRESA", 0, true),
                new StyledTable.Col("RESULTADO", 170, false, HorizontalAlignment.Right),
                new StyledTable.Col("PREMIO", 150, false, HorizontalAlignment.Right));
            host.Controls.Add(_champList); host.Controls.Add(_champInfo);
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

        string PrizesText(LeagueCfg c)
        {
            var partes = new List<string>();
            for (int i = 0; i < c.General.Length; i++) if (c.General[i] > 0) partes.Add(Ordinal(i + 1) + " " + Eur(c.General[i]));
            string s = Tr("Premios · General: ") + (partes.Count > 0 ? string.Join(" · ", partes) : "—");
            if (c.Viajeros > 0) s += "   |   " + Tr("Viajeros: ") + Eur(c.Viajeros);
            if (c.Mercancias > 0) s += "   |   " + Tr("Mercancías: ") + Eur(c.Mercancias);
            if (c.Participacion > 0) s += "   |   " + Tr("Participación: ") + Eur(c.Participacion);
            s += "\n" + string.Format(Tr("Cuenta el beneficio neto de los servicios registrados en el mes, como mucho {0} por maquinista y día. Para clasificar hacen falta {1} servicios."), c.Cap, c.Min);
            return s;
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
            if (_leagueList == null || !Supa.IsLoggedIn) return;
            _leagueList.BeginReload("liga");
            _leagueList.ShowLoading(Tr("Cargando…"));
            await Supa.RpcAsync("league_close_pending", new { });   // cierra los meses terminados (idempotente)
            var (c, err) = await ReadLeagueCfg();
            if (c == null)
            {
                _leagueInfo.Text = Tr("Liga del mes");
                _leaguePrizes.Text = ""; _leagueLeaders.Text = "";
                _leagueList.SetEmpty(NoLeagueOnServer(err) ? Tr("El servidor aún no tiene la liga mensual.") : Tr("Error: ") + err);
                _leagueList.EndReload();
                return;
            }
            bool pre = c.Month < c.Start;
            _leagueInfo.Text = !c.Enabled ? Tr("La liga mensual está desactivada.")
                             : pre ? string.Format(Tr("Pretemporada · la liga empieza en {0}. Esta clasificación es de prueba: no reparte premios."), MonthName(c.Start))
                             : string.Format(Tr("Liga de {0} · {1}"), MonthName(c.Month), Countdown(c.EndsUtc));
            _leaguePrizes.Text = PrizesText(c);

            var (json, err2) = await Supa.RpcAsync("league_standings", new { });
            _leagueList.ClearRows();
            if (err2 != null) { _leagueList.SetEmpty(Tr("Error: ") + err2); _leagueList.EndReload(); return; }
            var mias = new HashSet<string>();
            foreach (var co in _empCompanies) mias.Add(co.Id);
            string lidV = null, lidM = null;
            int n = 0;
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
                        if (pv == 1) { premio += c.Viajeros; }
                        if (pm == 1) { premio += c.Mercancias; }
                    }
                    if (pv == 1) lidV = string.Format(Tr("{0} ({1} viajeros)"), name, pax.ToString("N0", EsEs));
                    if (pm == 1) lidM = string.Format(Tr("{0} ({1} t·km)"), name, tkm.ToString("N0", EsEs));
                    Image logo = null;
                    try { logo = LogoFor(id, Str(e, "logo")) ?? PlaceholderLogo(name); } catch { }
                    bool mia = mias.Contains(id);
                    Color? gris = ok ? (Color?)null : Theme.Subtle;
                    _leagueList.AddRow(new[]
                        {
                            ok ? pg + "." : "—", name, serv.ToString("N0", EsEs), Eur(net), pax.ToString("N0", EsEs),
                            tkm.ToString("N0", EsEs), premio > 0 ? Eur(premio) : "—"
                        },
                        new Color?[] { ok && pg <= 3 ? Theme.Accent : gris, mia ? Theme.AccentHi : gris, gris, net < 0 ? RedC : gris, gris, gris, premio > 0 ? Theme.Accent : gris },
                        logo, id);
                    n++;
                }
            }
            catch { }
            if (n == 0) _leagueList.SetEmpty(Tr("Aún no hay servicios este mes."));
            _leagueList.EndReload();
            _leagueLeaders.Text = Tr("Líder en viajeros: ") + (lidV ?? "—") + "      " + Tr("Líder en mercancías: ") + (lidM ?? "—")
                                + "\n" + Tr("Las empresas en gris aún no tienen los servicios mínimos para clasificar.");
        }

        static string Ordinal(int n) => !I18n.English ? n + ".º"
            : n + (n % 100 is 11 or 12 or 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" });

        static string CategoryName(string c) => c switch
        {
            "general" => Tr("General"), "viajeros" => Tr("Viajeros"), "mercancias" => Tr("Mercancías"), "participacion" => Tr("Participación"), _ => c
        };

        async void LoadChampions()
        {
            if (_champList == null || !Supa.IsLoggedIn) return;
            _champList.BeginReload("campeones");
            _champList.ShowLoading(Tr("Cargando…"));
            await Supa.RpcAsync("league_close_pending", new { });
            var (json, err) = await Supa.RpcAsync("league_awards_list", new { p_limit = 300 });
            _champList.ClearRows();
            if (err != null)
            {
                _champList.SetEmpty(NoLeagueOnServer(err) ? Tr("El servidor aún no tiene la liga mensual.") : Tr("Error: ") + err);
                _champList.EndReload(); return;
            }
            var mias = new HashSet<string>();
            foreach (var co in _empCompanies) mias.Add(co.Id);
            int n = 0; double total = 0;
            try
            {
                using var d = JsonDocument.Parse(json);
                foreach (var e in d.RootElement.EnumerateArray())
                {
                    DateTime.TryParse(Str(e, "month"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var m);
                    string cat = Str(e, "category"), id = Str(e, "company_id");
                    int pos = (int)Num(e, "place"); double amt = Num(e, "amount"), val = Num(e, "value");
                    string res = cat switch
                    {
                        "general" => Eur(val),
                        "viajeros" => string.Format(Tr("{0} viajeros"), val.ToString("N0", EsEs)),
                        "mercancias" => string.Format(Tr("{0} t·km"), val.ToString("N0", EsEs)),
                        _ => string.Format(Tr("{0} servicios"), val.ToString("N0", EsEs))
                    };
                    bool mia = mias.Contains(id);
                    if (mia) total += amt;
                    _champList.AddRow(new[] { MonthName(m, true), CategoryName(cat), pos > 0 ? Ordinal(pos) : "—", Str(e, "company_name"), res, Eur(amt) },
                        new Color?[] { Theme.Subtle, null, pos == 1 ? Theme.Accent : (Color?)null, mia ? Theme.AccentHi : (Color?)null, Theme.Subtle, Theme.Accent });
                    n++;
                }
            }
            catch { }
            if (n == 0) _champList.SetEmpty(Tr("Aún no hay campeones: se conocerán al terminar la primera temporada."));
            _champList.EndReload();
            _champInfo.Text = n == 0 ? "" : string.Format(Tr("Premios ganados por tus empresas: {0}"), Eur(total));
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
                var porEmpresa = new Dictionary<string, List<string>>();
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
                        if (!porEmpresa.TryGetValue(id, out var l)) porEmpresa[id] = l = new List<string>();
                        l.Add(txt + " (" + Eur(amt) + ")");
                        totales[id] = (totales.TryGetValue(id, out var t) ? t : 0) + amt;
                    }
                if (ultimo != visto) { _prefs.LeagueSeenMonth = ultimo; _prefs.Save(); }
                if (porEmpresa.Count == 0) return;
                var sb = new System.Text.StringBuilder(string.Format(Tr("¡Enhorabuena! Resultados de la Liga de {0}:"), MonthName(mesAviso)) + "\n\n");
                foreach (var kv in porEmpresa)
                    sb.Append("• ").Append(mias[kv.Key]).Append(": ").Append(string.Join(", ", kv.Value)).Append(" — ").Append(Eur(totales[kv.Key])).Append('\n');
                sb.Append('\n').Append(Tr("El dinero ya está en la tesorería de la empresa. Consulta la clasificación en Ranking → Campeones."));
                MessageBox.Show(this, sb.ToString(), Tr("Liga de empresas"), MessageBoxButtons.OK, MessageBoxIcon.Information);
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
