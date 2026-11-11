// Número de tren en la sala compartida (2026-11-09): antes de entrar, el maquinista elige el tipo de servicio y la dependencia
// (estación) de destino, y le sale un número de 4 cifras al azar: par si se aleja de Estación Sur (Sur → Norte), impar si va
// hacia ella (Norte → Sur). Se ve en el HUD, sobre el minimapa, y su SelectOR lo manda a la sala (SwitchPilot.SetTrainNo):
// el Puesto de Mando lo pone junto a su nombre.
// (2026-11-10, petición) Y sus paradas intermedias: estaciones, instalaciones de mercancías o apartaderos (con nombre en la ruta),
// en una lista o con clic derecho en el mapa; van en orden desde la salida (por la distancia en línea recta). Las ve el jefe
// de circulación en la tarjeta del tren del Puesto.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SelectOR
{
    public partial class MainMenuForm
    {
        // C · MD · LD · AV · M (lo que va a la sala) y su nombre
        internal static readonly (string code, string name)[] SalaServiceTypes =
        {
            ("C", "Cercanías"), ("MD", "Media Distancia"), ("LD", "Larga Distancia"), ("AV", "Alta Velocidad"), ("M", "Mercancías"),
        };
        internal static string SalaServiceName(string code) => SalaServiceTypes.FirstOrDefault(t => t.code == code).name ?? "";

        // el tren de esta entrada en la sala (null: sin elegir)
        sealed class SalaTrain { public string No, Type, Dest, From, Con; public List<string> Stops = new(); }   // Con: el .con (sin extensión), para el puesto de mando · Stops: paradas intermedias
        SalaTrain _salaTrain;

        const string SalaSouth = "Estación Sur";

        // Tipo de servicio, destino y número. false = cancelado. Sin estaciones (otra ruta, grafo sin hacer): true sin número.
        // change (2026-11-09): durante la conducción (clic en tu número del HUD): se cambian el tipo y el destino; el origen y,
        // si no cambia el sentido, el número se quedan; el jefe de circulación lo ve en la tarjeta de tu tren en el puesto
        async Task<bool> SalaTrainDialog(string dir, string patFile, bool change = false)
        {
            var old = _salaTrain;
            if (change && old == null) return false;
            if (!change) _salaTrain = null;
            List<(string name, double x, double y, int n)> st = null; string from = null; RouteGraph graph = null; HashSet<string> freight = new();
            List<(string name, double x, double y)> sidings = new();
            if (!change) SetBusy(Tr("Preparando las estaciones de la sala…"));
            try
            {
                var g = await RouteGraphFor(dir); graph = g;
                if (g != null)
                {
                    st = Schematic.Stations(g);
                    var fr = FreightPlaces(g, st.Select(x => x.name));   // (2026-11-09) y las de mercancías
                    freight = new HashSet<string>(fr.Select(x => x.name), StringComparer.Ordinal);
                    st.AddRange(fr.Select(x => (x.name, x.x, x.y, 1)));
                    sidings = SidingPlaces(g, st.Select(x => x.name));   // (2026-11-10) los apartaderos con nombre: solo como paradas
                    var pts = change ? new List<(float x, float z, bool rev)>() : await Task.Run(() => PatWorldPoints(patFile));
                    if (change) from = old.From;
                    else if (pts.Count > 0 && OrGeo.TryLatLon(pts[0].x, pts[0].z, out double la, out double lo))
                    {
                        var (x0, y0) = g.Proj(la, lo);
                        from = st.OrderBy(s => (s.x - x0) * (s.x - x0) + (s.y - y0) * (s.y - y0)).Select(s => s.name).FirstOrDefault();
                    }
                }
            }
            catch { st = null; }
            finally { if (!change) { SetIdle(); UpdateStatus(); } }
            if (st == null || st.Count < 2) return !change;

            var dests = st.Where(s => s.name != from).ToList();
            var south = st.FirstOrDefault(s => string.Equals(s.name, SalaSouth, StringComparison.OrdinalIgnoreCase));
            var fromSt = st.FirstOrDefault(s => s.name == from);
            double DistSouth((string name, double x, double y, int n) s) => south.name == null ? 0 : Math.Sqrt((s.x - south.x) * (s.x - south.x) + (s.y - south.y) * (s.y - south.y));
            // par: se aleja de Estación Sur; impar: va hacia ella
            bool Even(int di) => south.name == null || fromSt.name == null || DistSouth(dests[di]) > DistSouth(fromSt);

            var rnd = new Random();
            int baseNo = change && int.TryParse(old.No, out int oldNo) ? oldNo : rnd.Next(1000, 10000);
            string NoFor(int di) { int n = baseNo; if ((n % 2 == 0) != Even(di)) n = n >= 9999 ? n - 1 : n + 1; return n.ToString("0000"); }

            int ti = Math.Max(0, Array.FindIndex(SalaServiceTypes, t => t.code == (change ? old.Type : _prefs?.SalaTipo)));
            int di0 = Math.Max(0, dests.FindIndex(s => s.name == (change ? old.Dest : _prefs?.SalaDestino)));
            // (2026-11-10) las paradas posibles: estaciones, mercancías y apartaderos (menos la salida)
            var stopCands = st.Select(x => (x.name, x.x, x.y, kind: freight.Contains(x.name) ? 1 : 0)).Concat(sidings.Select(x => (x.name, x.x, x.y, kind: 2)))
                              .Where(x => x.name != from).GroupBy(x => x.name).Select(q => q.First()).ToList();
            var stopsWas = change ? old.Stops.ToList() : (_prefs?.SalaParadas ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries).Where(n => stopCands.Any(c => c.name == n)).ToList();
            List<string> Ordered(IEnumerable<string> names, string dest)
            {
                var l = names.Where(n => n != dest && n != from).Distinct().ToList();
                if (fromSt.name == null) return l;
                double D(string n) { var c = stopCands.First(q => q.name == n); return (c.x - fromSt.x) * (c.x - fromSt.x) + (c.y - fromSt.y) * (c.y - fromSt.y); }
                return l.OrderBy(D).ToList();
            }
            if (SalaLaunchHook != null && !change)   // (pruebas: sin ventana, lo de la última vez)
            {
                _salaTrain = new SalaTrain { No = NoFor(di0), Type = SalaServiceTypes[ti].code, Dest = dests[di0].name, From = from, Stops = Ordered(stopsWas, dests[di0].name) };
                return true;
            }
            using var dlg = new FormDialog(Tr(change ? "Cambiar tu tren" : "Tu tren en la sala"), Tr(change ? "Cambiar" : "Entrar en la sala"), 720, "🚆",
                change ? Tr("El jefe de circulación verá el cambio en la tarjeta de tu tren") : from != null ? string.Format(Tr("Sales de {0}"), from) : Tr("Sala compartida de CGL"));
            if (change) { dlg.TopMost = true; dlg.StartPosition = FormStartPosition.CenterScreen; }   // (encima de Open Rails)
            dlg.AddCombo("type", Tr("Tipo de servicio"), SalaServiceTypes.Select(t => Tr(t.name)), ti);
            var cbDest = dlg.AddCombo("dest", Tr("Dependencia de destino"), dests.Select(s => freight.Contains(s.name) ? s.name + "  ·  " + Tr("mercancías") : s.name), di0);
            var info = dlg.AddInfo("", Theme.Gold);
            void Show()
            {
                int di = Math.Max(0, cbDest.SelectedIndex);
                info.Text = string.Format(Tr("Tu número de tren: {0}"), NoFor(di));   // (2026-11-09) sin explicar la paridad
            }
            // (2026-11-09) el mapa de la ruta: dónde está cada destino (SalaTrainMap.cs); un clic en una estación la elige
            var map = new SalaTrainMap(graph, st.Select(x => (x.name, x.x, x.y)).Concat(sidings)) { Origin = from, Dest = dests[Math.Max(0, cbDest.SelectedIndex)].name, Freight = freight,
                                                                                         Sidings = new HashSet<string>(sidings.Select(x => x.name), StringComparer.Ordinal) };
            map.Picked += name => { int k = dests.FindIndex(x => x.name == name); if (k >= 0) cbDest.SelectedIndex = k; };
            dlg.AddControl(map, Theme.Px(320));
            // (2026-11-10) las paradas intermedias: la lista (y clic derecho en el mapa)
            dlg.AddInfo(Tr("Paradas intermedias (opcional): márcalas en la lista o con clic derecho en el mapa."));
            var lst = new CheckedListBox { CheckOnClick = true, BorderStyle = BorderStyle.None, BackColor = Theme.Surface2, ForeColor = Theme.Text, Font = Theme.Font(9f), IntegralHeight = false };
            foreach (var c in stopCands.OrderBy(c => c.kind).ThenBy(c => c.name, StringComparer.CurrentCulture))
                lst.Items.Add(new StopItem { Name = c.name, Text = c.name + (c.kind == 1 ? "  ·  " + Tr("mercancías") : c.kind == 2 ? "  ·  " + Tr("apartadero") : "") }, stopsWas.Contains(c.name));
            dlg.AddControl(lst, Theme.Px(150));
            var stopsInfo = dlg.AddInfo("", Color.FromArgb(120, 200, 230));
            List<string> Checked() => lst.CheckedItems.Cast<StopItem>().Select(i => i.Name).ToList();
            void ShowStops()
            {
                var l = Ordered(Checked(), dests[Math.Max(0, cbDest.SelectedIndex)].name);
                map.Stops = new HashSet<string>(l, StringComparer.Ordinal); map.Invalidate();
                stopsInfo.Text = l.Count == 0 ? Tr("Sin paradas intermedias") : Tr("Paradas") + ": " + string.Join(" · ", l);
            }
            lst.ItemCheck += (s, e) => lst.BeginInvoke(new Action(ShowStops));
            map.StopToggled += name =>
            {
                for (int k = 0; k < lst.Items.Count; k++)
                    if (((StopItem)lst.Items[k]).Name == name) { lst.SetItemChecked(k, !lst.GetItemChecked(k)); lst.TopIndex = Math.Max(0, k - 2); break; }
            };
            cbDest.SelectedIndexChanged += (s, e) => { Show(); map.Dest = dests[Math.Max(0, cbDest.SelectedIndex)].name; ShowStops(); };
            Show(); ShowStops();
            dlg.AddInfo(Tr("Sale en el HUD, sobre el minimapa, y el jefe de circulación lo ve en el Puesto de Mando junto a tu nombre."));
            if ((change ? dlg.ShowDialog() : dlg.ShowDialog(this)) != DialogResult.OK) return false;
            int t0 = Math.Max(0, dlg.Index("type")), d0 = Math.Max(0, dlg.Index("dest"));
            _salaTrain = new SalaTrain { No = NoFor(d0), Type = SalaServiceTypes[t0].code, Dest = dests[d0].name, From = from, Con = old?.Con, Stops = Ordered(Checked(), dests[d0].name) };
            if (change) { SalaTrainToPilot(); try { _serviceHud?.Invalidate(); } catch { } }
            if (_prefs != null) { _prefs.SalaTipo = _salaTrain.Type; _prefs.SalaDestino = _salaTrain.Dest; _prefs.SalaParadas = string.Join("|", _salaTrain.Stops); try { _prefs.Save(); } catch { } }
            return true;
        }

        // (2026-11-09) Las instalaciones de mercancías: los apartaderos con nombre de la ruta («Cereales Paco S.A. Vía 2» →
        // «Cereales Paco S.A.»), sin las estaciones de viajeros ni las vías de servicio (apartados, maniobras, talleres, reserva…).
        // Cada una, en el punto medio de sus vías.
        static readonly System.Text.RegularExpressions.Regex SidingVia = new(@"\s*[-·,]?\s*\b(V[ií]a|Via)\b.*$|\s+\d+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        static readonly string[] SidingNotFreight = { "apartad", " ap ", "bolsillo", "mantenimiento", "maniobra", "mango", "reserva", "taller", "clasificaci", "comienzo", "museo", "terminal", " ut", "túnel", "tunel", "viaducto", "puente" };
        // el nombre de la instalación de mercancías de un apartadero, o null si no lo es (también lo usa el puesto: RouteBoard)
        internal static string FreightName(string siding, HashSet<string> stations)
        {
            string b = SidingVia.Replace(siding ?? "", "").Trim();
            b = SidingVia.Replace(b, "").Trim();   // («… 1 Vía 2»)
            if (b.Length < 3 || stations != null && stations.Contains(b)) return null;
            string low = " " + b.ToLowerInvariant() + " ";
            if (SidingNotFreight.Any(w => low.Contains(w)) || b.Length <= 3 && b.All(char.IsUpper)) return null;
            if (System.Text.RegularExpressions.Regex.IsMatch(b, @"^[A-Z]{1,3}-\d")) return null;   // (2026-11-09) carreteras («A-343», «N-331»)
            return b;
        }
        internal static List<(string name, double x, double y)> FreightPlaces(RouteGraph g, IEnumerable<string> stations)
        {
            var st = new HashSet<string>(stations, StringComparer.OrdinalIgnoreCase);
            var acc = new Dictionary<string, List<(double x, double y)>>(StringComparer.OrdinalIgnoreCase);
            if (g?.Edges == null) return new();
            for (int e = 0; e < g.Edges.Length; e++)
                foreach (var (off, name) in g.Edges[e].Sidings)
                {
                    string b = FreightName(name, st); if (b == null) continue;
                    if (!acc.TryGetValue(b, out var l)) acc[b] = l = new();
                    l.Add(g.PointAt(e, off));
                }
            // (2026-11-09) el punto de sus vías más cerca del centro (con el mismo nombre en dos sitios, la media caía fuera de la vía)
            return acc.Select(kv =>
            {
                double mx = kv.Value.Average(p => p.x), my = kv.Value.Average(p => p.y);
                var c = kv.Value.OrderBy(p => (p.x - mx) * (p.x - mx) + (p.y - my) * (p.y - my)).First();
                return (kv.Key, c.x, c.y);
            }).OrderBy(t => t.Key, StringComparer.CurrentCulture).ToList();
        }

        sealed class StopItem { public string Name, Text; public override string ToString() => Text; }

        // (2026-11-11) el nombre de un sitio sin su «Vía 2» ni su número («Cereales Paco S.A. Vía 2» → «Cereales Paco S.A.»): así
        // se agrupan las vías de una terminal o de un apartadero (FreightPlaces, SidingPlaces; horario de la sala)
        internal static string PlaceBase(string s) { string b = SidingVia.Replace(s ?? "", "").Trim(); return SidingVia.Replace(b, "").Trim(); }
        static string PlaceNorm(string s)
        {
            var d = (s ?? "").Trim().Normalize(System.Text.NormalizationForm.FormD); var sb = new System.Text.StringBuilder();
            foreach (var c in d) if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }
        // ¿el andén o vía de apartadero «raw» es del sitio «want»? Igual (sin tildes ni mayúsculas) o, si «want» es el nombre
        // agrupado de una terminal o un apartadero, su nombre sin la vía. exact: solo igual.
        internal static bool SamePlace(string raw, string want, bool exact = false)
        {
            if (string.IsNullOrWhiteSpace(raw) || string.IsNullOrWhiteSpace(want)) return false;
            string w = PlaceNorm(want);
            if (PlaceNorm(raw) == w) return true;
            if (exact) return false;
            // (sin la vía de ninguno de los dos: «Industrias CAF 9» del despachador y «Industrias CAF» de los andenes)
            string br = PlaceNorm(PlaceBase(raw)), bw = PlaceNorm(PlaceBase(want));
            return br.Length > 0 && (br == w || br == bw);
        }

        // (2026-11-10) Los apartaderos con nombre de la ruta que no son estaciones ni instalaciones de mercancías (apartados,
        // bolsillos, depósitos…), para elegirlos como parada intermedia. Cada uno, en el punto de sus vías más cerca del centro.
        static readonly string[] SidingNotPlace = { "túnel", "tunel", "viaducto", "puente", "comienzo" };
        internal static List<(string name, double x, double y)> SidingPlaces(RouteGraph g, IEnumerable<string> exclude)
        {
            var ex = new HashSet<string>(exclude, StringComparer.OrdinalIgnoreCase);
            // (con y sin tilde, el mismo: «Hernandez Plaza - Mango» y «Hernández Plaza - Mango»)
            var acc = new Dictionary<string, List<(double x, double y)>>(StringComparer.Create(System.Globalization.CultureInfo.InvariantCulture, System.Globalization.CompareOptions.IgnoreCase | System.Globalization.CompareOptions.IgnoreNonSpace));
            if (g?.Edges == null) return new();
            for (int e = 0; e < g.Edges.Length; e++)
                foreach (var (off, name) in g.Edges[e].Sidings)
                {
                    string b = SidingVia.Replace(name ?? "", "").Trim(); b = SidingVia.Replace(b, "").Trim();
                    if (b.Length < 3 || ex.Contains(b)) continue;
                    if (b.Length <= 3 && b.All(char.IsUpper)) continue;   // (siglas sueltas: «CAF», «LOC», «MAT»)
                    string low = " " + b.ToLowerInvariant() + " ";
                    if (SidingNotPlace.Any(w => low.Contains(w))) continue;
                    if (System.Text.RegularExpressions.Regex.IsMatch(b, @"^[A-Z]{1,3}-\d")) continue;   // (carreteras)
                    if (!acc.TryGetValue(b, out var l)) acc[b] = l = new();
                    l.Add(g.PointAt(e, off));
                }
            return acc.Select(kv =>
            {
                double mx = kv.Value.Average(p => p.x), my = kv.Value.Average(p => p.y);
                var c = kv.Value.OrderBy(p => (p.x - mx) * (p.x - mx) + (p.y - my) * (p.y - my)).First();
                return (kv.Key, c.x, c.y);
            }).OrderBy(t => t.Key, StringComparer.CurrentCulture).ToList();
        }

        // (2026-11-09) Clic en tu número de tren del HUD: cambiar el tipo de servicio y el destino
        bool _salaChanging;
        async void SalaTrainChange()
        {
            if (_salaChanging || _salaTrain == null || _curRoute == null) return;
            _salaChanging = true;
            try { await SalaTrainDialog(_curRoute.Path, null, change: true); } catch { }
            finally { _salaChanging = false; }
        }

        // Al crear el enlace con la sala (MainMenuForm.Agujas.cs): el número, a la sala.
        void SalaTrainToPilot()
        {
            try
            {
                if (_pilot == null || _salaTrain == null || _roomHost != SalaHost || _roomPort != SalaPort) return;
                _pilot.SetTrainNo(_salaTrain.No, _salaTrain.Type, _salaTrain.Dest, _salaTrain.Con, _salaTrain.Stops);
            }
            catch { }
        }

        // HUD (sobre el minimapa): «2471» y «Media Distancia → Estación Norte». null fuera de la sala.
        (string no, string sub)? SalaTrainHud()
        {
            if (_salaTrain == null || !RoomMapOn) return null;
            return (_salaTrain.No, Tr(SalaServiceName(_salaTrain.Type)) + " → " + _salaTrain.Dest
                                   + (_salaTrain.Stops.Count == 1 ? " · " + Tr("1 parada") : _salaTrain.Stops.Count > 1 ? " · " + string.Format(Tr("{0} paradas"), _salaTrain.Stops.Count) : ""));
        }
    }
}
