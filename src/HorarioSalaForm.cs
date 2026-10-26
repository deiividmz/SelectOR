// HORARIO DE LA SALA (1.2.77): los trenes IA de la sala compartida de CGL, solo para el administrador (botón 🚆 junto a
// ⛅). Se guarda en Supabase (sql/horario-sala.sql: room_ai_services y room_ai_settings) y el despachador fijo de la sala
// lo lee al arrancar y cada minuto (SelectOR Despachador/HorarioSala.cs): los cambios entran sin reiniciar; los trenes
// que ya circulan terminan su viaje y las salidas nuevas siguen el horario nuevo.
//  · arriba, los ajustes: trenes IA activados o no, cuántos como mucho a la vez y la densidad por franja horaria;
//  · los servicios en tarjetas (HorarioSalaForm.Cards.cs; añadir, editar, quitar; doble clic = editar);
//  · «Probar camino»: si hay vía entre las dos estaciones y cuántos km (aproximado: por la red de la ruta, sin mirar por
//    qué lado se puede pasar cada aguja; el despachador busca el camino de verdad al crear el tren).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SelectOR
{
    public sealed partial class HorarioSalaForm : Form
    {
        public sealed class Service
        {
            public long Id; public string Name = "", Kind = "cercanias", Origin = "", Destination = "", Consist = "", First = "07:00", Last = "23:00", Stops = "todas";
            public int Every = 30; public bool Active = true;
            public string Hint = "";   // (1.2.79) el recorrido marcado en el mapa: «o=nodo;d=nodo;v=nodo,nodo» (HorarioRecorridoForm)
        }

        public static readonly string[] Kinds = { "cercanias", "media", "larga", "mercancias" };   // (media: sql/horario-media.sql)
        public static readonly string[] FranjaNames = { "0–6 h", "6–10 h", "10–17 h", "17–21 h", "21–24 h" };
        static readonly int[] DensityVals = { 0, 25, 50, 75, 100, 150, 200 };
        static readonly int[] MaxVals = { 0, 2, 4, 6, 8, 10, 12, 15, 20, 25, 30, 40 };
        static string Tr(string s) => I18n.T(s);
        static string KindName(string k) => k == "larga" ? Tr("Larga distancia") : k == "media" ? Tr("Media distancia") : k == "mercancias" ? Tr("Mercancías") : Tr("Cercanías");

        readonly Func<Task<RouteGraph>> _graph;
        readonly string _routeDir, _route;
        readonly ServiceCards _cards;   // (1.2.78) tarjetas en vez de tabla
        readonly ThemeCheck _enabled;
        readonly ComboBox _max;
        readonly ComboBox[] _density = new ComboBox[5];
        readonly Label _status;
        List<Service> _services = new();
        List<string> _stations, _consists;
        RouteGraph _g;
        public Func<string, object, Task<(string json, string err)>> Rpc = Supa.RpcAsync;   // (las pruebas la cambian)
        public Func<string, Task<string>> RetireAi;   // (1.2.78) retirar ya los trenes IA (null = todos; o un servicio): null si fue bien

        public HorarioSalaForm(string routeDir, Func<Task<RouteGraph>> graph, string route = "CGL_V22N_RE",
                               Func<Task<RouteMapData>> mapData = null, Func<List<(string name, double lat, double lon, double kmh)>> aiTrains = null)
        {
            _routeDir = routeDir; _graph = graph; _route = route; _mapData = mapData; _aiTrains = aiTrains;
            Text = Tr("Horario de la sala");
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(Theme.Px(1480), Theme.Px(780)); MinimumSize = new Size(Theme.Px(1100), Theme.Px(540));   // (1.2.77) con el mapa a la derecha
            DialogKit.Dress(this);
            Font = Theme.Font(9.5f);

            var head = new DialogKit.Header(Tr("Horario de la sala"), Tr("Trenes IA de la sala compartida de CGL · solo el administrador"), "🚆") { Dock = DockStyle.Top };

            // ajustes
            var top = new Panel { Dock = DockStyle.Top, Height = Theme.Px(78), BackColor = Theme.Surface, Padding = new Padding(Theme.Px(16), Theme.Px(10), Theme.Px(16), Theme.Px(6)) };
            _enabled = new ThemeCheck { Text = Tr("Trenes IA activados"), ForeColor = Theme.Text, BackColor = Theme.Surface, AutoSize = false, Size = new Size(Theme.Px(200), Theme.Px(26)), Location = new Point(Theme.Px(16), Theme.Px(10)) };
            var maxLab = new Label { Text = Tr("Como mucho a la vez"), AutoSize = true, ForeColor = Theme.Subtle, BackColor = Theme.Surface, Location = new Point(Theme.Px(16), Theme.Px(44)) };
            _max = DialogKit.Combo(); _max.Width = Theme.Px(76); _max.Location = new Point(Theme.Px(150), Theme.Px(40));
            foreach (int v in MaxVals) _max.Items.Add(v.ToString(CultureInfo.InvariantCulture));
            top.Controls.Add(_enabled); top.Controls.Add(maxLab); top.Controls.Add(_max);
            var dLab = new Label { Text = Tr("Densidad por franja (salidas del horario)"), AutoSize = true, ForeColor = Theme.Subtle, BackColor = Theme.Surface, Location = new Point(Theme.Px(250), Theme.Px(12)) };
            top.Controls.Add(dLab);
            for (int k = 0; k < 5; k++)
            {
                int x = Theme.Px(250 + k * 146);
                top.Controls.Add(new Label { Text = FranjaNames[k], AutoSize = true, ForeColor = Theme.Text, BackColor = Theme.Surface, Location = new Point(x, Theme.Px(44)) });
                var c = DialogKit.Combo(); c.Width = Theme.Px(90); c.Location = new Point(x + Theme.Px(52), Theme.Px(40));
                foreach (int v in DensityVals) c.Items.Add(v + " %");
                _density[k] = c; top.Controls.Add(c);
            }
            var saveSet = DialogKit.Secondary(Tr("Guardar ajustes"), 150);
            saveSet.Anchor = AnchorStyles.Top | AnchorStyles.Right; saveSet.Location = new Point(top.Width - saveSet.Width - Theme.Px(16), Theme.Px(22));
            top.Resize += (s, e) => saveSet.Left = top.ClientSize.Width - saveSet.Width - Theme.Px(16);
            saveSet.Click += async (s, e) => await SaveSettings();
            top.Controls.Add(saveSet);

            _cards = new ServiceCards { Dock = DockStyle.Fill, ColorOf = ColorOf, KindName = KindName };
            _cards.Open += async s => await Edit(s);
            var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(Theme.Px(16), Theme.Px(10), Theme.Px(16), Theme.Px(6)), BackColor = Theme.Bg };
            body.Controls.Add(_cards);
            BuildMap(body);   // (1.2.77) el mapa de la ruta con el recorrido de cada servicio (HorarioSalaForm.Mapa.cs)

            var foot = new DialogKit.Footer();
            var add = DialogKit.Primary(Tr("Añadir servicio"), 160);
            var edit = DialogKit.Secondary(Tr("Editar"), 110);
            var del = DialogKit.Danger(Tr("Quitar"), 110);
            var test = DialogKit.Secondary(Tr("Probar camino"), 140);
            var retire = DialogKit.Danger(Tr("Retirar trenes IA…"), 170);   // (1.2.78)
            var routeBtn = DialogKit.Secondary(Tr("Recorrido…"), 120);       // (1.2.79) vías de salida, de llegada y de paso, en el mapa
            routeBtn.Click += async (s, e) => await EditRoute(_cards.Selected);
            retire.Click += (s, e) => RetireMenu(retire);
            var close = DialogKit.Secondary(Tr("Cerrar"), 110);
            add.Click += async (s, e) => await Edit(null);
            edit.Click += async (s, e) => { if (_cards.Selected == null) Pick(); else await Edit(_cards.Selected); };
            del.Click += async (s, e) => await Delete(_cards.Selected);
            test.Click += async (s, e) => await TestPath(_cards.Selected);
            close.Click += (s, e) => Close();
            foot.AddRight(close, test, routeBtn, del, edit, add, retire);
            _status = new Label { AutoSize = false, Width = Theme.Px(160), ForeColor = Theme.Subtle, BackColor = foot.BackColor, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(Theme.Px(16), 0, 0, 0) };
            foot.AddLeft(_status);

            Controls.Add(body); Controls.Add(top); Controls.Add(head); Controls.Add(foot);
            Shown += async (s, e) => { _ = LoadMapAsync(); await Reload(); };
        }

        // ── leer
        public async Task Reload()
        {
            _status.Text = Tr("Leyendo el horario…");
            var (json, err) = await Rpc("room_ai_get", new { p_route = _route });
            if (IsDisposed) return;
            if (err != null)
            {
                _status.Text = "";
                ThemedBox.Show(this, err.IndexOf("PGRST202", StringComparison.OrdinalIgnoreCase) >= 0 ? Tr("El servidor aún no tiene el horario de la sala (falta horario-sala.sql).") : err,
                               Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            LoadJson(json);
            _status.Text = string.Format(Tr("{0} servicios"), _services.Count);
        }

        public void LoadJson(string json)
        {
            bool enabled = false; int max = 12; var dens = new[] { 100, 100, 100, 100, 100 };
            var list = new List<Service>();
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
                var r = doc.RootElement;
                if (r.ValueKind == JsonValueKind.Object)
                {
                    if (r.TryGetProperty("enabled", out var en) && (en.ValueKind == JsonValueKind.True || en.ValueKind == JsonValueKind.False)) enabled = en.GetBoolean();
                    if (r.TryGetProperty("max_trains", out var mx) && mx.TryGetInt32(out int m)) max = m;
                    if (r.TryGetProperty("density", out var de) && de.ValueKind == JsonValueKind.Array)
                    { int i = 0; foreach (var x in de.EnumerateArray()) { if (i < 5 && x.TryGetInt32(out int d)) dens[i] = d; i++; } }
                    if (r.TryGetProperty("services", out var sv) && sv.ValueKind == JsonValueKind.Array)
                        foreach (var x in sv.EnumerateArray())
                        {
                            string S(string k) => x.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";
                            list.Add(new Service
                            {
                                Id = x.TryGetProperty("id", out var id) && id.TryGetInt64(out long l) ? l : 0, Name = S("name"), Kind = S("kind"), Origin = S("origin"), Destination = S("destination"),
                                Consist = S("consist"), First = S("first_time"), Last = S("last_time"), Stops = S("stops"),
                                Every = x.TryGetProperty("every_min", out var ev) && ev.TryGetInt32(out int e) ? e : 0,
                                Active = !x.TryGetProperty("active", out var ac) || ac.ValueKind != JsonValueKind.False,
                                Hint = S("route_hint") ?? ""
                            });
                        }
                }
            }
            catch { }
            _services = list;
            _enabled.Checked = enabled;
            _max.SelectedIndex = Near(MaxVals, max);
            for (int k = 0; k < 5; k++) _density[k].SelectedIndex = Near(DensityVals, dens[k]);
            Fill();
            ServicesChanged();   // (el mapa: recorridos otra vez)
        }

        static int Near(int[] vals, int v) { int best = 0; for (int i = 1; i < vals.Length; i++) if (Math.Abs(vals[i] - v) < Math.Abs(vals[best] - v)) best = i; return best; }

        void Fill()
        {
            _cards.EmptyText = Tr("Aún no hay servicios: «Añadir servicio».");
            _cards.SetItems(_services);
        }

        void Pick() => ThemedBox.Show(this, Tr("Elige un servicio de la lista."), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);

        // ── ajustes
        async Task SaveSettings()
        {
            var dens = Enumerable.Range(0, 5).Select(k => DensityVals[Math.Max(0, _density[k].SelectedIndex)]).ToArray();
            var (_, err) = await Rpc("room_ai_settings_set", new { p_enabled = _enabled.Checked, p_max_trains = MaxVals[Math.Max(0, _max.SelectedIndex)], p_density = dens });
            if (IsDisposed) return;
            if (err != null) { ThemedBox.Show(this, err, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            _status.Text = Tr("Ajustes guardados: la sala los aplica en un minuto.");
        }

        // ── (1.2.79) el recorrido en el mapa: vía de salida, de llegada y por dónde pasar
        async Task EditRoute(Service s)
        {
            if (s == null) { Pick(); return; }
            await EnsureLists();
            if (_g == null || _map?.Data == null) { ThemedBox.Show(this, Tr("El mapa de la ruta aún no está listo."), Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            using var f = new HorarioRecorridoForm(s.Name, s.Origin, s.Destination, s.Hint, _g, _map.Data);
            if (f.ShowDialog(this) != DialogResult.OK) return;
            var (_, err) = await Rpc("room_ai_service_route", new { p_id = s.Id, p_route_hint = f.Hint });
            if (IsDisposed) return;
            if (err != null)
            {
                ThemedBox.Show(this, err.IndexOf("PGRST202", StringComparison.OrdinalIgnoreCase) >= 0 ? Tr("El servidor aún no guarda el recorrido de los servicios (falta horario-recorrido.sql).") : err,
                               Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            _status.Text = Tr("Recorrido guardado: la sala lo aplica en un minuto.");
            await Reload();
        }

        // ── (1.2.78) retirar ya los trenes IA de la sala: todos o los del servicio elegido
        void RetireMenu(Control at)
        {
            if (RetireAi == null) return;
            var m = MenuStyle.Apply(new ContextMenuStrip());
            int live = _ai.Count;
            m.Items.Add(new ToolStripMenuItem(live > 0 ? string.Format(Tr("Todos los trenes IA ({0} circulando)"), live) : Tr("Todos los trenes IA"), null, async (o, e) => await Retire(null)));
            var s = _cards.Selected;
            if (s != null)
            {
                int mine = _ai.Count(t => ServiceIndexOf(t.name) == _services.IndexOf(s));
                m.Items.Add(new ToolStripMenuItem(string.Format(Tr("Solo los de «{0}» ({1} circulando)"), s.Name, mine), null, async (o, e) => await Retire(s.Name)));
            }
            else m.Items.Add(new ToolStripMenuItem(Tr("(elige un servicio para retirar solo los suyos)")) { Enabled = false });
            m.Show(at, new Point(0, -m.PreferredSize.Height));
        }

        async Task Retire(string service)
        {
            string what = service == null ? Tr("todos los trenes IA de la sala") : string.Format(Tr("los trenes IA de «{0}»"), service);
            if (ThemedBox.Show(this, string.Format(Tr("¿Retirar ya {0}? Desaparecen al momento, también los que están circulando.\n\nLas salidas siguientes del horario siguen: para que no salgan más, desactiva el servicio o los trenes IA."), what),
                               Tr("Retirar trenes IA"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            _status.Text = Tr("Retirando…");
            string err = await RetireAi(service);
            if (IsDisposed) return;
            if (err != null) { _status.Text = ""; ThemedBox.Show(this, err, Tr("Retirar trenes IA"), MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            _status.Text = Tr("Retirados: desaparecen de la sala en unos segundos.");
        }

        // ── listas de la ruta (estaciones y apartaderos con nombre; composiciones de TRAINS\CONSISTS)
        async Task EnsureLists()
        {
            if (_stations != null) return;
            try { _g = await _graph(); } catch { _g = null; }
            var names = new SortedSet<string>(StringComparer.CurrentCultureIgnoreCase);
            if (_g != null)
                foreach (var E in _g.Edges)
                {
                    foreach (var (_, n) in E.Platforms) if (!string.IsNullOrWhiteSpace(n)) names.Add(n.Trim());
                    foreach (var (_, n) in E.Sidings) if (!string.IsNullOrWhiteSpace(n)) names.Add(n.Trim());
                }
            _stations = names.ToList();
            var cons = new SortedSet<string>(StringComparer.CurrentCultureIgnoreCase);
            try
            {
                string dir = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(_routeDir.TrimEnd('\\', '/'))) ?? "", "TRAINS", "CONSISTS");
                if (Directory.Exists(dir)) foreach (var f in Directory.GetFiles(dir, "*.con")) cons.Add(Path.GetFileNameWithoutExtension(f));
            }
            catch { }
            _consists = cons.ToList();
        }

        static readonly Regex Hm = new Regex(@"^([01]\d|2[0-3]):[0-5]\d$");

        // (1.2.78) por el servicio mismo (null = nuevo), no por su posición en la lista
        async Task Edit(Service sv)
        {
            bool isNew = sv == null;
            await EnsureLists();
            var s = sv ?? new Service();
            var stations = new List<string>(_stations);
            // (1.2.78) su origen y su destino aunque ya no salgan en la ruta (antes se enseñaba la primera estación de la lista)
            foreach (var st in new[] { s.Destination, s.Origin })
                if (st != "" && !stations.Any(x => string.Equals(x, st, StringComparison.CurrentCultureIgnoreCase))) stations.Insert(0, st);
            var cons = _consists.Count > 0 ? new List<string>(_consists) : new List<string>();
            if (s.Consist != "" && !cons.Contains(s.Consist)) cons.Insert(0, s.Consist);
            var stopList = s.Stops == "todas" || s.Stops == "ninguna" ? new List<string>() : s.Stops.Split(',').Select(x => x.Trim()).Where(x => x != "").ToList();
            using var dlg = new FormDialog(!isNew ? Tr("Editar servicio") : Tr("Nuevo servicio"), Tr("Guardar"), 560, "🚆", Tr("Trenes IA de la sala compartida de CGL"));
            dlg.AddText("name", Tr("Nombre (p. ej. C1)"), s.Name);
            dlg.AddCombo("kind", Tr("Tipo"), Kinds.Select(KindName), Math.Max(0, Array.IndexOf(Kinds, s.Kind)));
            int StIdx(string n) => Math.Max(0, stations.FindIndex(x => string.Equals(x, n, StringComparison.CurrentCultureIgnoreCase)));
            dlg.AddCombo("origin", Tr("Origen"), stations, StIdx(s.Origin));
            dlg.AddCombo("dest", Tr("Destino"), stations, StIdx(s.Destination));
            dlg.AddCombo("con", Tr("Composición"), cons, Math.Max(0, cons.IndexOf(s.Consist)));
            dlg.AddText("first", Tr("Primera salida (HH:MM, hora de la sala)"), s.First);
            dlg.AddText("every", Tr("Cada cuántos minutos (0 = una sola salida)"), s.Every.ToString(CultureInfo.InvariantCulture));
            dlg.AddText("last", Tr("Última salida (HH:MM)"), s.Last);
            string[] modes = { Tr("En todas las estaciones"), Tr("Sin paradas"), Tr("Solo en las marcadas") };
            int mode = s.Stops == "todas" ? 0 : s.Stops == "ninguna" ? 1 : 2;
            dlg.AddCombo("mode", Tr("Paradas"), modes, mode);
            var marked = stopList.Select(x => stations.FindIndex(y => string.Equals(y, x, StringComparison.CurrentCultureIgnoreCase))).Where(i => i >= 0).ToList();
            dlg.AddChecklist("stops", Tr("Estaciones en las que para"), stations, marked, 120);
            dlg.AddCombo("active", Tr("Activo"), new[] { Tr("Sí"), Tr("No") }, s.Active ? 0 : 1);
            if (dlg.Field("mode") is ComboBox mb && dlg.Field("stops") is Control sl)
            {
                sl.Enabled = mb.SelectedIndex == 2;
                mb.SelectedIndexChanged += (o, e) => sl.Enabled = mb.SelectedIndex == 2;
            }
            dlg.Validate = async d =>
            {
                string name = d.Get("name"), first = d.Get("first"), last = d.Get("last"), every = d.Get("every");
                if (name.Length == 0 || name.Length > 24 || name.Contains('|')) return Tr("El nombre: de 1 a 24 letras, sin «|».");
                if (!Hm.IsMatch(first) || !Hm.IsMatch(last)) return Tr("Las horas, como 07:30.");
                if (!int.TryParse(every, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ev) || ev < 0 || ev > 1440) return Tr("Cada cuántos minutos: de 0 a 1440.");
                int oi = d.Index("origin"), di = d.Index("dest"), ci = d.Index("con");
                if (oi < 0 || di < 0 || oi >= stations.Count || di >= stations.Count) return Tr("Elige el origen y el destino.");
                if (oi == di) return Tr("El origen y el destino son la misma estación.");
                if (ci < 0 || ci >= cons.Count) return Tr("Elige la composición.");
                int md = d.Index("mode");
                string stops = md == 0 ? "todas" : md == 1 ? "ninguna" : string.Join(", ", d.Checked("stops").Select(i => stations[i]));
                if (md == 2 && stops.Length == 0) return Tr("Marca alguna estación, o elige «En todas las estaciones» o «Sin paradas».");
                // (1.2.77) su composición descrita: el despachador de la sala no tiene las composiciones de cada uno (AiConsistSpec.cs)
                string baseDir = Path.GetDirectoryName(Path.GetDirectoryName(_routeDir.TrimEnd('\\', '/'))) ?? "";
                string spec = AiConsistSpec.Build(baseDir, cons[ci]);
                if (spec == null) return string.Format(Tr("No se puede leer la composición «{0}» en tu Open Rails."), cons[ci]);
                var (json, err) = await Rpc("room_ai_service_save", new
                {
                    p_id = !isNew ? (long?)s.Id : null, p_name = name, p_kind = Kinds[Math.Max(0, d.Index("kind"))], p_origin = stations[oi], p_destination = stations[di],
                    p_consist = cons[ci], p_first_time = first, p_every_min = ev, p_last_time = last, p_stops = stops, p_active = d.Index("active") == 0, p_route = _route,
                    p_consist_spec = spec
                });
                if (err != null && err.IndexOf("p_consist_spec", StringComparison.OrdinalIgnoreCase) >= 0) return Tr("El servidor aún no guarda la composición de los servicios (falta horario-composicion.sql).");
                return err;
            };
            if (dlg.ShowDialog(this) == DialogResult.OK) await Reload();
        }

        async Task Delete(Service s)
        {
            if (s == null) { Pick(); return; }
            if (ThemedBox.Show(this, string.Format(Tr("¿Quitar el servicio {0} ({1} → {2})?"), s.Name, s.Origin, s.Destination), Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            var (_, err) = await Rpc("room_ai_service_delete", new { p_id = s.Id });
            if (IsDisposed) return;
            if (err != null) { ThemedBox.Show(this, err, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            await Reload();
        }

        async Task TestPath(Service s)
        {
            if (s == null) { Pick(); return; }
            await EnsureLists();
            double km = _g == null ? -1 : PathKm(_g, s.Origin, s.Destination);
            string msg = _g == null ? Tr("No se ha podido leer la ruta.")
                       : km < 0 ? string.Format(Tr("No hay vía entre {0} y {1}."), s.Origin, s.Destination)
                       : string.Format(Tr("Hay camino de {0} a {1}: unos {2} km. (El despachador busca el camino exacto al crear el tren.)"), s.Origin, s.Destination, km.ToString("0.0", CultureInfo.CurrentCulture));
            ThemedBox.Show(this, msg, Tr("Probar camino"), MessageBoxButtons.OK, km < 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        }

        // Kilómetros por la red de la ruta entre un andén (o apartadero) de una estación y uno de la otra; −1 si no hay vía.
        // (Aproximado: no mira por qué lado se puede pasar cada aguja.)
        public static double PathKm(RouteGraph g, string from, string to)
        {
            bool Is(string n, string want) => n != null && string.Equals(n.Trim(), want?.Trim(), StringComparison.CurrentCultureIgnoreCase);
            var dist = new Dictionary<int, double>(); var pq = new PriorityQueue<int, double>();
            double best = double.MaxValue;
            void Seed(int n, double d) { if (n < 0) return; if (!dist.TryGetValue(n, out double o) || d < o) { dist[n] = d; pq.Enqueue(n, d); } }
            var targets = new List<(int e, double off)>();
            for (int e = 0; e < g.Edges.Length; e++)
            {
                var E = g.Edges[e];
                foreach (var (off, n) in E.Platforms.Concat(E.Sidings))
                {
                    if (Is(n, from)) { Seed(E.A, off); Seed(E.B, E.Len - off); }
                    if (Is(n, to)) targets.Add((e, off));
                }
            }
            // origen y destino en el mismo tramo
            for (int e = 0; e < g.Edges.Length; e++)
            {
                var E = g.Edges[e];
                foreach (var (o1, n1) in E.Platforms.Concat(E.Sidings)) if (Is(n1, from)) foreach (var (o2, _) in targets.Where(t => t.e == e)) best = Math.Min(best, Math.Abs(o2 - o1));
            }
            while (pq.TryDequeue(out int n, out double dn))
            {
                if (dn > dist[n] + 1e-6 || dn > best) continue;
                foreach (int e in g.EdgesAt(n))
                {
                    var E = g.Edges[e];
                    foreach (var (te, off) in targets) if (te == e) best = Math.Min(best, dn + (E.A == n ? off : E.Len - off));
                    int m = E.A == n ? E.B : E.A; if (m < 0) continue;
                    double nd = dn + E.Len;
                    if (!dist.TryGetValue(m, out double om) || nd < om) { dist[m] = nd; pq.Enqueue(m, nd); }
                }
            }
            return best == double.MaxValue ? -1 : best / 1000;
        }
    }
}
