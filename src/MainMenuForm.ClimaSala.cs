// CLIMA DE LA SALA COMPARTIDA (1.2.74). El administrador elige la estación y el tiempo de la sala de CGL con el botón ⛅
// junto a «Conducir en la sala compartida» (solo lo ve él). Se guarda en Supabase (room_weather: sql/clima-sala.sql) y el
// servidor de la sala lo lee cada 10 s (SelectOR Servidor MP/Weather.cs) y se lo pasa al despachador, que lo reparte:
//  · nubes, lluvia o nieve y niebla: al momento, para todos los que están dentro;
//  · la estación: a quien entra (Open Rails carga el paisaje de la estación al empezar; por eso SelectOR lanza Open Rails
//    ya con la de la sala, que el servidor dice en «SELECTOR ROOM»: MainMenuForm.SalaCgl.cs).
// Estación «Automática» = la de España según el calendario (cambia sola el día que toca).
// Todos ven el tiempo de la sala debajo de la casilla («Otoño, lluvia»).

using System;
using System.Drawing;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SelectOR
{
    public partial class MainMenuForm
    {
        int _salaSeason = -1, _salaWeather = -1;   // lo que dice la sala (−1: no se sabe)
        int _salaOffset;                           // la hora de la sala: minutos de adelanto o retraso sobre la de España
        Label _salaWxBtn;

        static readonly string[] SalaSeasonNames = { "Primavera", "Verano", "Otoño", "Invierno" };
        // en Open Rails: 0 despejado, 1 nieve, 2 lluvia
        static readonly string[] SalaWeatherNames = { "despejado", "nieve", "lluvia" };

        // (SalaSubText) «hora de España», «hora de España +3 h»
        string SalaClockText()
            => _salaOffset == 0 ? Tr("hora de España")
               : string.Format(Tr("hora de España {0} h"), (_salaOffset > 0 ? "+" : "−") + (Math.Abs(_salaOffset) / 60.0).ToString("0.#", System.Globalization.CultureInfo.CurrentCulture));

        // (SalaSubText) «Otoño, lluvia»
        string SalaWeatherText()
        {
            if (_salaSeason < 0 || _salaSeason > 3) return "";
            string w = _salaWeather >= 0 && _salaWeather <= 2 ? Tr(SalaWeatherNames[_salaWeather]) : null;
            return Tr(SalaSeasonNames[_salaSeason]) + (w != null ? ", " + w : "");
        }

        // (BuildSalaCheck) el botón ⛅, a la derecha de la casilla; solo para el administrador
        void BuildSalaWeatherButton()
        {
            // (1.2.77) el icono en color, como los demás de SelectOR (EmojiIcon)
            _salaWxBtn = new EmojiIcon { Text = "⛅", Size = new Size(30, 30),
                                     ForeColor = Theme.Subtle, BackColor = Theme.Surface, Font = Theme.Font(13f), Cursor = Cursors.Hand, Visible = false };
            _salaWxBtn.Click += async (s, e) => await EditSalaWeather();
            new ToolTip().SetToolTip(_salaWxBtn, Tr("Clima de la sala (administrador)"));
            Action place = () => _salaWxBtn.Location = new Point(_salaHost.ClientSize.Width - _salaWxBtn.Width - 4, Math.Max(0, (_salaHost.ClientSize.Height - _salaWxBtn.Height) / 2));
            _salaHost.Resize += (s, e) => place();
            _salaHost.HandleCreated += (s, e) => place();
            _salaHost.Controls.Add(_salaWxBtn);
            _salaWxBtn.BringToFront();
        }

        // (SalaCheckUpdate) a la vista solo para el administrador (la sesión puede llegar después)
        void SalaWeatherButtonUpdate()
        {
            if (_salaWxBtn == null || _salaHost == null) return;
            bool admin = Supa.IsSuperadmin && Supa.IsLoggedIn;
            if (_salaWxBtn.Visible == admin) return;
            _salaWxBtn.Visible = admin;
            if (_salaAiBtn != null) _salaAiBtn.Visible = admin;   // (1.2.77) 🚆 el horario de los trenes IA
            _salaHost.Width = admin ? 394 : 326;   // (con el 🖥 del puesto de mando)
        }

        async Task EditSalaWeather()
        {
            string title = Tr("Clima de la sala");
            if (!Supa.IsSuperadmin) { ThemedBox.Show(this, Tr("Solo el administrador."), title, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            // lo de ahora
            int season = -1, weather = 0, clouds = 10, precip = 60, vis = 40000, offset = 0;
            var (json, err) = await Supa.RpcAsync("room_weather_get", new { });
            if (err != null)
            {
                ThemedBox.Show(this, err.IndexOf("PGRST202", StringComparison.OrdinalIgnoreCase) >= 0
                    ? Tr("El servidor aún no tiene el clima de la sala (falta clima-sala.sql).") : err, title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            try
            {
                using var doc = JsonDocument.Parse(json);
                var r = doc.RootElement;
                int I(string k, int def) => r.ValueKind == JsonValueKind.Object && r.TryGetProperty(k, out var v) && v.TryGetInt32(out int x) ? x : def;
                season = I("season", -1); weather = I("weather", 0); clouds = I("clouds", 10); vis = I("visibility", 40000); offset = I("time_offset", 0);
                int p = I("precip", 0); if (p > 0) precip = p;
            }
            catch { }

            // las opciones (los valores que se guardan)
            string[] seasons = { Tr("Automática (la de España)"), Tr("Primavera"), Tr("Verano"), Tr("Otoño"), Tr("Invierno") };
            string[] types = { Tr("Despejado"), Tr("Lluvia"), Tr("Nieve") };
            int[] typeOf = { 0, 2, 1 };   // (en Open Rails: 0 despejado, 1 nieve, 2 lluvia)
            string[] cloudNames = { Tr("Sin nubes"), Tr("Algunas nubes"), Tr("Nuboso"), Tr("Cubierto") };
            int[] cloudVals = { 10, 35, 65, 100 };
            string[] precipNames = { Tr("Ligera"), Tr("Moderada"), Tr("Fuerte") };
            int[] precipVals = { 30, 60, 100 };
            string[] visNames = { Tr("Buena (40 km)"), Tr("Calima (10 km)"), Tr("Neblina (3 km)"), Tr("Niebla (1 km)"), Tr("Niebla densa (300 m)") };
            int[] visVals = { 40000, 10000, 3000, 1000, 300 };
            // la hora: de 12 h de retraso a 12 h de adelanto, de hora en hora
            var offNames = new System.Collections.Generic.List<string>(); var offVals = new System.Collections.Generic.List<int>();
            for (int hh = -12; hh <= 12; hh++)
            {
                offVals.Add(hh * 60);
                offNames.Add(hh == 0 ? Tr("La de España") : string.Format(Tr("{0} h sobre la de España"), (hh > 0 ? "+" : "−") + Math.Abs(hh)));
            }
            int Near(int[] vals, int v) { int best = 0; for (int i = 1; i < vals.Length; i++) if (Math.Abs(vals[i] - v) < Math.Abs(vals[best] - v)) best = i; return best; }

            using var dlg = new FormDialog(title, Tr("Guardar"), 480, "⛅", Tr("Para todos los de la sala compartida de CGL"));
            dlg.AddCombo("season", Tr("Estación"), seasons, Math.Max(0, Math.Min(4, season + 1)));
            dlg.AddCombo("type", Tr("Tiempo"), types, Array.IndexOf(typeOf, Math.Max(0, Math.Min(2, weather))));
            dlg.AddCombo("clouds", Tr("Nubes"), cloudNames, Near(cloudVals, clouds));
            dlg.AddCombo("precip", Tr("Lluvia o nieve"), precipNames, Near(precipVals, precip));
            dlg.AddCombo("vis", Tr("Visibilidad"), visNames, Near(visVals, vis));
            dlg.AddCombo("clock", Tr("Hora"), offNames, Near(offVals.ToArray(), offset));
            dlg.AddInfo(Tr("Las nubes, la lluvia o la nieve, la niebla y la hora cambian para todos los que están dentro (en unos 10 s; la hora, con SelectOR 1.2.74). La estación la ve cada uno al entrar en la sala: quien ya está dentro, la próxima vez que entre."));
            var precipBox = dlg.Field("precip") as ComboBox;
            var typeBox = dlg.Field("type") as ComboBox;
            if (precipBox != null && typeBox != null)
            {
                precipBox.Enabled = typeBox.SelectedIndex > 0;
                typeBox.SelectedIndexChanged += (s, e) => precipBox.Enabled = typeBox.SelectedIndex > 0;
            }
            dlg.Validate = async d =>
            {
                int ns = d.Index("season") - 1, nt = typeOf[Math.Max(0, d.Index("type"))];
                var (_, e2) = await Supa.RpcAsync("room_weather_set", new
                {
                    p_season = ns, p_weather = nt, p_clouds = cloudVals[Math.Max(0, d.Index("clouds"))],
                    p_precip = nt == 0 ? 0 : precipVals[Math.Max(0, d.Index("precip"))], p_visibility = visVals[Math.Max(0, d.Index("vis"))],
                    p_time_offset = offVals[Math.Max(0, d.Index("clock"))]
                });
                if (e2 != null) return e2.IndexOf("PGRST202", StringComparison.OrdinalIgnoreCase) >= 0 ? Tr("El servidor aún no tiene el clima de la sala (falta clima-sala.sql).") : e2;
                // a la vista ya (la sala lo dirá igual en la próxima consulta)
                _salaSeason = ns >= 0 ? ns : SpainNow().season; _salaWeather = nt; _salaOffset = offVals[Math.Max(0, d.Index("clock"))];
                if (_salaSub != null) _salaSub.Text = SalaSubText();
                return null;
            };
            dlg.ShowDialog(this);
        }
    }
}
