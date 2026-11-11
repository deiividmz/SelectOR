// (2026-11-09: ahora en Empresas → Ajustes → Sala compartida; antes era la pestaña «Sala» de Ajustes de SelectOR)
// SALA (1.2.77, solo el administrador): los tiempos del multijugador de la sala compartida de CGL —
// itinerarios, agujas, órdenes a las señales, Tab y trenes que se quedan con la vía—. Se guardan en Supabase
// (sql/plazos-sala.sql: room_timings_get / room_timings_set) y el despachador fijo los lee cada minuto (Plazos.cs del
// despachador): entran sin reiniciar. Lo que queda como de fábrica no se guarda.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SelectOR
{
    public sealed class SalaTiemposPanel : Panel
    {
        // clave · grupo · texto · de fábrica (s) · en minutos (si no, en segundos)
        public static readonly (string key, string group, string label, double def, bool min)[] SalaPlazos =
        {
            ("itin_caduca", "ITINERARIOS", "Un itinerario sin usar caduca a los", 300, true),
            ("itin_anulacion", "ITINERARIOS", "Al anularlo con un tren acercándose, las agujas siguen enclavadas", 120, true),
            ("itin_retencion", "ITINERARIOS", "Retención: tren parado sin usar su itinerario mientras otro espera", 120, true),
            ("itin_cola", "ITINERARIOS", "Tiempo máximo de un itinerario en cola", 900, true),
            ("aguja_mano", "AGUJAS", "Agujas puestas a mano: protegidas frente a otros maquinistas", 180, true),
            ("aguja_jugador", "AGUJAS", "Agujas cambiadas por un jugador: cuentan como su itinerario", 600, true),
            ("orden_parada", "SEÑALES", "Orden de parada (sin jefe de circulación): vuelve sola a automática a los", 600, true),
            ("orden_libre", "SEÑALES", "Orden de anuncio o vía libre (sin jefe de circulación): vuelve a automática a los", 180, true),
            ("tab", "SEÑALES", "Tab: el rebase autorizado (sin jefe de circulación) y la espera al jefe caducan a los", 300, true),
            ("tramo_fuera", "TRENES QUE SE QUEDAN CON LA VÍA", "Tramos cogidos que quedan fuera de la ruta del tren: se sueltan a los", 30, false),
            ("parado_suelta", "TRENES QUE SE QUEDAN CON LA VÍA", "Tren parado que cierra la señal a otro: la cede hasta que arranque a los", 90, false),
        };
        const string SalaRoute = "CGL_V22N_RE";

        Label _lblSala;
        readonly Dictionary<string, RoundedInput> _salaInputs = new();
        bool _salaLoaded;

        public SalaTiemposPanel()
        {
            var page = this; Dock = DockStyle.Fill; BackColor = Theme.Bg; Padding = new Padding(0, Theme.Px(4), 0, Theme.Px(4));
            var info = new Label
            {
                Text = I18n.T("Tiempos del multijugador de la sala compartida de CGL. Solo el administrador. El despachador de la sala los aplica en un minuto, sin reiniciar; los que dejes como de fábrica no se guardan."),
                Dock = DockStyle.Top, Height = Theme.Px(34), ForeColor = Theme.Subtle, BackColor = Theme.Bg, Font = Theme.Font(8.75f)
            };
            // (2026-11-09) en dos columnas (itinerarios y agujas · señales y trenes que se quedan con la vía), filas compactas:
            // así cabe sin barras en Empresas → Ajustes también a 1366×768
            var list = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Theme.Bg, ColumnCount = 2, RowCount = 1 };
            list.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); list.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            list.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var cols = new[] { new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Margin = new Padding(0, 0, Theme.Px(14), 0) }, new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Margin = new Padding(0) } };
            list.Controls.Add(cols[0], 0, 0); list.Controls.Add(cols[1], 1, 0);
            var colOf = new Dictionary<string, int> { ["ITINERARIOS"] = 0, ["AGUJAS"] = 0, ["SEÑALES"] = 1 };
            var ys = new int[2]; string lastGroup = null;
            int lw = Theme.Px(270), iw = Theme.Px(70);
            foreach (var p in SalaPlazos)
            {
                int c = colOf.TryGetValue(p.group, out int cc) ? cc : 1;
                var col = cols[c]; ref int y = ref ys[c];
                if (p.group != lastGroup)
                {
                    lastGroup = p.group;
                    var g = new Label { Text = I18n.T(p.group), Location = new Point(0, y + Theme.Px(4)), AutoSize = false, Size = new Size(Theme.Px(500), Theme.Px(20)), ForeColor = Theme.AccentHi, BackColor = Theme.Bg, Font = Theme.Font(8.5f, FontStyle.Bold) };
                    col.Controls.Add(g); y += Theme.Px(24);
                }
                var lab = new Label { Text = I18n.T(p.label), Location = new Point(0, y), AutoSize = false, Size = new Size(lw, Theme.Px(32)), ForeColor = Theme.Text, BackColor = Theme.Bg, Font = Theme.Font(8.75f), TextAlign = ContentAlignment.MiddleLeft };
                var inp = new RoundedInput("") { Location = new Point(lw + Theme.Px(8), y + Theme.Px(1)), Size = new Size(iw, Theme.Px(30)) };
                inp.Box.Text = Show(p.def, p.min);
                var unit = new Label { Text = p.min ? I18n.T("min") : I18n.T("s"), Location = new Point(lw + Theme.Px(12) + iw, y + Theme.Px(7)), AutoSize = true, ForeColor = Theme.Text, BackColor = Theme.Bg, Font = Theme.Font(9f) };
                var def = new Label { Text = string.Format(I18n.T("de fábrica: {0}"), Show(p.def, p.min) + " " + (p.min ? I18n.T("min") : I18n.T("s"))), Location = new Point(lw + Theme.Px(44) + iw, y + Theme.Px(8)), AutoSize = true, ForeColor = Theme.Subtle, BackColor = Theme.Bg, Font = Theme.Font(8f) };
                col.Controls.Add(lab); col.Controls.Add(inp); col.Controls.Add(unit); col.Controls.Add(def);
                _salaInputs[p.key] = inp;
                y += Theme.Px(33);
            }
            var bar = new Panel { Dock = DockStyle.Bottom, Height = Theme.Px(48), BackColor = Theme.Bg };
            var save = DialogKit.Primary(I18n.T("Guardar"), 140); save.Height = Theme.Px(36);
            var fact = DialogKit.Fit(DialogKit.Secondary(I18n.T("Valores de fábrica")), 160); fact.Height = Theme.Px(36);
            save.Location = new Point(0, Theme.Px(10)); fact.Location = new Point(save.Right + Theme.Px(10), Theme.Px(10));
            _lblSala = new Label { AutoSize = false, Location = new Point(fact.Right + Theme.Px(14), Theme.Px(10)), Size = new Size(Theme.Px(520), Theme.Px(36)), ForeColor = Theme.Subtle, BackColor = Theme.Bg, TextAlign = ContentAlignment.MiddleLeft, Font = Theme.Font(9f) };
            save.Click += async (s, e) => await SalaSave(false);
            fact.Click += async (s, e) =>
            {
                if (ThemedBox.Show(FindForm(), I18n.T("¿Volver a poner todos los tiempos de la sala como de fábrica?"), I18n.T("Sala"), MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                await SalaSave(true);
            };
            bar.Controls.Add(save); bar.Controls.Add(fact); bar.Controls.Add(_lblSala);
            page.Controls.Add(list); page.Controls.Add(bar); page.Controls.Add(info);
            page.VisibleChanged += async (s, e) => { if (page.Visible && !_salaLoaded) await SalaLoad(); };
        }

        static string Show(double seconds, bool min) => (min ? seconds / 60.0 : seconds).ToString("0.##", CultureInfo.CurrentCulture);

        async Task SalaLoad()
        {
            _lblSala.Text = I18n.T("Leyendo los tiempos de la sala…");
            var (json, err) = await Supa.RpcAsync("room_timings_get", new { p_route = SalaRoute });
            if (IsDisposed) return;
            if (err != null) { _lblSala.ForeColor = Color.FromArgb(255, 183, 77); _lblSala.Text = SalaErr(err); return; }
            _salaLoaded = true;
            var vals = new Dictionary<string, double>();
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("vals", out var v) && v.ValueKind == JsonValueKind.Object)
                    foreach (var pr in v.EnumerateObject()) if (pr.Value.ValueKind == JsonValueKind.Number) vals[pr.Name] = pr.Value.GetDouble();
            }
            catch { }
            foreach (var p in SalaPlazos) _salaInputs[p.key].Box.Text = Show(vals.TryGetValue(p.key, out double x) ? x : p.def, p.min);
            int changed = SalaPlazos.Count(p => vals.ContainsKey(p.key) && Math.Abs(vals[p.key] - p.def) > 0.5);
            _lblSala.ForeColor = Theme.Subtle;
            _lblSala.Text = changed == 0 ? I18n.T("Todos los tiempos están como de fábrica.") : string.Format(I18n.T("{0} tiempo(s) cambiados por el administrador."), changed);
        }

        async Task SalaSave(bool factory)
        {
            var vals = new Dictionary<string, double>();
            if (!factory)
                foreach (var p in SalaPlazos)
                {
                    string t = _salaInputs[p.key].Box.Text.Trim().Replace(',', '.');
                    if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || v <= 0)
                    { _lblSala.ForeColor = Color.FromArgb(255, 183, 77); _lblSala.Text = string.Format(I18n.T("Revisa «{0}»: tiene que ser un número."), I18n.T(p.label)); _salaInputs[p.key].Box.Focus(); return; }
                    double s = Math.Round(p.min ? v * 60 : v);
                    if (s < 10 || s > 7200) { _lblSala.ForeColor = Color.FromArgb(255, 183, 77); _lblSala.Text = string.Format(I18n.T("Revisa «{0}»: de 10 s a 2 h."), I18n.T(p.label)); _salaInputs[p.key].Box.Focus(); return; }
                    if (Math.Abs(s - p.def) > 0.5) vals[p.key] = s;   // (los de fábrica no se guardan)
                }
            _lblSala.ForeColor = Theme.Subtle; _lblSala.Text = I18n.T("Guardando…");
            var (_, err) = await Supa.RpcAsync("room_timings_set", new { p_vals = vals, p_route = SalaRoute });
            if (IsDisposed) return;
            if (err != null) { _lblSala.ForeColor = Color.FromArgb(255, 183, 77); _lblSala.Text = SalaErr(err); return; }
            if (factory) foreach (var p in SalaPlazos) _salaInputs[p.key].Box.Text = Show(p.def, p.min);
            _lblSala.ForeColor = Theme.AccentHi;
            _lblSala.Text = vals.Count == 0 ? I18n.T("Guardado: todos como de fábrica. La sala lo aplica en un minuto.") : string.Format(I18n.T("Guardado: {0} tiempo(s) cambiados. La sala lo aplica en un minuto."), vals.Count);
        }

        static string SalaErr(string err) => err.IndexOf("PGRST202", StringComparison.OrdinalIgnoreCase) >= 0 ? I18n.T("El servidor aún no tiene los tiempos de la sala (falta plazos-sala.sql).")
                                           : err.IndexOf("42501", StringComparison.Ordinal) >= 0 || err.IndexOf("Solo el administrador", StringComparison.Ordinal) >= 0 ? I18n.T("Solo el administrador.") : err;
    }
}
