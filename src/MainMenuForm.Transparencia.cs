// (1.2.90) Tablón → Transparencia: cómo va cada empresa en el mes (servidor: transparency_board, en
// sql/tablon-alquiler-transparencia.sql). Lo que cuenta en la Liga y lo que no, y unos indicadores iguales para todas
// (ocupación alta, trenes en el tope de masa, infracciones, carné bajo, anulados). Solo empresas: sin nombres de
// maquinistas. «¿Cómo se calcula?» explica la Liga y el cobro de los servicios con los ajustes vigentes.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

namespace SelectOR
{
    public partial class MainMenuForm
    {
        CardTable _trnList;
        RoundButton _trnMonthBtn, _trnHowBtn;
        bool _trnPrev;   // el mes anterior en lugar del de ahora
        sealed class TrnRow
        {
            public string Id, Name; public Image Logo; public int Members, Trains, PosG, PosV, PosM, LeagueServices, Services, Out, Annulled,
                PaxServices, HighOcc, FreServices, MassCap, LowLicense, Infr, InfrPts; public bool Eligible;
            public double LeagueNet, Km, Hours, Tkm, Income, Cost, Net; public long Pax, LeaguePax;
        }
        readonly List<TrnRow> _trnRows = new();

        CardTable BuildTransparencyList(Func<int, int, int, CardTable> cards)
        {
            var l = cards(1, 92, 560);
            l.SubCols = new[] { 2, 3 }; l.QuoteCol = 4; l.QuoteMarks = false; l.RightSubCols = new[] { 6, 7 };
            l.Accent = row => _trnRows.FirstOrDefault(x => x.Id == row.Key) is TrnRow t
                              ? (t.PosG == 1 ? Theme.Gold : t.PosG == 2 ? Color.FromArgb(200, 206, 212) : t.PosG == 3 ? Color.FromArgb(205, 137, 84) : t.Eligible ? TabTeal : Theme.SurfaceHi)
                              : null;
            return l;
        }

        static DateTime LeagueMonthNow()
        {
            DateTime t;
            try { t = TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTime.UtcNow, "Romance Standard Time"); } catch { t = DateTime.Now; }
            return new DateTime(t.Year, t.Month, 1);
        }

        string TrnMonthName() => LeagueMonthNow().AddMonths(_trnPrev ? -1 : 0).ToString("MMMM yyyy", I18n.Culture);

        void ToggleTransparencyMonth() { _trnPrev = !_trnPrev; _trnRows.Clear(); UpdateTablonButtons(); LoadTransparency(); }

