// Empresas → Ajustes → «Cobros y pluses» (2026-11-09; sql/ajustes-cobros.sql, solo el administrador): las cifras del cálculo
// de los servicios que antes solo se cambiaban con un SQL. Se ven en unidades fáciles (porcentajes como «80 %», la bonificación
// por distancia como «+30 %») y se guardan como las usa el servidor (0,8 · 0,3 · 1,3…). Valen al momento: el cálculo de cada
// servicio y los Consejos del Tablón las leen del servidor.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Windows.Forms;

namespace SelectOR
{
    public partial class MainMenuForm
    {
        // columna · texto · cómo se enseña (× factor, + desplazamiento: lo que se ve = valor × factor + desplazamiento) · decimales
        static readonly (string key, string label, double mul, double add, string fmt)[] EcoFields =
        {
            ("pax_cap_ref", "Plazas que cobran (0 = sin límite)", 1, 0, "0"),
            ("pax_cap_exp", "Exponente por encima (1 = todas)", 1, 0, "0.##"),
            ("pax_turnover_max", "Tope (× plazas; 0 = sin tope)", 1, 0, "0.##"),
            ("pax_occ_full", "Ocupación llena (%)", 100, 0, "0.#"),
            ("pax_occ_over", "De pie, por encima, cuentan al (%)", 100, 0, "0.#"),
            ("pax_dist_from", "Plus por distancia desde (km)", 1, 0, "0"),
            ("pax_dist_full", "Plus máximo a los (km)", 1, 0, "0"),
            ("pax_dist_max", "Plus máximo por distancia (+%)", 100, 0, "0.#"),
            ("canon_per_t_km", "Canon por tonelada y km (€)", 1, 0, "0.###"),
            ("empty_cost_pct", "Traslado en vacío (% canon y energía)", 100, 0, "0.#"),
            ("fre_dist_from", "Plus por distancia desde (km)", 1, 0, "0"),
            ("fre_dist_full", "Plus máximo a los (km)", 1, 0, "0"),
            ("fre_dist_max", "Plus máximo por distancia (+%)", 100, -100, "0.#"),
            ("fre_mass_cap", "Carga que cobra como mucho (t)", 1, 0, "0"),
            ("room_bonus_pct", "Plus de sala (% del beneficio)", 1, 0, "0.#"),
            ("room_jefe_pct", "Plus con jefe de circulación (%)", 1, 0, "0.#"),
            ("room_bonus_daily", "Servicios con plus al día", 1, 0, "0"),
            ("room_min_minutes", "Minutos mínimos del servicio", 1, 0, "0"),
            ("room_streak_n", "Racha: servicios en la semana", 1, 0, "0"),
            ("room_streak_bonus", "Racha: premio (€)", 1, 0, "0"),
        };
        readonly Dictionary<string, RoundedInput> _ecoIn = new();
        Label _ecoMsg;

        TableLayoutPanel BuildEconomyPage()
        {
            var (page, L, R) = TwoColPage();
            page.Visible = false;
            TableLayoutPanel Grid(int cols, params string[] keys)
                => FieldGrid(cols, 140, keys.Select(k => (Tr(EcoFields.First(f => f.key == k).label), _ecoIn[k] = EmpInput(""))).ToArray());
            Label H(string t) { var h = EmpHeader(t); h.Margin = new Padding(2, 6, 2, 2); return h; }   // (encabezados más juntos: cabe todo a 1366×768)
            L.Controls.Add(H("VIAJEROS (GLOBAL, ADMINISTRADOR)"));
            L.Controls.Add(Grid(3, "pax_cap_ref", "pax_cap_exp", "pax_turnover_max", "pax_occ_full", "pax_occ_over", "pax_dist_from", "pax_dist_full", "pax_dist_max"));
            L.Controls.Add(H("COSTES"));
            L.Controls.Add(Grid(3, "canon_per_t_km", "empty_cost_pct"));
            R.Controls.Add(H("MERCANCÍAS"));
            R.Controls.Add(Grid(3, "fre_dist_from", "fre_dist_full", "fre_dist_max", "fre_mass_cap"));
            R.Controls.Add(H("SALA COMPARTIDA"));
            R.Controls.Add(Grid(3, "room_bonus_pct", "room_jefe_pct", "room_bonus_daily", "room_min_minutes", "room_streak_n", "room_streak_bonus"));
            // el botón y, a su lado, el mensaje
            var bar = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Theme.Bg, Margin = new Padding(0, 4, 0, 0) };
            var save = EmpButton(Tr("Guardar cobros y pluses"), primary: true); save.Width = 240; save.Margin = new Padding(2, 2, 10, 2);
            save.Click += (s, e) => SaveEconomy();
            _ecoMsg = EmpMsg(); _ecoMsg.MaximumSize = new System.Drawing.Size(240, 0); _ecoMsg.Margin = new Padding(0, 4, 0, 0);
            bar.Controls.Add(save); bar.Controls.Add(_ecoMsg);
            R.Controls.Add(bar);
            return page;
        }

        async void LoadEconomy()
        {
            if (_ecoIn.Count == 0) return;
            var (json, err) = await Supa.RpcAsync("admin_economy_get", new { });
            if (err != null)
            {
                Msg(_ecoMsg, MissingRpc(err) ? Tr("El servidor aún no tiene esta opción (falta ajustes-cobros.sql).") : Tr("Error: ") + CleanRpcErr(err), true);
                return;
            }
            try
            {
                using var d = JsonDocument.Parse(json); var r = d.RootElement;
                foreach (var f in EcoFields)
                    if (_ecoIn.TryGetValue(f.key, out var inp) && r.TryGetProperty(f.key, out var v) && v.ValueKind == JsonValueKind.Number)
                        inp.Box.Text = (v.GetDouble() * f.mul + f.add).ToString(f.fmt, I18n.Culture);
                Msg(_ecoMsg, "", false);
            }
            catch { }
        }

        async void SaveEconomy()
        {
            if (!Supa.IsSuperadmin) return;
            var vals = new Dictionary<string, double>();
            foreach (var f in EcoFields)
            {
                if (!_ecoIn.TryGetValue(f.key, out var inp)) continue;
                string t = inp.Box.Text.Trim();
                if (!double.TryParse(t.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double shown))
                { Msg(_ecoMsg, string.Format(Tr("Revisa «{0}»: tiene que ser un número."), Tr(f.label)), true); inp.Box.Focus(); return; }
                vals[f.key] = Math.Round((shown - f.add) / f.mul, 6);
            }
            if (vals.TryGetValue("pax_dist_full", out var pf) && vals.TryGetValue("pax_dist_from", out var p0) && pf <= p0
                || vals.TryGetValue("fre_dist_full", out var ff) && vals.TryGetValue("fre_dist_from", out var f0) && ff <= f0)
            { Msg(_ecoMsg, Tr("El plus máximo por distancia tiene que llegar a más km que donde empieza."), true); return; }
            Msg(_ecoMsg, Tr("Guardando…"), false);
            var (_, err) = await Supa.RpcAsync("admin_economy_set", new { p_vals = vals });
            if (err != null) { Msg(_ecoMsg, MissingRpc(err) ? Tr("El servidor aún no tiene esta opción (falta ajustes-cobros.sql).") : Tr("Error: ") + CleanRpcErr(err), true); return; }
            _tipsLoaded = DateTime.MinValue;   // (los Consejos del Tablón, con las cifras nuevas la próxima vez que se abran)
            Msg(_ecoMsg, Tr("Guardado: vale ya para los servicios que se registren desde ahora."), false);
            LoadEconomy();
        }
    }
}
