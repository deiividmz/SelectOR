// Empresas → Tablón → «Consejos» (petición 2026-11-08): cómo sacar más beneficio en cada viaje, como el cartel y el anuncio
// de Discord del mismo día. Las cifras no van escritas: salen de los ajustes de las tarifas (app_settings) y del modelo de
// viajeros (PaxCfg y la curva de horas), así que siguen siendo verdad si el administrador cambia algo.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Text.Json;
using System.Windows.Forms;

namespace SelectOR
{
    public partial class MainMenuForm
    {
        Panel _tipsHost;
        TableLayoutPanel _tipsGrid;
        Panel _tipsChart;
        int _tipsProfile;          // la curva de horas que se ve: 0 Cercanías · 1 Media · 2 Larga · 3 Alta Velocidad
        DateTime _tipsLoaded = DateTime.MinValue;

        Panel BuildTipsPage()
        {
            _tipsHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, AutoScroll = true };
            try { Native.UseDarkScrollBars(_tipsHost); } catch { }
            _tipsGrid = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true, BackColor = Theme.Bg, Margin = new Padding(0) };
            _tipsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            _tipsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            void Fit() { _tipsGrid.Width = Math.Max(10, _tipsHost.ClientSize.Width - 4); }
            _tipsHost.Resize += (s, e) => Fit();
            _tipsHost.VisibleChanged += (s, e) => { if (_tipsHost.Visible) Fit(); };
            _tipsHost.Controls.Add(_tipsGrid);
            return _tipsHost;
        }

        async void LoadTips()
        {
            if (_tipsGrid == null) return;
            if (DateTime.UtcNow - _tipsLoaded < TimeSpan.FromMinutes(5) && _tipsGrid.Controls.Count > 0) return;
            try { await LoadPaxModel(); } catch { }   // (el modelo de viajeros de ahora: Ajustes → Viajeros)
            var (json, err) = await Supa.SelectAsync("app_settings?select=*&id=eq.1");
            JsonElement a = default;
            try { if (err == null) { var d = JsonDocument.Parse(json).RootElement; if (d.ValueKind == JsonValueKind.Array && d.GetArrayLength() > 0) a = d[0].Clone(); } } catch { }
            double S(string k, double def) => a.ValueKind == JsonValueKind.Object && a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : def;
            _tipsLoaded = DateTime.UtcNow;
            FillTips(S);
        }

        void FillTips(Func<string, double, double> S)
        {
            var c = I18n.Culture;
            string N(double v, string f = "0.##") => v.ToString(f, c);
            string Pct(double v) => (v * 100).ToString("0", c) + " %";
            string Eur(double v) => v >= 100 ? EurT(v) : v.ToString("0.00", c) + " €";
            _tipsGrid.SuspendLayout();
            _tipsGrid.Controls.Clear();
            _tipsGrid.RowStyles.Clear();

            // ── viajeros
            double capRef = S("pax_cap_ref", 300), capExp = S("pax_cap_exp", 0.5), turn = S("pax_turnover_max", 3);
            double twice = capRef * 2, f2 = capRef > 0 ? Math.Pow(capRef / twice, 1 - Math.Min(1, Math.Max(0, capExp))) : 1;
            var pax = new List<(string, string)>();
            if (capRef > 0) pax.Add(("🚆", string.Format(Tr("**Tren a la medida.** Hasta {0} plazas cobran todos los viajeros; por encima, cada plaza de más rinde menos (con {1} plazas, un {2})."), N(capRef, "0"), N(twice, "0"), Pct(f2))));
            pax.Add(("🚉", string.Format(Tr("**Para en las estaciones.** Cada viajero que sube paga {0} de billete"), Eur(S("fleet_fare_base", 3)))
                           + (turn > 0 ? string.Format(Tr(", y en un servicio cobran hasta {0} veces las plazas del tren."), N(turn)) : ".")));
            double dmax = S("pax_dist_max", 0.3);
            if (dmax > 0) pax.Add(("📏", string.Format(Tr("**Recorridos largos.** Desde {0} km, bonificación por distancia de hasta +{1} a los {2} km."), N(S("pax_dist_from", 40), "0"), Pct(dmax), N(S("pax_dist_full", 250), "0"))));
            pax.Add(("⚡", Tr("**Rápido y cómodo.** La velocidad media y el confort del tren suben el billete.")));
            double occ = S("pax_occ_full", 0.8), over = S("pax_occ_over", 0.3);
            pax.Add(("🧍", string.Format(Tr("**Sin ir a reventar.** Con el tren por encima del {0} de ocupación, los viajeros de pie cuentan al {1}."), Pct(occ), Pct(over))));
            // (2026-11-09) el plus nocturno (sql/plus-nocturno.sql; sus cifras, del modelo de viajeros: Ajustes → Viajeros)
            if (PaxCfg.NightBonusKm > 0)
                pax.Add(("🌙", string.Format(Tr("**De noche, con plus.** Entre las {0}:00 y las {1}:00 (hora del juego) espera poca gente, pero la empresa cobra {2}/km de plus en proporción a las paradas hechas de noche."),
                                             PaxCfg.NightFrom.ToString("00", c), PaxCfg.NightTo.ToString("00", c), Eur(PaxCfg.NightBonusKm))));
            AddTipCard(Tr("VIAJEROS"), Theme.AccentHi, pax, 0);

            // ── costes
            var cost = new List<(string, string)>
            {
                ("⏱️", string.Format(Tr("**El tiempo es salario.** El maquinista cobra {0} + {1}/h de marcha (como mucho {2} h): evita esperas y vueltas sin sentido."),
                                     EurT(S("default_salary_per_service", 100)), EurT(S("salary_per_hour", 30)), N(S("salary_max_hours", 6), "0"))),
                ("🛤️", string.Format(Tr("**Cada km cuesta.** Canon de {0}/km + {1}/t·km y energía de {2}/km por el factor de masa."),
                                     EurT(S("default_canon_per_km", 45)), S("canon_per_t_km", 0.04).ToString("0.00", c) + " €", EurT(S("default_energy_per_km", 40)))),
                ("🚚", string.Format(Tr("**Traslados en vacío.** Pagan el {0} del canon y de la energía, pero no cobran: muévete con viajeros o carga siempre que puedas."), Pct(S("empty_cost_pct", 0.5)))),
                ("⚖️", Tr("**Sin masa de más.** Cada tonelada paga canon y más energía: no lleves vagones o coches que no te hacen falta.")),
                ("💶", Tr("**Compra, no alquiles.** El alquiler a SelectOR se paga en cada servicio; un tren comprado se amortiza con el uso.")),
            };
            AddTipCard(Tr("COSTES"), Carne.Gold, cost, 1);

            // ── demanda (con la curva de horas)
            var pc = PaxCfg;
            string[] prof = { Tr("Cercanías"), Tr("Media Distancia"), Tr("Larga Distancia"), Tr("Alta Velocidad") };
            var dem = new List<(string, string)>
            {
                ("📊", string.Format(Tr("En cada estación espera un {0} de las plazas del tren, multiplicado por todo lo de abajo y con ±{1} al azar."), N(pc.CapacityPct, "0.#") + " %", Pct(pc.Jitter))),
                ("🏙️", string.Format(Tr("**Tamaño de la estación** (por los metros de andén): apeadero ×{0}, normal ×{1}, grande ×{2}, muy grande ×{3}."),
                                     N(pc.StationWeights[0]), N(pc.StationWeights[1]), N(pc.StationWeights[2]), N(pc.StationWeights[3]))),
                ("🚄", string.Format(Tr("**Tipo de servicio** (por la velocidad máxima del tren): {0} ×{1}, {2} ×{3}, {4} ×{5}, {6} ×{7}."),
                                     prof[0], N(pc.ProfileDemand[0]), prof[1], N(pc.ProfileDemand[1]), prof[2], N(pc.ProfileDemand[2]), prof[3], N(pc.ProfileDemand[3]))),
                ("🔄", string.Format(Tr("**Rotación.** En cada parada baja un {0} en Cercanías, {1} en Media, {2} en Larga y {3} en Alta Velocidad (más en las estaciones grandes): el tren se vacía y se vuelve a llenar."),
                                     Pct(pc.ProfileAlight[0]), Pct(pc.ProfileAlight[1]), Pct(pc.ProfileAlight[2]), Pct(pc.ProfileAlight[3]))),
                ("☀️", string.Format(Tr("**Verano:** {0} ×{1}, {2} ×{3}, {4} ×{5}, {6} ×{7}."),
                                     prof[0], N(PaxSeasonFactor(PaxProfile.Cercanias, 1)), prof[1], N(PaxSeasonFactor(PaxProfile.Media, 1)), prof[2], N(PaxSeasonFactor(PaxProfile.Larga, 1)), prof[3], N(PaxSeasonFactor(PaxProfile.AV, 1)))),
                ("🌧️", string.Format(Tr("**Clima:** con nieve, todos ×{0}; con lluvia, Cercanías ×{1} y Media ×{2}."), N(pc.SnowFactor), N(pc.RainCercanias), N(pc.RainMedia))),
                ("ℹ️", Tr("En Actividad no cuentan la estación del año ni el clima; en Conducción libre y Horarios, los que elijas al empezar.")),
            };
            var dc = AddTipCard(Tr("DEMANDA · CUÁNTOS VIAJEROS ESPERAN"), Color.FromArgb(96, 165, 250), dem, 0, span: true, withChart: true);

            // ── mercancías, sala y liga
            double fmax = S("fre_dist_max", 1.5);
            AddTipCard(Tr("MERCANCÍAS"), Color.FromArgb(251, 146, 60), new List<(string, string)>
            {
                ("📦", string.Format(Tr("**{0}/km**, y más cuanto más carga lleves (hasta {1} t)."), EurT(S("default_income_per_km", 200)), N(S("fre_mass_cap", 2000), "N0"))),
                ("📏", fmax > 1 ? string.Format(Tr("Desde {0} km, bonificación por distancia de hasta +{1} a los {2} km."), N(S("fre_dist_from", 40), "0"), Pct(fmax - 1), N(S("fre_dist_full", 300), "0"))
                                : Tr("Los recorridos largos reparten mejor los costes fijos del servicio.")),
            }, 0);
            AddTipCard(Tr("SALA COMPARTIDA Y LIGA"), Color.FromArgb(45, 212, 191), new List<(string, string)>
            {
                ("🤝", string.Format(Tr("Haz el servicio entero en la sala: +{0} del beneficio y +{1} más si hay jefe de circulación."), N(S("room_bonus_pct", 15), "0") + " %", N(S("room_jefe_pct", 5), "0") + " %")),
                ("🔥", string.Format(Tr("{0} al llegar a {1} servicios en la sala en la semana (sin infracciones graves)."), EurT(S("room_streak_bonus", 1000)), N(S("room_streak_n", 5), "0"))),
                ("🏆", Tr("El límite de viajeros es solo para el pago: en la Liga cuentan todos los que lleva el tren.")),
                ("📅", string.Format(Tr("En la Liga cuentan hasta {0} servicios por maquinista y día."), N(S("league_daily_cap", 8), "0"))),
            }, 1);
            _tipsGrid.Controls.Add(new Label { Text = Tr("El desglose de cada servicio, con todas sus cifras, en «¿Cómo se calcula?» del resultado."), AutoSize = true, ForeColor = Theme.Subtle, Font = Theme.Font(9f), Margin = new Padding(4, 6, 4, 10) }, 0, _tipsGrid.RowCount);
            _tipsGrid.SetColumnSpan(_tipsGrid.Controls[_tipsGrid.Controls.Count - 1], 2);
            _tipsGrid.ResumeLayout();
        }

        int _tipsCol, _tipsRow;
        // una tarjeta con su título y sus consejos («**negrita.** resto»). col 0 empieza fila nueva; span: las dos columnas
        Control AddTipCard(string title, Color accent, List<(string icon, string text)> items, int col, bool span = false, bool withChart = false)
        {
            if (col == 0 || span) { _tipsRow = _tipsGrid.RowCount; _tipsGrid.RowCount++; _tipsGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize)); }
            var card = new Card { Dock = DockStyle.Fill, Fill = Theme.Surface, Radius = 12, Margin = new Padding(col == 0 ? 0 : 7, 0, col == 0 && !span ? 7 : 0, 12), Padding = new Padding(16, 12, 16, 12), AutoSize = true };
            // (2026-11-08) todo con el color de la tarjeta: con «Transparent» los iconos salían sobre un recuadro del color de la ventana
            var bg = Theme.Surface;
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, AutoSize = true, BackColor = bg };
            t.Controls.Add(new Label { Text = title, AutoSize = true, ForeColor = accent, Font = Theme.Font(9f, FontStyle.Bold), BackColor = bg, Margin = new Padding(0, 0, 0, 6) });
            if (withChart) t.Controls.Add(BuildTipsChart());
            foreach (var (icon, text) in items)
            {
                var row = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, BackColor = bg, Margin = new Padding(0, 4, 0, 4), Dock = DockStyle.Top };
                row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.Px(30)));
                row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                row.Controls.Add(new EmojiLabel { Text = icon, AutoSize = true, Font = Theme.Font(12f), BackColor = bg, ForeColor = Theme.Text, Margin = new Padding(0) }, 0, 0);
                row.Controls.Add(TipText(text), 1, 0);
                t.Controls.Add(row);
            }
            card.Controls.Add(t);
            _tipsGrid.Controls.Add(card, span ? 0 : col, _tipsRow);
            if (span) _tipsGrid.SetColumnSpan(card, 2);
            return card;
        }

        // «**negrita.** resto»: la parte entre ** en negrita y el resto normal, como un párrafo que se ajusta al ancho
        Control TipText(string s)
        {
            string bold = "", rest = s;
            if (s.StartsWith("**")) { int k = s.IndexOf("**", 2); if (k > 2) { bold = s.Substring(2, k - 2); rest = s.Substring(k + 2); } }
            var host = new Panel { BackColor = Theme.Surface, Dock = DockStyle.Top, Margin = new Padding(0, 2, 0, 0), Height = Theme.Px(20) };
            host.Paint += (o, e) =>
            {
                using var fb = Theme.Font(9.75f, FontStyle.Bold); using var fr = Theme.Font(9.75f);
                DrawRich(e.Graphics, host.ClientSize.Width, bold, rest, fb, fr, out _);
            };
            host.Resize += (o, e) =>
            {
                if (host.ClientSize.Width < 20) return;
                using var g = host.CreateGraphics(); using var fb = Theme.Font(9.75f, FontStyle.Bold); using var fr = Theme.Font(9.75f);
                DrawRich(g, host.ClientSize.Width, bold, rest, fb, fr, out int h, measureOnly: true);
                if (host.Height != h) host.Height = h;
                host.Invalidate();
            };
            return host;
        }

        // texto con un trozo en negrita al principio, por palabras, ajustado a «width»
        static void DrawRich(Graphics g, int width, string bold, string rest, Font fb, Font fr, out int height, bool measureOnly = false)
        {
            var words = new List<(string w, Font f)>();
            foreach (var w in bold.Split(' ')) if (w.Length > 0) words.Add((w, fb));
            foreach (var w in rest.Split(' ')) if (w.Length > 0) words.Add((w, fr));
            const TextFormatFlags TF = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
            int x = 0, y = 0, lineH = TextRenderer.MeasureText(g, "Ag", fr, Size.Empty, TF).Height + 2, space = TextRenderer.MeasureText(g, " ", fr, Size.Empty, TF).Width;
            space = Math.Max(space, 4);
            foreach (var (w, f) in words)
            {
                int ww = TextRenderer.MeasureText(g, w, f, Size.Empty, TF).Width;
                if (x > 0 && ",.;:)".IndexOf(w[0]) >= 0) x -= space;   // («200 €/km, y…»: sin espacio antes de la coma)
                if (x > 0 && x + ww > width) { x = 0; y += lineH; }
                if (!measureOnly) TextRenderer.DrawText(g, w, f, new Point(x, y), Theme.Text, TF);
                x += ww + space;
            }
            height = y + lineH + 2;
        }

        // la demanda a cada hora del simulador, con los cuatro tipos de servicio a elegir
        Control BuildTipsChart()
        {
            var box = new Panel { Dock = DockStyle.Top, Height = Theme.Px(170), BackColor = Theme.Surface, Margin = new Padding(0, 0, 0, 8) };
            string[] names = { Tr("Cercanías"), Tr("Media Distancia"), Tr("Larga Distancia"), Tr("Alta Velocidad") };
            var chips = new List<Rectangle>();
            box.Paint += (s, e) =>
            {
                var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
                using var f = Theme.Font(8.5f, FontStyle.Bold); using var fs = Theme.Font(8f);
                chips.Clear();
                int x = 0;
                for (int i = 0; i < 4; i++)
                {
                    var sz = TextRenderer.MeasureText(g, names[i], f);
                    var r = new Rectangle(x, 0, sz.Width + 18, Theme.Px(24));
                    using (var p = Theme.Round(r, 10)) using (var b = new SolidBrush(i == _tipsProfile ? Color.FromArgb(96, 165, 250) : Theme.Surface2)) g.FillPath(b, p);
                    TextRenderer.DrawText(g, names[i], f, r, i == _tipsProfile ? Color.FromArgb(15, 20, 30) : Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                    chips.Add(r); x = r.Right + 6;
                }
                var curve = PaxHourCurve[_tipsProfile];
                double mx = Math.Max(0.1, curve.Max());
                int top = Theme.Px(34), bottom = box.Height - Theme.Px(18), H = bottom - top, W = box.Width;
                float bw = W / 24f;
                for (int h = 0; h < 24; h++)
                {
                    float bh = Math.Max(3f, (float)(curve[h] / mx * H));   // (las de madrugada, por bajas que sean, se ven)
                    var r = new RectangleF(h * bw + 1, bottom - bh, Math.Max(2, bw - 3), bh);
                    using var b = new SolidBrush(curve[h] >= mx * 0.85 ? Color.FromArgb(245, 197, 66) : Color.FromArgb(96, 165, 250));
                    if (bh < 8) g.FillRectangle(b, r); else using (var p = Theme.Round(Rectangle.Round(r), 3)) g.FillPath(b, p);
                    if (h % 6 == 0 || h == 23) TextRenderer.DrawText(g, h + " h", fs, new Point((int)(h * bw), bottom + 2), Theme.Subtle, TextFormatFlags.NoPrefix);
                }
            };
            box.MouseClick += (s, e) => { for (int i = 0; i < chips.Count; i++) if (chips[i].Contains(e.Location)) { _tipsProfile = i; box.Invalidate(); } };
            box.Cursor = Cursors.Hand;
            box.Resize += (s, e) => box.Invalidate();
            return box;
        }
    }
}