        async void LoadTransparency()
        {
            int seq = ++_tabSeq;
            if (_trnRows.Count == 0) _trnList.ShowLoading(Tr("Cargando…"));
            object args = _trnPrev ? new { p_month = LeagueMonthNow().AddMonths(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) } : new { p_month = (string)null };
            var (json, err) = await Supa.RpcAsync("transparency_board", args);
            if (seq != _tabSeq) return;
            _trnRows.Clear();
            if (err != null)
            {
                _trnList.SetEmpty(MissingRpc(err) ? Tr("El servidor aún no tiene el portal de transparencia (falta tablon-alquiler-transparencia.sql).") : Tr("Error: ") + err);
                UpdateTablonButtons(); return;
            }
            try
            {
                using var d = JsonDocument.Parse(json);
                foreach (var e in d.RootElement.EnumerateArray())
                    _trnRows.Add(new TrnRow
                    {
                        Id = Str(e, "company_id"), Name = Str(e, "name"), Logo = LogoFor(Str(e, "company_id"), Str(e, "logo")),
                        Members = (int)Num(e, "members"), Trains = (int)Num(e, "trains"), PosG = (int)Num(e, "pos_general"), PosV = (int)Num(e, "pos_viajeros"),
                        PosM = (int)Num(e, "pos_mercancias"), Eligible = Bool(e, "eligible"), LeagueServices = (int)Num(e, "league_services"),
                        LeagueNet = Num(e, "league_net"), LeaguePax = (long)Num(e, "league_pax"), Services = (int)Num(e, "services"), Out = (int)Num(e, "out_of_league"),
                        Annulled = (int)Num(e, "annulled"), Km = Num(e, "km"), Hours = Num(e, "hours"), Pax = (long)Num(e, "pax"), Tkm = Num(e, "tkm"),
                        Income = Num(e, "income"), Cost = Num(e, "cost"), Net = Num(e, "net"), PaxServices = (int)Num(e, "pax_services"), HighOcc = (int)Num(e, "high_occ"),
                        FreServices = (int)Num(e, "freight_services"), MassCap = (int)Num(e, "mass_cap"), LowLicense = (int)Num(e, "low_license"),
                        Infr = (int)Num(e, "infractions"), InfrPts = (int)Num(e, "infraction_points")
                    });
            }
            catch { }
            _trnList.ClearRows();
            string N(double v) => v.ToString("N0", I18n.Culture);
            string Pct(int a, int b) => b > 0 ? Math.Round(100.0 * a / b).ToString("0", I18n.Culture) + " %" : "—";
            int k = 0;
            foreach (var r in _trnRows)
            {
                string pill = r.PosG > 0 ? string.Format(Tr("{0}.º en la Liga"), r.PosG) : Tr("Sin puesto en la Liga");
                var medals = new List<string>();
                if (r.PosV > 0 && r.PosV <= 3) medals.Add(string.Format(Tr("{0}.º en viajeros"), r.PosV));
                if (r.PosM > 0 && r.PosM <= 3) medals.Add(string.Format(Tr("{0}.º en mercancías"), r.PosM));
                if (medals.Count > 0) _trnList.Badges[k] = string.Join(" · ", medals);
                // 1.ª línea: la actividad del mes; 2.ª: qué cuenta en la Liga y qué no
                var act = new List<string>
                {
                    string.Format(r.Services == 1 ? Tr("{0} servicio") : Tr("{0} servicios"), N(r.Services)), N(r.Km) + " km",
                    string.Format(Tr("{0} h de conducción"), r.Hours.ToString("N1", I18n.Culture)),
                };
                if (r.Pax > 0) act.Add(string.Format(Tr("{0} viajeros"), N(r.Pax)));
                if (r.Tkm > 0) act.Add(N(r.Tkm) + " t·km");
                act.Add(string.Format(r.Members == 1 ? Tr("{0} socio") : Tr("{0} socios"), r.Members));
                act.Add(string.Format(r.Trains == 1 ? Tr("{0} tren") : Tr("{0} trenes"), r.Trains));
                var lg = new List<string> { string.Format(Tr("cuentan en la Liga: {0}"), N(r.LeagueServices)) };
                if (r.Out > 0) lg.Add(string.Format(Tr("no cuentan: {0}"), N(r.Out)));
                if (r.Annulled > 0) lg.Add(string.Format(Tr("anulados: {0}"), N(r.Annulled)));
                if (!r.Eligible) lg.Add(Tr("le faltan servicios para tener puesto"));
                // los indicadores, iguales para todas
                var ind = new List<string>();
                if (r.PaxServices > 0) ind.Add(string.Format(Tr("ocupación alta en el {0} de sus servicios de viajeros"), Pct(r.HighOcc, r.PaxServices)));
                if (r.FreServices > 0) ind.Add(string.Format(Tr("en el tope de masa el {0} de sus mercancías"), Pct(r.MassCap, r.FreServices)));
                ind.Add(r.Infr == 0 ? Tr("sin infracciones") : string.Format(r.Infr == 1 ? Tr("{0} infracción (−{1} puntos)") : Tr("{0} infracciones (−{1} puntos)"), r.Infr, r.InfrPts));
                if (r.LowLicense > 0) ind.Add(string.Format(Tr("{0} con el carné bajo"), r.LowLicense));
                string perSrv = r.Services > 0 ? string.Format(Tr("{0} por servicio"), EurT(r.Net / r.Services)) : "";
                string perH = r.Hours > 0.05 ? string.Format(Tr("{0} por hora"), EurT(r.Net / r.Hours)) : "";
                Color posC = r.PosG == 1 ? Theme.Gold : r.PosG > 0 ? TabTeal : Theme.Subtle;
                Color indC = r.Infr > 0 || r.LowLicense > 0 ? Color.FromArgb(240, 196, 90) : Theme.Subtle;
                _trnList.AddRow(new[] { r.Name, pill, string.Join("  ·  ", act), string.Join("  ·  ", lg), "📊  " + string.Join("   ·   ", ind), EurT(r.LeagueNet), Tr("beneficio en la Liga"), string.Join("  ·  ", new[] { perSrv, perH }.Where(x => x.Length > 0)) },
                    new Color?[] { null, posC, Theme.Subtle, TabTeal, indC, r.LeagueNet >= 0 ? Theme.AccentHi : Color.FromArgb(229, 115, 115), Theme.Subtle, Theme.Subtle }, r.Logo, r.Id);
                k++;
            }
            if (_trnRows.Count == 0) _trnList.SetEmpty(string.Format(Tr("Todavía no hay servicios en {0}."), TrnMonthName()));
            _tabBanner?.SetPage(TabIcons[4], Tr(TabKickers[4]) + " · " + TrnMonthName(), Tr(TablonTabNames[4]), Tr(TabIntros[4]), TabColors[4]);
            TablonNews(4, _trnRows.Where(r => r.PosG > 0).Take(3).Select(r => "📊 " + string.Format(Tr("{0}.º en la Liga: {1} · {2}"), r.PosG, r.Name, EurT(r.LeagueNet))));
            UpdateTablonButtons();
        }

        // ¿Cómo se calcula? La Liga y el cobro de los servicios, con los ajustes vigentes del servidor.
        async void ShowLeagueRules()
        {
            var (json, err) = await Supa.SelectAsync("app_settings?select=*&id=eq.1");
            JsonElement a = default; bool ok = false;
            JsonDocument doc = null;
            try { if (err == null) { doc = JsonDocument.Parse(json); if (doc.RootElement.GetArrayLength() > 0) { a = doc.RootElement[0]; ok = true; } } } catch { }
            if (!ok) { Msg(_tabMsg, Tr("Error: ") + (err ?? "app_settings"), true); doc?.Dispose(); return; }
            using (doc)
            {
                double V(string p, double def) { try { return a.TryGetProperty(p, out var x) && x.ValueKind == JsonValueKind.Number ? x.GetDouble() : def; } catch { return def; } }
                string E(double v) => v.ToString(Math.Abs(v - Math.Round(v)) > 0.004 && v < 1000 ? "N2" : "N0", I18n.Culture) + " €";
                string P(double v) => Math.Round(v * 100).ToString("0", I18n.Culture) + " %";
                string D(double v) => v.ToString("N0", I18n.Culture);
                long[] gen = { 0, 0, 0 }; long pv = 0, pm = 0, pp = 0;
                try
                {
                    if (a.TryGetProperty("league_prizes", out var pz) && pz.ValueKind == JsonValueKind.Object)
                    {
                        if (pz.TryGetProperty("general", out var g) && g.ValueKind == JsonValueKind.Array) { int i = 0; foreach (var x in g.EnumerateArray()) if (i < 3) gen[i++] = (long)x.GetDouble(); }
                        if (pz.TryGetProperty("viajeros", out var v) && v.ValueKind == JsonValueKind.Array && v.GetArrayLength() > 0) pv = (long)v[0].GetDouble();
                        if (pz.TryGetProperty("mercancias", out var m) && m.ValueKind == JsonValueKind.Array && m.GetArrayLength() > 0) pm = (long)m[0].GetDouble();
                        if (pz.TryGetProperty("participacion", out var q) && q.ValueKind == JsonValueKind.Number) pp = (long)q.GetDouble();
                    }
                }
                catch { }
                var sb = new StringBuilder();
                sb.AppendLine(Tr("LA LIGA"));
                sb.AppendLine("•  " + string.Format(Tr("Cuenta el beneficio (ingresos menos costes) de los servicios válidos del mes. Por maquinista y día cuentan como mucho sus {0} primeros servicios."), D(V("league_daily_cap", 8))));
                sb.AppendLine("•  " + string.Format(Tr("Para tener puesto, una empresa necesita {0} servicios en el mes."), D(V("league_min_services", 10))));
                sb.AppendLine("•  " + Tr("No cuentan los servicios anulados ni los de maquinistas con el carné por debajo de 6 puntos."));
                sb.AppendLine("•  " + string.Format(Tr("General: por beneficio. Viajeros: por viajeros llevados. Mercancías: por toneladas·km, con la masa contada como mucho hasta {0} t."), D(V("fre_mass_cap", 2000))));
                sb.AppendLine("•  " + string.Format(Tr("Premios: General {0}, {1} y {2}; Viajeros {3}; Mercancías {4}; y {5} a cada empresa con puesto."), EurT(gen[0]), EurT(gen[1]), EurT(gen[2]), EurT(pv), EurT(pm), EurT(pp)));
                sb.AppendLine();
                sb.AppendLine(Tr("LO QUE COBRA UN SERVICIO"));
                sb.AppendLine("•  " + string.Format(Tr("Viajeros: un billete de {0} por viajero (más con confort y velocidad) y {1} por cada viajero·km. Pasado el {2} de ocupación, cada viajero·km de más cuenta al {3}."),
                                  E(V("fleet_fare_base", 0.3)), E(V("fleet_fare_per_km", 0.3)), P(V("pax_occ_full", 0.8)), P(V("pax_occ_over", 0.3))));
                if (V("pax_cap_ref", 0) > 0)   // (1.2.90, tarifas-1290.sql) viajeros según la demanda
                    sb.AppendLine("•  " + string.Format(Tr("Viajeros según la demanda: hasta {0} plazas cobran todos; en un tren más grande los viajeros crecen menos que sus plazas, y como mucho cobran {1} veces sus plazas por servicio."),
                                      D(V("pax_cap_ref", 300)), D(V("pax_turnover_max", 3))));
                sb.AppendLine("•  " + string.Format(Tr("Bonificación por distancia en viajeros: desde los {0} km, hasta un {1} más a los {2} km."), D(V("pax_dist_from", 40)), P(V("pax_dist_max", 1)), D(V("pax_dist_full", 250))));
                sb.AppendLine("•  " + string.Format(Tr("Mercancías: {0} por km, según la masa del tren frente a {1} t (como mucho {2} t); desde los {3} km, hasta un {4} más a los {5} km."),
                                  E(V("default_income_per_km", 0)), D(V("fleet_mass_base", 500)), D(V("fre_mass_cap", 2000)), D(V("fre_dist_from", 40)), P(V("fre_dist_max", 1.5)), D(V("fre_dist_full", 300))));
                sb.AppendLine("•  " + string.Format(Tr("Costes: canon de {0} por km, energía de {1} por km (más con más masa), salario de {2} más {3} por hora (hasta {4} h), mantenimiento de cada unidad y el alquiler si es alquilada."),
                                  E(V("default_canon_per_km", 0)), E(V("default_energy_per_km", 0)), E(V("default_salary_per_service", 0)), E(V("salary_per_hour", 0)), D(V("salary_max_hours", 6))));
                if (V("canon_per_t_km", 0) > 0)
                    sb.AppendLine("•  " + string.Format(Tr("El canon sube con la masa: {0} más por cada tonelada y km."), E(V("canon_per_t_km", 0))));
                if (V("empty_cost_pct", 1) < 0.999)
                    sb.AppendLine("•  " + string.Format(Tr("Los traslados en vacío pagan el {0} del canon y de la energía."), P(V("empty_cost_pct", 1))));
                sb.AppendLine();
                sb.AppendLine(Tr("LOS INDICADORES (IGUALES PARA TODAS)"));
                sb.AppendLine("•  " + string.Format(Tr("Ocupación alta: servicios de viajeros con el tren más del {0} lleno de media."), P(V("pax_occ_full", 0.8))));
                sb.AppendLine("•  " + string.Format(Tr("Tope de masa: servicios de mercancías con {0} t o más (por encima ya no cobran más)."), D(V("fre_mass_cap", 2000))));
                sb.AppendLine("•  " + Tr("Infracciones: las del carné de sus maquinistas en el mes, con los puntos quitados. Carné bajo: servicios que no cuentan por tener menos de 6 puntos."));
                sb.AppendLine("•  " + Tr("Solo salen empresas: nunca los nombres de los maquinistas."));
                ThemedBox.Show(this, sb.ToString().TrimEnd(), Tr("¿Cómo se calcula?"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
    }
}
