// La empresa compra TRENES (.con) (servidor: sql/trenes-empresa.sql y sql/trenes-por-con.sql).
//  · Cada .con comprado (o alquilado) es un EJEMPLAR de la flota, con su estado, sus km, su mantenimiento y su
//    matrícula, como antes una máquina. El precio es el del tren entero: cada máquina como siempre (fleet_value) y
//    cada coche o vagón a precio bajo (vagón 40.000 € + 800 €/t · coche 150.000 € + 2.000 €/plaza · otros 120.000 €,
//    × la escala de precios de la flota).
//  · El tren queda en la empresa con su .con, sus vehículos y su composición 2D: cualquier socio se lo añade a su
//    contenido desde Flota (solo el .con: el material, las carpetas TRAINSET, lo tiene que tener instalado cada uno).
//  · Un tren se reconoce por su COMPOSICIÓN (sus vehículos por modelo), no por el nombre del archivo: cada maquinista
//    puede tenerlo con otro nombre.
//  · Al ponerse de servicio se usa un ejemplar libre de ese tren. Si no lo hay: en «Aviso» (y «No se comprueba») vale
//    un ejemplar de otro tren de la empresa con la misma máquina de cabeza; en «Obligatorio», no. Las máquinas
//    sueltas compradas antes («máquinas anteriores») valen siempre como cabeza.
//  · El maquinista pide el tren (se envían su .con y su imagen); el gerente o un gestor lo compran en Compra →
//    Solicitudes, aunque no tengan ese material.
//  · Plazas (trenes-plazas.sql): el gerente, un gestor o el administrador fijan las plazas de cada tren de la flota y
//    valen para todos los socios (viajeros, ingreso y tope del servidor) sin tocar ningún .eng/.wag; si no las han
//    fijado, valen las del PassengerCapacity de los archivos de cada uno (SeatsOverride en AnalyzeComposition).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SelectOR
{
    public partial class MainMenuForm
    {
        // ============================ datos ============================
        sealed class TrainVeh
        {
            public string Name = "", Folder = "", Path;
            public bool Wagon; public int Count = 1;
            public string Type = "other";                 // vagón: freight | pax | other
            public string Etype = "electric"; public bool Automotor;
            public double Power, Speed, Cars = 1, Mass, Brake, Capacity, Comfort;
            public object ToJson(int count) => new
            {
                name = Name, folder = Folder, wagon = Wagon, count, type = Type, etype = Etype, automotor = Automotor,
                power = Math.Round(Power), speed = Math.Round(Speed), cars = (int)Math.Max(1, Cars), mass = Math.Round(Mass, 1),
                brake = Math.Round(Brake), capacity = Math.Round(Capacity), comfort = Math.Round(Comfort)
            };
            public static TrainVeh FromJson(JsonElement e) => new TrainVeh
            {
                Name = Str(e, "name"), Folder = Str(e, "folder"), Wagon = e.TryGetProperty("wagon", out var w) && w.ValueKind == JsonValueKind.True,
                Count = Math.Max(1, (int)Num(e, "count")), Type = Str(e, "type").Length > 0 ? Str(e, "type") : "other",
                Etype = Str(e, "etype").Length > 0 ? Str(e, "etype") : "electric", Automotor = e.TryGetProperty("automotor", out var a) && a.ValueKind == JsonValueKind.True,
                Power = Num(e, "power"), Speed = Num(e, "speed"), Cars = Math.Max(1, Num(e, "cars")), Mass = Num(e, "mass"),
                Brake = Num(e, "brake"), Capacity = Num(e, "capacity"), Comfort = Num(e, "comfort")
            };
        }

        sealed class CoTrain
        {
            public string Id, Name, ConFile, By, At; public bool HasImage;
            public int? Seats;                                   // plazas fijadas por la empresa (null: las de los archivos)
            public List<TrainVeh> Vehicles = new();
            public double FileSeats => Vehicles.Sum(v => Math.Max(0, v.Capacity) * Math.Max(1, v.Count));   // las de sus vehículos al darlo de alta
            public string Lead => Vehicles.FirstOrDefault(v => !v.Wagon)?.Name ?? "";   // la máquina de cabeza
        }

        readonly List<CoTrain> _coTrains = new();
        string _coTrainsCompany, _coTrainsStamp, _trainMode = "warn";
        bool _trainsOnServer = true;   // false: falta trenes-empresa.sql

        // Clave de un tren: sus vehículos por modelo (nombre del .eng/.wag, sin la carpeta: las libreas cuentan igual).
        // Con la unidad completa (UnitVehicles): un tren comprado antes de la 1.2.57 con la motriz de cola de un automotor
        // largo en su lista se sigue reconociendo con el mismo .con.
        static string TrainKey(IEnumerable<TrainVeh> vs)
            => string.Join("|", UnitVehicles(vs).GroupBy(v => v.Name.Trim().ToLowerInvariant()).OrderBy(g => g.Key).Select(g => g.Key + "×" + g.Sum(x => x.Count)));

        // Un tren encabezado por un automotor: las demás motrices de su misma carpeta con otro nombre son partes de esa
        // unidad (la de cola de un 450, la otra cabeza de un AVE…), no máquinas aparte. Las cabezas iguales (dos unidades
        // acopladas) sí cuentan. Es la regla de TrainVehiclesOf, aplicada a una lista ya hecha (las guardadas en el servidor).
        static List<TrainVeh> UnitVehicles(IEnumerable<TrainVeh> vs)
        {
            var l = vs.ToList();
            var head = l.FirstOrDefault(v => !v.Wagon);
            if (head == null || !head.Automotor) return l;
            return l.Where(v => v.Wagon || ReferenceEquals(v, head)
                              || string.Equals(v.Name, head.Name, StringComparison.OrdinalIgnoreCase)
                              || !string.Equals(v.Folder ?? "", head.Folder ?? "", StringComparison.OrdinalIgnoreCase)).ToList();
        }

        // Los vehículos de un tren del contenido, por modelo, con sus datos de tasación. Las motrices internas de un
        // automotor no cuentan (se tasa la formación por su cabeza), ni los remolques de un automotor (van en ella).
        List<TrainVeh> TrainVehiclesOf(TrainItem c)
        {
            var list = new List<TrainVeh>();
            if (c?.FilePath == null) return list;
            ConsistDoc doc;
            try { doc = ConsistDoc.Load(c.FilePath); } catch { return list; }
            if (doc == null || doc.Cars.Count == 0) return list;
            var hidden = AutomotorMemberEngs();
            string lead = LeadEngineName(c);
            string leadPath = null;
            foreach (var car in doc.Cars) if (car.IsEngine && string.Equals(car.Name, lead, StringComparison.OrdinalIgnoreCase)) { leadPath = ResolveCarFile(car.Name, car.Folder); break; }
            var (_, leadKmh, _) = leadPath != null ? ReadEngineSpecs(leadPath) : (0, 0, "");
            var analysis = AnalyzeComposition(c, leadKmh);
            bool leadAuto = leadPath != null && ShapeForService(DetectUnitShape(leadPath), analysis.Freight, leadPath).automotor;
            string leadFolder = null;
            foreach (var car in doc.Cars) if (car.IsEngine && string.Equals(car.Name, lead, StringComparison.OrdinalIgnoreCase)) { leadFolder = car.Folder ?? ""; break; }
            var byKey = new Dictionary<string, TrainVeh>(StringComparer.OrdinalIgnoreCase);
            foreach (var car in doc.Cars)
            {
                string path = ResolveCarFile(car.Name, car.Folder);
                var st = Veh(path);
                bool traction = car.IsEngine && (st?.Traction ?? false);   // con potencia, o sin declararla (Open Rails pone la suya)
                if (car.IsEngine && hidden.Contains(car.Name) && !(path != null && IsUnitHead(path))) continue;   // motriz interna de una formación
                if (!traction && leadAuto) continue;                                                // remolque del automotor
                // otra motriz de la misma unidad (misma carpeta, otro nombre): la de cola de un automotor largo, como un 450
                // de 6 coches con las dos motrices en los extremos. Va en el precio del automotor, no como máquina aparte.
                if (traction && leadAuto && !string.Equals(car.Name, lead, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(car.Folder ?? "", leadFolder ?? "", StringComparison.OrdinalIgnoreCase)) continue;
                string key = car.Name + "|" + (car.Folder ?? "");
                if (byKey.TryGetValue(key, out var have)) { have.Count++; continue; }
                var v = new TrainVeh { Name = car.Name, Folder = car.Folder ?? "", Path = path, Wagon = !traction };
                if (traction)
                {
                    var (kw, kmh, type) = ReadEngineSpecs(path);
                    var (automotor, cars) = ShapeForService(DetectUnitShape(path), analysis.Freight, path);
                    var (mass, brake) = AnalyzeUnitPhysics(path, automotor);
                    v.Etype = type; v.Automotor = automotor; v.Power = kw; v.Speed = kmh; v.Cars = cars; v.Mass = mass; v.Brake = brake;
                    if (automotor) { v.Capacity = analysis.Capacity; v.Comfort = analysis.Comfort; }
                }
                else
                {
                    v.Mass = st?.MassT ?? 0; v.Capacity = st?.Capacity ?? 0;
                    string k = DeclaredVehicleType(path);
                    v.Type = v.Capacity > 0 ? "pax" : k == "freight" ? "freight" : "other";
                    v.Comfort = v.Capacity > 0 ? analysis.Comfort : 0;
                }
                byKey[key] = v; list.Add(v);
            }
            return list;
        }

        // Los vehículos de cada .con del contenido se leen una vez (para reconocer los trenes de la empresa).
        static readonly Dictionary<string, List<TrainVeh>> _trainVehCache = new(StringComparer.OrdinalIgnoreCase);
        List<TrainVeh> TrainVehiclesOfCached(TrainItem c)
        {
            if (c?.FilePath == null) return new List<TrainVeh>();
            lock (_trainVehCache) if (_trainVehCache.TryGetValue(c.FilePath, out var hit)) return hit;
            var l = TrainVehiclesOf(c);
            lock (_trainVehCache) _trainVehCache[c.FilePath] = l;
            return l;
        }

        // Precio de una unidad, igual que el servidor (fleet_value / wagon_value), y el del tren entero (train_value).
        double UnitPriceLocal(TrainVeh v)
        {
            if (!v.Wagon) return FleetPriceLocal(v.Power, v.Speed, v.Etype, v.Automotor, (int)Math.Max(1, v.Cars), v.Mass, v.Brake).price;
            double b = v.Type == "freight" ? 40000 + 800 * Math.Min(300, Math.Max(0, v.Mass))
                     : v.Type == "pax" ? 150000 + 2000 * Math.Min(400, Math.Max(0, v.Capacity)) : 120000;
            return Math.Round(b * _fleetScale, 2);
        }
        double TrainPriceLocal(IEnumerable<TrainVeh> vs) => vs.Sum(v => UnitPriceLocal(v) * Math.Max(1, v.Count));

        string TrainSummary(IEnumerable<TrainVeh> vs)
        {
            var l = UnitVehicles(vs);
            // un automotor se dice con sus coches («automotor de 3 coches»)
            var autos = l.Where(v => !v.Wagon && v.Automotor).ToList();
            int locos = l.Where(v => !v.Wagon && !v.Automotor).Sum(v => v.Count), wag = l.Where(v => v.Wagon && v.Type == "freight").Sum(v => v.Count),
                coach = l.Where(v => v.Wagon && v.Type != "freight").Sum(v => v.Count);
            var parts = new List<string>();
            foreach (var a in autos) parts.Add((a.Count > 1 ? a.Count + " × " : "") + string.Format(Tr("automotor de {0} coches"), (int)Math.Max(1, a.Cars)));
            if (locos > 0) parts.Add(string.Format(locos == 1 ? Tr("{0} máquina") : Tr("{0} máquinas"), locos));
            if (coach > 0) parts.Add(string.Format(coach == 1 ? Tr("{0} coche") : Tr("{0} coches"), coach));
            if (wag > 0) parts.Add(string.Format(wag == 1 ? Tr("{0} vagón") : Tr("{0} vagones"), wag));
            return string.Join(" · ", parts);
        }

        static bool TrainCarriesPassengers(IEnumerable<TrainVeh> vs) => vs.Any(v => v.Capacity > 0 || (v.Wagon && v.Type == "pax"));

        static string TEur(double v) => v.ToString("N0", EsEs) + " €";

        // La lista de trenes de cada empresa (train_list), guardada con su huella (train_list_stamp, trenes-lista-ligera.sql):
        // solo se vuelve a descargar si la huella cambia (un tren nuevo, unas plazas…). Con miles de trenes eran varios MB
        // en cada sección. stamp: la huella de la lista devuelta (vacía sin el SQL: entonces se descarga siempre).
        readonly Dictionary<string, (string stamp, string json)> _trainListCache = new(StringComparer.OrdinalIgnoreCase);
        bool _trainStampOnServer = true;

        readonly Dictionary<string, Task<(string json, string err, string stamp)>> _trainListBusy = new(StringComparer.OrdinalIgnoreCase);

        // Si ya se está pidiendo la de esa empresa (Flota y los distintivos a la vez, p. ej.), se espera a esa misma.
        Task<(string json, string err, string stamp)> TrainListJsonAsync(string companyId)
        {
            if (_trainListBusy.TryGetValue(companyId, out var busy)) return busy;
            var task = TrainListJsonCoreAsync(companyId);
            if (task.IsCompleted) return task;
            _trainListBusy[companyId] = task;
            _ = task.ContinueWith(_ => { if (_trainListBusy.TryGetValue(companyId, out var b) && b == task) _trainListBusy.Remove(companyId); },
                                  TaskScheduler.FromCurrentSynchronizationContext());
            return task;
        }

        async Task<(string json, string err, string stamp)> TrainListJsonCoreAsync(string companyId)
        {
            string stamp = null;
            if (_trainStampOnServer)
            {
                var (js, es) = await Supa.RpcAsync("train_list_stamp", new { p_company = companyId });
                if (es == null) stamp = JsonText(js);
                else if (es.Contains("PGRST202") || es.Contains("Could not find")) _trainStampOnServer = false;
            }
            lock (_trainListCache)
                if (!string.IsNullOrEmpty(stamp) && _trainListCache.TryGetValue(companyId, out var c) && c.stamp == stamp) return (c.json, null, stamp);
            var (json, err) = await Supa.RpcPagedAsync("train_list", new { p_company = companyId });
            if (err == null && !string.IsNullOrEmpty(stamp)) lock (_trainListCache) _trainListCache[companyId] = (stamp, json);
            return (json, err, err == null ? stamp : null);
        }

        async Task<bool> LoadCoTrainsAsync(string companyId, bool force = false)
        {
            if (companyId == null) return false;
            if (!force && _coTrainsCompany == companyId) return true;
            var (json, err, stamp) = await TrainListJsonAsync(companyId);
            if (err != null) { _trainsOnServer = !(err.Contains("PGRST202") || err.Contains("Could not find")); return false; }
            _trainsOnServer = true;
            if (!string.IsNullOrEmpty(stamp) && _coTrainsCompany == companyId && _coTrainsStamp == stamp) return true;   // no ha cambiado: la que ya hay vale
            var list = new List<CoTrain>();
            try
            {
                using var d = JsonDocument.Parse(json);
                foreach (var e in d.RootElement.EnumerateArray())
                {
                    var t = new CoTrain { Id = Str(e, "id"), Name = Str(e, "name"), ConFile = Str(e, "con_file"), By = Str(e, "created_by"), At = FmtDate(Str(e, "created_at")),
                                          HasImage = e.TryGetProperty("has_image", out var hi) && hi.ValueKind == JsonValueKind.True };
                    if (e.TryGetProperty("vehicles", out var vs) && vs.ValueKind == JsonValueKind.Array) foreach (var v in vs.EnumerateArray()) t.Vehicles.Add(TrainVeh.FromJson(v));
                    if (e.TryGetProperty("seats", out var se) && se.ValueKind == JsonValueKind.Number && se.GetInt32() > 0) t.Seats = se.GetInt32();
                    _trainMode = Str(e, "mode").Length > 0 ? Str(e, "mode") : _trainMode;
                    list.Add(t);
                }
                if (list.Count == 0) { var (jm, em) = await Supa.RpcAsync("train_mode", new { }); if (em == null) _trainMode = JsonText(jm); }
            }
            catch { }
            _coTrains.Clear(); _coTrains.AddRange(list); _coTrainsCompany = companyId; _coTrainsStamp = stamp;
            var seats = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in list) if (t.Seats is int n) seats[t.Id] = n;
            _trainSeats = seats;   // se cambia de una vez: se lee desde otros hilos (AnalyzeComposition)
            return true;
        }

        // ============================ plazas fijadas por la empresa ============================
        Dictionary<string, int> _trainSeats = new(StringComparer.OrdinalIgnoreCase);   // tren → plazas fijadas

        // Las plazas que la empresa ha fijado para este .con (si es uno de sus trenes); null si no (valen las de los archivos).
        int? SeatsOverride(TrainItem c)
        {
            if (c?.FilePath == null) return null;
            var ct = _conTrain; var ts = _trainSeats;
            return ct.TryGetValue(c.FilePath, out var id) && ts.TryGetValue(id, out var n) && n > 0 ? n : null;
        }

        // Los datos de un tren con las plazas de la empresa, si las ha fijado (la caché de datos guarda las de los archivos).
        async Task<TrainSpec> TrainSpecSeatsAsync(TrainItem c)
        {
            var sp = await TrainSpecAsync(c);
            int? seats = SeatsOverride(c);
            if (sp == null || seats == null) return sp;
            string service = await Task.Run(() => AnalyzeComposition(c, sp.Kmh).ServiceType);   // con las plazas fijadas (densidad)
            return new TrainSpec { Traction = sp.Traction, Service = service, Kw = sp.Kw, Kmh = sp.Kmh, Capacity = seats.Value, MassT = sp.MassT, LengthM = sp.LengthM,
                                   Cars = sp.Cars, Engines = sp.Engines, Freight = false, Automotor = sp.Automotor, Known = sp.Known };
        }

        // Lo mismo, con lo ya calculado (filtros de Conducción libre y Compra): con plazas fijadas, es de viajeros.
        TrainSpec KnownSpecSeats(TrainItem c)
        {
            var sp = KnownSpec(c);
            int? seats = sp == null ? null : SeatsOverride(c);
            if (seats == null) return sp;
            return new TrainSpec { Traction = sp.Traction, Service = sp.Service, Kw = sp.Kw, Kmh = sp.Kmh, Capacity = seats.Value, MassT = sp.MassT, LengthM = sp.LengthM,
                                   Cars = sp.Cars, Engines = sp.Engines, Freight = false, Automotor = sp.Automotor, Known = sp.Known };
        }

        // Las tarjetas y fichas de los trenes de la empresa se vuelven a calcular (han cambiado sus plazas o cuáles son).
        void ForgetTrainSpecs(IEnumerable<string> cons)
        {
            var l = cons.ToList();
            try { _lstConsists?.ForgetSpecs(l); } catch { }
            if (_activePage == 2 && _lstConsists?.SelectedItem is TrainItem sel && l.Contains(sel.FilePath, StringComparer.OrdinalIgnoreCase)) UpdateExploreSpecs(sel);
        }

        // Flota: el gerente, un gestor o el administrador fijan las plazas del tren elegido (o vuelven a las de los archivos).
        async void SetTrainSeatsUi()
        {
            int i = FleetSelectedRow(); var co = _empSel;
            if (co == null || !(CanManage() || Supa.IsSuperadmin) || i < 0 || i >= _fleetRowTrain.Count || _fleetRowTrain[i].Length == 0) return;
            var t = _coTrains.Find(x => x.Id == _fleetRowTrain[i]);
            if (t == null) return;
            // las de los archivos: las de tu contenido si tienes el tren; si no, las que tenía al darlo de alta
            double files = t.FileSeats;
            if (_trainCon.TryGetValue(t.Id, out var con))
            {
                var c = _consistsAll.FirstOrDefault(x => string.Equals(x?.FilePath, con, StringComparison.OrdinalIgnoreCase));
                if (c != null) files = await Task.Run(() => AnalyzeComposition(c, ReadEngineSpecs(c.Locomotive?.FilePath ?? "").kmh, ignoreSeats: true).Capacity);
            }
            using var f = new Form
            {
                Text = Tr("Plazas del tren"), BackColor = Theme.Bg, ForeColor = Theme.Text, StartPosition = FormStartPosition.CenterParent,
                MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false, KeyPreview = true, Font = Theme.Font(9.5f), FormBorderStyle = FormBorderStyle.FixedDialog,
            };
            try { f.Icon = Icon; } catch { }
            f.ClientSize = new Size(Theme.Px(560), 200);
            var stack = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, AutoSize = true, BackColor = Theme.Bg, Padding = new Padding(18, 12, 18, 14) };
            stack.Controls.Add(new Label { Text = t.Name, AutoSize = true, MaximumSize = new Size(Theme.Px(520), 0), Font = Theme.Font(13f, FontStyle.Bold), Margin = new Padding(0, 0, 0, 4) });
            stack.Controls.Add(new Label { Text = Tr("Las plazas que fijes valen para todos los socios que conduzcan este tren: los viajeros que suben, el ingreso del servicio y el tope del servidor. No se cambia ningún archivo .eng ni .wag. Si no las fijas, cada maquinista usa las de sus archivos (PassengerCapacity)."),
                                         AutoSize = true, MaximumSize = new Size(Theme.Px(520), 0), ForeColor = Theme.Subtle, Margin = new Padding(0, 0, 0, 10) });
            stack.Controls.Add(new Label { Text = string.Format(Tr("Plazas de los archivos: {0}"), files > 0 ? files.ToString("N0", EsEs) : Tr("ninguna (sin PassengerCapacity)")), AutoSize = true, Margin = new Padding(0, 0, 0, 6) });
            var row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Theme.Bg, Margin = new Padding(0, 0, 0, 6) };
            row.Controls.Add(new Label { Text = Tr("Plazas para la empresa:"), AutoSize = true, Margin = new Padding(0, 6, 8, 0) });
            var num = new NumericUpDown { Minimum = 1, Maximum = 5000, Value = Math.Max(1, Math.Min(5000, t.Seats ?? (int)Math.Round(files > 0 ? files : 100))), Width = Theme.Px(110), BackColor = Theme.Surface2, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle, TextAlign = HorizontalAlignment.Right, Font = Theme.Font(11f, FontStyle.Bold) };
            row.Controls.Add(num);
            stack.Controls.Add(row);
            var msg = new Label { AutoSize = true, ForeColor = RedC, Margin = new Padding(0, 2, 0, 2) };
            stack.Controls.Add(msg);
            var btns = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Theme.Bg };
            var bSave = EmpButton(Tr("Guardar"), primary: true); bSave.Width = Theme.Px(140);
            var bFiles = EmpButton(Tr("Usar las de los archivos")); bFiles.Width = Theme.Px(220); bFiles.Margin = new Padding(8, 10, 2, 2); bFiles.Visible = t.Seats != null;
            var bCancel = EmpButton(Tr("Cancelar")); bCancel.Width = Theme.Px(120); bCancel.Margin = new Padding(8, 10, 2, 2);
            btns.Controls.Add(bSave); btns.Controls.Add(bFiles); btns.Controls.Add(bCancel);
            stack.Controls.Add(btns);
            f.Controls.Add(stack);
            async void Save(int? seats)
            {
                bSave.Enabled = bFiles.Enabled = false; msg.ForeColor = Theme.Subtle; msg.Text = Tr("Guardando…");
                var (_, err) = await Supa.RpcAsync("train_set_seats", new { p_company = co.Id, p_train = t.Id, p_seats = seats });
                if (err != null)
                {
                    bSave.Enabled = bFiles.Enabled = true; msg.ForeColor = RedC;
                    msg.Text = err.Contains("PGRST202") || err.Contains("Could not find") ? Tr("El servidor aún no admite fijar las plazas (falta trenes-plazas.sql).") : Tr("Error: ") + err;
                    return;
                }
                f.DialogResult = DialogResult.OK; f.Close();
            }
            bSave.Click += (s, e) => Save((int)num.Value);
            bFiles.Click += (s, e) => Save(null);
            bCancel.Click += (s, e) => f.Close();
            f.KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) f.Close(); };
            f.Load += (s, e) => { var ps = stack.GetPreferredSize(new Size(f.ClientSize.Width, 0)); f.ClientSize = new Size(Math.Max(f.ClientSize.Width, ps.Width), ps.Height + 4); CenterOnScreen(f); };
            _seatsOpen = f;
            DialogResult res;
            try { res = f.ShowDialog(this); } finally { _seatsOpen = null; }
            if (res != DialogResult.OK) return;
            Msg(_fleetMsg, string.Format(Tr("Plazas de «{0}» guardadas: valen para todos los socios."), t.Name), false);
            await LoadCoTrainsAsync(co.Id, force: true);
            ForgetTrainSpecs(_conTrain.Where(kv => kv.Value == t.Id).Select(kv => kv.Key));
            LoadFleet();
        }
        Form _seatsOpen;   // para las pruebas

        async Task<(string conFile, string conText, string image)> CoTrainFiles(string companyId, string trainId)
        {
            var (json, err) = await Supa.RpcAsync("train_get", new { p_company = companyId, p_train = trainId });
            if (err != null) return (null, null, null);
            try { using var d = JsonDocument.Parse(json); var r = d.RootElement; return (Str(r, "con_file"), Str(r, "con_text"), Str(r, "image")); }
            catch { return (null, null, null); }
        }

        // ============================ la composición 2D como imagen ============================
        // La composición guardada de un tren de la empresa o de una solicitud, en la caché de disco.
        static string SavedImageFile(string kind, string id)
            => Path.Combine(AppDataTidy.CacheRoot, "composiciones-guardadas", kind + "_" + string.Concat((id ?? "").Where(char.IsLetterOrDigit)) + ".jpg");

        static string SaveB64Image(string b64, string file)
        {
            try
            {
                if (string.IsNullOrEmpty(b64)) return null;
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                File.WriteAllBytes(file + ".tmp", Convert.FromBase64String(b64));
                File.Move(file + ".tmp", file, true);
                return file;
            }
            catch { return null; }
        }

        readonly Dictionary<string, Task<string>> _trainImgBusy = new(StringComparer.OrdinalIgnoreCase);

        Task<string> CoTrainImageFileAsync(string companyId, CoTrain t)
        {
            if (t == null || !t.HasImage) return Task.FromResult<string>(null);
            string file = SavedImageFile("tren", t.Id);
            if (File.Exists(file)) return Task.FromResult(file);
            if (_trainImgBusy.TryGetValue(t.Id, out var busy)) return busy;   // ya se está descargando: la misma descarga
            var task = DownloadCoTrainImageAsync(companyId, t.Id, file);
            if (task.IsCompleted) return task;
            _trainImgBusy[t.Id] = task;
            _ = task.ContinueWith(_ => _trainImgBusy.Remove(t.Id), TaskScheduler.FromCurrentSynchronizationContext());
            return task;
        }

        async Task<string> DownloadCoTrainImageAsync(string companyId, string trainId, string file)
        {
            var (_, _, image) = await CoTrainFiles(companyId, trainId);
            return await Task.Run(() => SaveB64Image(image, file));
        }

        async Task<Bitmap> ConsistStripAsync(TrainItem c)
        {
            string file = await ConsistStripFileAsync(c?.FilePath);
            return await Task.Run(() => LoadStripFile(file));
        }

        // JPEG en base64 (como mucho 1400 px de ancho) para guardarla en el servidor con el tren.
        static string ImageToB64(Bitmap bmp)
        {
            if (bmp == null) return null;
            try
            {
                Bitmap use = bmp;
                if (bmp.Width > 1400)
                {
                    use = new Bitmap(1400, Math.Max(1, (int)Math.Round(bmp.Height * 1400.0 / bmp.Width)));
                    using var g = Graphics.FromImage(use);
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.DrawImage(bmp, 0, 0, use.Width, use.Height);
                }
                var enc = ImageCodecInfo.GetImageEncoders().First(x => x.FormatID == ImageFormat.Jpeg.Guid);
                using var ps = new EncoderParameters(1); ps.Param[0] = new EncoderParameter(Encoder.Quality, 78L);
                using var ms = new MemoryStream();
                use.Save(ms, enc, ps);
                if (!ReferenceEquals(use, bmp)) use.Dispose();
                var b64 = Convert.ToBase64String(ms.ToArray());
                return b64.Length <= 400000 ? b64 : null;
            }
            catch { return null; }
        }

        static Bitmap B64ToImage(string b64)
        {
            if (string.IsNullOrEmpty(b64)) return null;
            try { using var ms = new MemoryStream(Convert.FromBase64String(b64)); using var img = Image.FromStream(ms); return new Bitmap(img); }
            catch { return null; }
        }

        // ============================ comprar o alquilar un tren ============================
        // Un ejemplar del tren: el de la empresa (trainId) o uno nuevo (con su .con, sus vehículos y su imagen). Devuelve
        // null si todo fue bien, o el error.
        async Task<string> BuyTrainCoreAsync(string companyId, string trainId, string name, string conFile, string conText,
                                             List<TrainVeh> vehicles, string imageB64, bool rent)
        {
            var (_, err) = await Supa.RpcAsync("buy_train", new
            {
                p_company = companyId, p_train = trainId, p_name = trainId == null ? name : null, p_con_file = trainId == null ? conFile : null,
                p_con_text = trainId == null ? conText : null, p_vehicles = trainId == null ? vehicles.Select(v => v.ToJson(v.Count)).ToArray() : null,
                p_image = trainId == null ? imageB64 : null, p_rent = rent
            });
            if (err != null) return err.Contains("PGRST202") || err.Contains("Could not find") ? Tr("El servidor aún no admite la compra de trenes (falta trenes-por-con.sql).") : err;
            _coTrainsCompany = null;            // la lista de trenes se vuelve a pedir
            lock (_fleetStatusCache) _fleetStatusCache.Clear();
            return null;
        }

        // Un tren del contenido de este equipo: si la empresa ya lo tiene, otro ejemplar; si no, se da de alta con él.
        // ask: confirmar antes (con el precio). Devuelve true si se compró.
        async Task<bool> BuyTrainAsync(TrainItem c, bool rent, Label msg, bool ask = true)
        {
            if (c?.FilePath == null || _empSel == null || !CanBuyTrains()) return false;
            var co = _empSel;
            var vehicles = await Task.Run(() => TrainVehiclesOfCached(c));
            if (vehicles.Count == 0 || !vehicles.Any(v => !v.Wagon)) { Msg(msg, Tr("No se ha podido leer ese tren o no lleva ninguna máquina de tracción."), true); return false; }
            double price = TrainPriceLocal(vehicles);
            await LoadCoTrainsAsync(co.Id, force: true);
            string key = TrainKey(vehicles);
            var known = _coTrains.FirstOrDefault(t => TrainKey(t.Vehicles) == key);
            int have = known != null ? TrainCopies(known.Id) : 0;
            if (ask)
            {
                string q = rent
                    ? string.Format(Tr("¿Alquilar «{0}»? Se paga {1} por servicio."), c.Name, TEur(price * _fleetRentPct))
                    : string.Format(Tr("¿Comprar «{0}» por {1}? La tesorería tiene {2}."), c.Name, TEur(price), TEur(co.Balance));
                if (have > 0) q += "\n\n" + string.Format(have == 1 ? Tr("La empresa ya tiene {0} ejemplar de este tren.") : Tr("La empresa ya tiene {0} ejemplares de este tren."), have);
                if (ThemedBox.Show(this, q, "SelectOR", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return false;
            }
            Msg(msg, rent ? Tr("Alquilando…") : Tr("Comprando…"), false);
            string conText = null, img = null;
            if (known == null)
            {
                try { conText = ConsistDoc.ReadText(c.FilePath); } catch { }
                if (string.IsNullOrEmpty(conText)) { Msg(msg, Tr("No se ha podido leer el .con de ese tren."), true); return false; }
                using var bmp = await ConsistStripAsync(c);
                img = ImageToB64(bmp);
            }
            string err = await BuyTrainCoreAsync(co.Id, known?.Id, c.Name, Path.GetFileNameWithoutExtension(c.FilePath), conText, vehicles, img, rent);
            if (err != null) { Msg(msg, Tr("Error: ") + err, true); return false; }
            Msg(msg, string.Format(rent ? Tr("«{0}» alquilado: ya está en Flota.") : Tr("«{0}» comprado: ya está en Flota."), c.Name), false);
            LoadCompanies(); LoadFleet();
            return true;
        }

        // «Comprar este tren» (Conducción libre y Horarios): comprar o alquilar.
        async void BuyLocalTrain(TrainItem c)
        {
            if (c?.FilePath == null || _empSel == null || !CanBuyTrains()) return;
            var co = _empSel;
            var vehicles = await Task.Run(() => TrainVehiclesOfCached(c));
            if (vehicles.Count == 0 || !vehicles.Any(v => !v.Wagon))
            {
                ThemedBox.Show(this, Tr("No se ha podido leer ese tren o no lleva ninguna máquina de tracción."), "SelectOR", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            double price = TrainPriceLocal(vehicles);
            await LoadCoTrainsAsync(co.Id, force: true);
            var known = _coTrains.FirstOrDefault(t => TrainKey(t.Vehicles) == TrainKey(vehicles));
            int have = known != null ? TrainCopies(known.Id) : 0;
            using var img = await ConsistStripAsync(c);
            int choice = ShowTrainBuyConfirm(c.Name, vehicles, img, price, have);
            if (choice == 0) return;
            var lbl = new Label();
            bool ok = await BuyTrainAsync(c, choice == 2, lbl, ask: false);
            ThemedBox.Show(this, lbl.Text, "SelectOR", MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }

        // Las ventanas de compra, solicitud y plazas se maquetan al cargarse (crecen hacia abajo desde la altura provisional):
        // ya con su tamaño final, se centran en la pantalla de SelectOR.
        void CenterOnScreen(Form f)
        {
            var wa = Screen.FromControl(this).WorkingArea;
            f.StartPosition = FormStartPosition.Manual;
            f.Location = new Point(wa.X + Math.Max(0, (wa.Width - f.Width) / 2), wa.Y + Math.Max(0, (wa.Height - f.Height) / 2));
        }

        // Ventana de compra: la composición, el desglose del precio vehículo por vehículo y lo que cuesta a la empresa.
        // 0 cancelar · 1 comprar · 2 alquilar.
        int ShowTrainBuyConfirm(string title, List<TrainVeh> vs, Bitmap image, double price, int have)
        {
            using var f = new Form
            {
                Text = Tr("Comprar este tren"), BackColor = Theme.Bg, ForeColor = Theme.Text, StartPosition = FormStartPosition.CenterParent,
                MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false, KeyPreview = true, Font = Theme.Font(9.5f), FormBorderStyle = FormBorderStyle.FixedDialog,
            };
            try { f.Icon = Icon; } catch { }
            int W = Theme.Px(1040);
            f.ClientSize = new Size(W + Theme.Px(40), 200);
            var stack = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, AutoSize = true, BackColor = Theme.Bg, Padding = new Padding(20, 14, 20, 16) };
            // cabecera: nombre, lo que lleva y, si la empresa ya lo tiene, cuántos
            var head = new TableLayoutPanel { AutoSize = false, ColumnCount = 2, RowCount = 1, BackColor = Theme.Bg, Margin = new Padding(0, 0, 0, 8), Width = W };
            head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var names = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Theme.Bg, Margin = new Padding(0) };
            names.Controls.Add(new Label { Text = Tr("COMPRAR ESTE TREN"), AutoSize = true, ForeColor = Theme.Subtle, Font = Theme.Font(8f, FontStyle.Bold), Margin = new Padding(0, 0, 0, 2) });
            names.Controls.Add(new Label { Text = title, AutoSize = true, MaximumSize = new Size(W - Theme.Px(260), 0), ForeColor = Theme.Accent, Font = Theme.Font(15f, FontStyle.Bold), Margin = new Padding(0, 0, 0, 2) });
            names.Controls.Add(new Label { Text = TrainSummary(vs), AutoSize = true, ForeColor = Theme.Subtle, Margin = new Padding(0) });
            head.Controls.Add(names, 0, 0);
            head.Height = names.GetPreferredSize(new Size(W - Theme.Px(260), 0)).Height + 4;
            if (have > 0)
                head.Controls.Add(new Label { Text = "✓ " + string.Format(have == 1 ? Tr("La empresa ya tiene {0} ejemplar") : Tr("La empresa ya tiene {0} ejemplares"), have), AutoSize = true, ForeColor = Theme.AccentHi, BackColor = Blend(Theme.Bg, Theme.Accent, 0.18f), Font = Theme.Font(9f, FontStyle.Bold), Padding = new Padding(10, 6, 10, 6), Anchor = AnchorStyles.Right | AnchorStyles.Top, Margin = new Padding(8, 4, 0, 0) }, 1, 0);
            stack.Controls.Add(head);
            // la composición entera
            if (image != null)
            {
                var strip = new StripBox { Image = image, Width = W, Height = Theme.Px(64), Margin = new Padding(0, 0, 0, 12) };
                stack.Controls.Add(strip);
            }
            // el desglose
            stack.Controls.Add(new Label { Text = Tr("DESGLOSE DEL PRECIO"), AutoSize = true, ForeColor = Theme.Subtle, Font = Theme.Font(8f, FontStyle.Bold), Margin = new Padding(0, 0, 0, 4) });
            stack.Controls.Add(BuildBreakdownTable(vs, W, out _));
            // lo que cuesta: compra, alquiler, tesorería y lo que queda
            double bal = _empSel?.Balance ?? 0;
            var tiles = new TableLayoutPanel { Width = W, Height = Theme.Px(66), AutoSize = false, ColumnCount = 4, RowCount = 1, BackColor = Theme.Bg, Margin = new Padding(0, 10, 0, 4) };
            for (int k = 0; k < 4; k++) tiles.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            tiles.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            tiles.Controls.Add(PriceChip(Tr("COMPRA (tren entero)"), Theme.Accent, out var vBuy), 0, 0);
            tiles.Controls.Add(PriceChip(Tr("ALQUILER / SERV."), Color.FromArgb(96, 165, 250), out var vRent), 1, 0);
            tiles.Controls.Add(PriceChip(Tr("TESORERÍA"), Theme.Text, out var vBal), 2, 0);
            tiles.Controls.Add(PriceChip(Tr("TRAS LA COMPRA"), bal - price >= 0 ? Theme.Text : RedC, out var vAfter), 3, 0);
            vBuy.Text = TEur(price); vRent.Text = TEur(price * _fleetRentPct); vBal.Text = TEur(bal); vAfter.Text = TEur(bal - price);
            foreach (Control ch in tiles.Controls) { ch.Dock = DockStyle.Fill; if (ch is Card cc) { cc.AutoSize = false; } }
            stack.Controls.Add(tiles);
            if (price > bal)
                stack.Controls.Add(new Label { Text = Tr("La tesorería no llega para comprarlo; se puede alquilar."), AutoSize = true, ForeColor = RedC, Margin = new Padding(0, 2, 0, 0) });
            // botones, a la derecha
            var btns = new FlowLayoutPanel { AutoSize = false, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, BackColor = Theme.Bg, Width = W, Height = Theme.Px(56), Margin = new Padding(0, 8, 0, 0) };
            var bBuy = EmpButton(have > 0 ? Tr("Comprar otro") : Tr("Comprar"), primary: true); bBuy.Width = Theme.Px(190); bBuy.Height = 40; bBuy.Enabled = price <= bal;
            var bRent = EmpButton(Tr("Alquilar")); bRent.Width = Theme.Px(150); bRent.Height = 40; bRent.Margin = new Padding(8, 10, 2, 2);
            var bCancel = EmpButton(Tr("Cancelar")); bCancel.Width = Theme.Px(130); bCancel.Height = 40; bCancel.Margin = new Padding(8, 10, 2, 2);
            bCancel.BaseColor = Theme.Surface2; bCancel.TextColor = Theme.Subtle;
            btns.Controls.Add(bBuy); btns.Controls.Add(bRent); btns.Controls.Add(bCancel);
            stack.Controls.Add(btns);
            f.Controls.Add(stack);
            int result = 0;
            bBuy.Click += (s, e) => { result = 1; f.Close(); };
            bRent.Click += (s, e) => { result = 2; f.Close(); };
            bCancel.Click += (s, e) => f.Close();
            f.KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) f.Close(); };
            f.Load += (s, e) => { var ps = stack.GetPreferredSize(new Size(f.ClientSize.Width, 0)); f.ClientSize = new Size(Math.Max(f.ClientSize.Width, ps.Width), ps.Height + 4); CenterOnScreen(f); };
            _trainBuyOpen = f;
            try { f.ShowDialog(this); } finally { _trainBuyOpen = null; }
            return result;
        }
        Form _trainBuyOpen;   // para las pruebas

        // La tabla del desglose (ventana de compra y «Desglose del precio»): una fila por vehículo con cómo se tasa.
        Control BuildBreakdownTable(List<TrainVeh> vs, int width, out double total)
        {
            var card = new Card { Width = width, Fill = Theme.Surface, Radius = 10, Padding = new Padding(12, 8, 12, 8), Margin = new Padding(0) };
            var grid = new TableLayoutPanel { AutoSize = true, ColumnCount = 5, BackColor = Theme.Surface, Margin = new Padding(0), Location = new Point(12, 8) };
            int inner = width - Theme.Px(24);
            int[] widths = { (int)(inner * 0.24), (int)(inner * 0.17), (int)(inner * 0.33), (int)(inner * 0.06), 0 };
            widths[4] = inner - widths[0] - widths[1] - widths[2] - widths[3] - Theme.Px(4);
            foreach (var w in widths) grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, w));
            Label Cell(string text, Color? c = null, bool right = false, bool bold = false, Color? back = null) => new Label
            {
                Text = text, AutoSize = false, Dock = DockStyle.Fill, Height = Theme.Px(30), ForeColor = c ?? Theme.Text, AutoEllipsis = true, BackColor = back ?? Theme.Surface,
                TextAlign = right ? ContentAlignment.MiddleRight : ContentAlignment.MiddleLeft, Font = Theme.Font(bold ? 8f : 9.25f, bold ? FontStyle.Bold : FontStyle.Regular), Margin = new Padding(0), Padding = new Padding(4, 0, 4, 0)
            };
            foreach (var (h, r) in new[] { (Tr("VEHÍCULO"), false), (Tr("TIPO"), false), (Tr("CÓMO SE TASA"), false), (Tr("Nº"), true), (Tr("PRECIO"), true) })
                grid.Controls.Add(Cell(h, Theme.Subtle, r, bold: true));
            total = 0; int k = 0;
            foreach (var v in vs)
            {
                var back = (k++ % 2 == 0) ? Blend(Theme.Surface, Theme.Bg, 0.35f) : Theme.Surface;
                double unit = UnitPriceLocal(v), sub = unit * Math.Max(1, v.Count);
                total += sub;
                grid.Controls.Add(Cell(v.Name + (string.IsNullOrEmpty(v.Folder) || string.Equals(v.Folder, v.Name, StringComparison.OrdinalIgnoreCase) ? "" : "  ·  " + v.Folder), back: back));
                grid.Controls.Add(Cell(UnitTypeText(v), Theme.Subtle, back: back));
                grid.Controls.Add(Cell(UnitPriceBasis(v), Theme.Subtle, back: back));
                grid.Controls.Add(Cell(Math.Max(1, v.Count) > 1 ? Math.Max(1, v.Count) + " ×" : "1", null, true, back: back));
                grid.Controls.Add(Cell(Math.Max(1, v.Count) > 1 ? TEur(unit) + "  →  " + TEur(sub) : TEur(sub), null, true, back: back));
            }
            grid.Controls.Add(Cell(Tr("TOTAL DEL TREN"), Theme.Subtle, bold: true)); grid.Controls.Add(Cell("")); grid.Controls.Add(Cell(""));
            grid.Controls.Add(Cell(vs.Sum(v => Math.Max(1, v.Count)).ToString(), Theme.Subtle, true));
            grid.Controls.Add(Cell(TEur(total), Theme.Accent, true, bold: false));
            card.Controls.Add(grid);
            card.Height = grid.GetPreferredSize(Size.Empty).Height + 16;   // a la medida de sus filas
            return card;
        }

        // ============================ el maquinista solicita un tren ============================
        async void RequestLocalTrain(TrainItem c)
        {
            if (c?.FilePath == null || _empSel == null || !CanRequestPurchase()) return;
            var co = _empSel;
            var vehicles = await Task.Run(() => TrainVehiclesOfCached(c));
            if (vehicles.Count == 0 || !vehicles.Any(v => !v.Wagon)) { ThemedBox.Show(this, Tr("No se ha podido leer ese tren o no lleva ninguna máquina de tracción."), "SelectOR", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            var img = await ConsistStripAsync(c);

            using var f = new Form
            {
                Text = Tr("Solicitar compra"), BackColor = Theme.Bg, ForeColor = Theme.Text, StartPosition = FormStartPosition.CenterParent,
                MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false, KeyPreview = true, Font = Theme.Font(9.5f), FormBorderStyle = FormBorderStyle.FixedDialog,
            };
            try { f.Icon = Icon; } catch { }
            f.ClientSize = new Size(Theme.Px(700), 200);
            var stack = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, AutoSize = true, BackColor = Theme.Bg, Padding = new Padding(16, 10, 16, 12) };
            stack.Controls.Add(new Label { Text = string.Format(Tr("Solicitar a {0}"), co.Name), AutoSize = true, Font = Theme.Font(13f, FontStyle.Bold), Margin = new Padding(0, 0, 0, 2) });
            stack.Controls.Add(new Label { Text = c.Name + "  ·  " + TrainSummary(vehicles), AutoSize = true, MaximumSize = new Size(Theme.Px(660), 0), ForeColor = Theme.Subtle, Margin = new Padding(0, 0, 0, 8) });
            if (img != null) stack.Controls.Add(new PictureBox { Image = img, SizeMode = PictureBoxSizeMode.Zoom, Width = Theme.Px(664), Height = Math.Min(Theme.Px(80), (int)(Theme.Px(664) * (double)img.Height / Math.Max(1, img.Width))) + 4, BackColor = Theme.Surface, Margin = new Padding(0, 0, 0, 8) });
            stack.Controls.Add(new Label { Text = Tr("Se envían su .con y su composición. Si el gerente o un gestor lo compran, el tren queda en la empresa y te lo puedes añadir desde Flota."), AutoSize = true, MaximumSize = new Size(Theme.Px(660), 0), ForeColor = Theme.Subtle, Margin = new Padding(0, 0, 0, 6) });
            var note = new TextBox { Width = Theme.Px(664), BackColor = Theme.Surface2, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle, PlaceholderText = Tr("Comentario para el gerente y los gestores (opcional)"), Margin = new Padding(0, 4, 0, 6) };
            stack.Controls.Add(note);
            var msg = new Label { AutoSize = true, ForeColor = RedC, Margin = new Padding(0, 2, 0, 2) };
            stack.Controls.Add(msg);
            var btns = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Theme.Bg };
            var bSend = EmpButton(Tr("Enviar solicitud"), primary: true); bSend.Width = Theme.Px(200);
            var bCancel = EmpButton(Tr("Cancelar")); bCancel.Width = Theme.Px(130); bCancel.Margin = new Padding(8, 10, 2, 2);
            btns.Controls.Add(bSend); btns.Controls.Add(bCancel); stack.Controls.Add(btns);
            f.Controls.Add(stack);
            bCancel.Click += (s, e) => f.Close();
            f.KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) f.Close(); };
            string conText = null;
            try { conText = ConsistDoc.ReadText(c.FilePath); } catch { }
            bSend.Click += async (s, e) =>
            {
                if (string.IsNullOrEmpty(conText)) { msg.Text = Tr("No se ha podido leer el .con de ese tren."); return; }
                bSend.Enabled = false; msg.ForeColor = Theme.Subtle; msg.Text = Tr("Enviando…");
                string name = c.Name.Length > 120 ? c.Name.Substring(0, 117) + "…" : c.Name;
                var (_, err) = await Supa.RpcAsync("request_train", new
                {
                    p_company = co.Id, p_kind = "train", p_name = name, p_vehicles = vehicles.Select(v => v.ToJson(v.Count)).ToArray(),
                    p_con_file = Path.GetFileNameWithoutExtension(c.FilePath), p_con_text = conText,
                    p_image = ImageToB64(img), p_note = note.Text.Trim().Length > 0 ? note.Text.Trim() : null
                });
                if (err != null)
                {
                    bSend.Enabled = true; msg.ForeColor = RedC;
                    if (err.Contains("PGRST202") || err.Contains("Could not find")) { f.Close(); RequestPurchase(c); return; }   // servidor sin trenes-empresa.sql: la solicitud de máquina de siempre
                    msg.Text = Tr("Error: ") + err; return;
                }
                f.DialogResult = DialogResult.OK; f.Close();
            };
            f.Load += (s, e) => { var ps = stack.GetPreferredSize(new Size(f.ClientSize.Width, 0)); f.ClientSize = new Size(Math.Max(f.ClientSize.Width, ps.Width), ps.Height + 4); CenterOnScreen(f); };
            _trainReqOpen = f;
            DialogResult res;
            try { res = f.ShowDialog(this); } finally { _trainReqOpen = null; }
            img?.Dispose();
            if (res == DialogResult.OK)
                ThemedBox.Show(this, string.Format(Tr("Solicitud enviada a {0}: el gerente y los gestores la verán en Compra → Solicitudes."), co.Name), "SelectOR", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        Form _trainReqOpen;   // para las pruebas

        // ============================ ejemplares y trenes del contenido ============================
        // Ejemplares de cada tren de la empresa elegida (los cuenta LoadFleet).
        readonly Dictionary<string, int> _trainCopyCount = new(StringComparer.OrdinalIgnoreCase);
        int TrainCopies(string trainId) => trainId != null && _trainCopyCount.TryGetValue(trainId, out var n) ? n : 0;

        // Qué .con de este equipo son trenes de la empresa: .con → tren y tren → su .con aquí. Solo se leen los .con con
        // la misma máquina de cabeza que algún tren de la empresa (son pocos).
        Dictionary<string, string> _conTrain = new(StringComparer.OrdinalIgnoreCase), _trainCon = new(StringComparer.OrdinalIgnoreCase);
        int _trainMatchSeq;
        async Task MatchLocalTrainsAsync()
        {
            var co = _empSel; int seq = ++_trainMatchSeq;
            if (co == null || !await LoadCoTrainsAsync(co.Id) || seq != _trainMatchSeq) return;
            var trains = _coTrains.ToList();
            var leads = new HashSet<string>(trains.Select(t => t.Lead).Where(l => l.Length > 0), StringComparer.OrdinalIgnoreCase);
            var all = _consistsAll.Where(c => c?.FilePath != null).ToList();
            var (conTrain, trainCon) = await Task.Run(() =>
            {
                var byKey = trains.GroupBy(t => TrainKey(t.Vehicles)).ToDictionary(g => g.Key, g => g.First().Id);
                var ct = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var tc = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var c in all)
                {
                    string lead = LeadEngineName(c);
                    if (lead == null || !leads.Contains(lead)) continue;
                    if (!byKey.TryGetValue(TrainKey(TrainVehiclesOfCached(c)), out var id)) continue;
                    ct[c.FilePath] = id;
                    if (!tc.ContainsKey(id)) tc[id] = c.FilePath;
                }
                return (ct, tc);
            });
            if (seq != _trainMatchSeq || co != _empSel) return;
            _conTrain = conTrain; _trainCon = trainCon;
            OnLocalTrainsMatched();
        }

        // Flota: el .con de un ejemplar, a tu contenido.
        async void AddFleetTrainToContent()
        {
            var co0 = _empSel;
            if (co0 != null && (_fleetCards?.MarkedCount ?? 0) > 1)
            {
                // varios: los trenes elegidos que aún no tienes en tu contenido (sin preguntar por el material)
                var ids = new List<string>();
                foreach (var (r, _) in FleetSelection()) { string t0 = r < _fleetRowTrain.Count ? _fleetRowTrain[r] : ""; if (t0.Length > 0 && !_trainCon.ContainsKey(t0) && !ids.Contains(t0)) ids.Add(t0); }
                if (ids.Count == 0) { Msg(_fleetMsg, Tr("Ya tienes todos esos trenes en tu contenido."), false); return; }
                int ok = 0; string err0 = null;
                Msg(_fleetMsg, Tr("Descargando los trenes…"), false);
                foreach (var tid in ids)
                {
                    var tt = _coTrains.Find(x => x.Id == tid);
                    var (cf, ct, _) = await CoTrainFiles(co0.Id, tid);
                    if (string.IsNullOrEmpty(ct)) { err0 ??= Tr("No se ha podido descargar el tren."); continue; }
                    string fl = WriteConToContent(cf ?? tt?.ConFile ?? tt?.Name ?? "Tren", ct, co0.Name, out var we);
                    if (fl == null) err0 ??= we; else { ok++; lock (_trainVehCache) _trainVehCache.Remove(fl); }
                }
                Msg(_fleetMsg, string.Format(Tr("{0} de {1} trenes añadidos a tu contenido."), ok, ids.Count) + (err0 != null ? "  " + Tr("Error: ") + err0 : ""), err0 != null);
                _fleetCards?.ClearMarks();
                return;
            }
            int i = FleetSelectedRow(); var co = _empSel;
            if (co == null || i < 0 || i >= _fleetRowTrain.Count || string.IsNullOrEmpty(_fleetRowTrain[i])) return;
            string trainId = _fleetRowTrain[i];
            var t = _coTrains.FirstOrDefault(x => x.Id == trainId);
            string name = t?.Name ?? Tr("Tren");
            var missing = t == null ? new List<string>() : t.Vehicles.Where(v => ResolveCarFile(v.Name, v.Folder) == null)
                                                        .Select(v => string.IsNullOrEmpty(v.Folder) ? v.Name : v.Folder).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (missing.Count > 0
                && ThemedBox.Show(this, string.Format(Tr("Te falta material para «{0}»: {1}.\n\nSe añade igualmente el .con, pero no podrás conducirlo hasta que instales ese material. ¿Seguir?"), name, string.Join(", ", missing.Take(4)) + (missing.Count > 4 ? "…" : "")),
                                   "SelectOR", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            Msg(_fleetMsg, Tr("Descargando el tren…"), false);
            var (conFile, conText, _) = await CoTrainFiles(co.Id, trainId);
            if (string.IsNullOrEmpty(conText)) { Msg(_fleetMsg, Tr("No se ha podido descargar el tren."), true); return; }
            string file = WriteConToContent(conFile ?? t?.ConFile ?? name, conText, co.Name, out var err);
            if (file == null) { Msg(_fleetMsg, Tr("No se ha podido guardar: ") + err, true); return; }
            Msg(_fleetMsg, string.Format(Tr("«{0}» añadido a tu contenido: {1}"), name, Path.GetFileName(file)), false);
            lock (_trainVehCache) _trainVehCache.Remove(file);
            await Task.Delay(1500);
            LoadFleet();
        }

        // Escribe el .con en TRAINS\CONSISTS del contenido elegido. Si ya hay uno con ese nombre y es distinto, se
        // guarda con el nombre de la empresa al lado (nunca se sobrescribe el del usuario). Devuelve el archivo o null.
        string WriteConToContent(string conFile, string conText, string tag, out string error)
        {
            error = null;
            string dir = Path.Combine(_curFolder?.Path ?? "", "TRAINS", "CONSISTS");
            if (_curFolder == null || !Directory.Exists(dir)) { error = Tr("Esta carpeta de contenido no tiene TRAINS\\CONSISTS."); return null; }
            string baseName = SafeFileName(string.IsNullOrWhiteSpace(conFile) ? "Tren" : conFile);
            string file = Path.Combine(dir, baseName + ".con");
            try
            {
                if (File.Exists(file))
                {
                    string cur = ConsistDoc.ReadText(file);
                    if (string.Equals(cur?.Trim(), conText.Trim(), StringComparison.Ordinal)) return file;   // ya es ese
                    file = Path.Combine(dir, SafeFileName(baseName + " (" + tag + ")") + ".con");
                    for (int k = 2; File.Exists(file) && !string.Equals(ConsistDoc.ReadText(file)?.Trim(), conText.Trim(), StringComparison.Ordinal); k++)
                        file = Path.Combine(dir, SafeFileName(baseName + " (" + tag + " " + k + ")") + ".con");
                    if (File.Exists(file)) return file;
                }
                // UTF-16 con BOM, como los .con de MSTS y como los guarda el Editor
                File.WriteAllText(file, conText, new System.Text.UnicodeEncoding(false, true));
            }
            catch (Exception e) { error = e.Message; return null; }
            ClearContentCaches();
            ReloadConsistsAfterEdit(changed: new[] { file });
            return file;
        }

        // ============================ al ponerse de servicio ============================
        sealed class TrainCopy { public string Id, TrainId, Lead, Plate, Status; }

        // Los ejemplares de la empresa con su estado real; null si el servidor aún no tiene trenes-por-con.sql.
        async Task<List<TrainCopy>> TrainCopiesAsync(string companyId)
        {
            var tOpen = OpenServiceUnits(companyId);
            var (json, err) = await SelectVehicles("id,train_id,lead_eng,plate,status,km_since_maint,maint_interval_km",
                                                   "&company_id=eq." + Uri.EscapeDataString(companyId) + "&kind=eq.train&order=created_at.asc");
            var open = await tOpen;
            if (err != null) return null;
            var list = new List<TrainCopy>();
            try
            {
                using var d = JsonDocument.Parse(json);
                foreach (var e in d.RootElement.EnumerateArray())
                    list.Add(new TrainCopy
                    {
                        Id = Str(e, "id"), TrainId = Str(e, "train_id"), Lead = Str(e, "lead_eng"), Plate = Str(e, "plate"),
                        Status = RealUnitStatus(Str(e, "status"), Str(e, "id"), open, Num(e, "km_since_maint"), Num(e, "maint_interval_km"))
                    });
            }
            catch { return null; }
            return list;
        }

        readonly HashSet<string> _trainWarned = new(StringComparer.OrdinalIgnoreCase);

        // Cuántas máquinas lleva un tren (un automotor doble lleva dos cabezas).
        static int Heads(IEnumerable<TrainVeh> vs) => vs == null ? -1 : vs.Where(v => !v.Wagon).Sum(v => Math.Max(1, v.Count));

        // La unidad con la que se abre el servicio: un ejemplar libre de ESTE tren; si no, (salvo en «Obligatorio») uno de
        // otro tren con la misma máquina de cabeza; si no, una máquina anterior de cabeza. (id, null) o (null, motivo).
        async Task<(string id, string reason)> ResolveServiceUnitAsync(string companyId, TrainItem c, List<(string name, string folder)> engs, bool quiet = false)
        {
            _lastUnitPlate = "";
            var copiesT = TrainCopiesAsync(companyId);
            bool trains = await LoadCoTrainsAsync(companyId, force: true);
            var copies = await copiesT;
            if (!trains || copies == null || c?.FilePath == null) return await ResolveCompanyUnitReason(companyId, engs);   // servidor sin trenes por .con
            var vehicles = await Task.Run(() => TrainVehiclesOfCached(c));
            string key = TrainKey(vehicles), lead = vehicles.FirstOrDefault(v => !v.Wagon)?.Name ?? LeadEngineName(c) ?? "";
            var exact = new HashSet<string>(_coTrains.Where(t => TrainKey(t.Vehicles) == key).Select(t => t.Id), StringComparer.OrdinalIgnoreCase);
            var mine = copies.Where(x => exact.Contains(x.TrainId)).ToList();
            var free = mine.FirstOrDefault(x => x.Status == "available");
            if (free != null) { _lastUnitPlate = free.Plate; _lastUnitReasonCode = "ok"; return (free.Id, null); }
            if (_trainMode != "enforce" && lead.Length > 0)
            {
                int heads = Heads(vehicles);
                var same = copies.FirstOrDefault(x => x.Status == "available" && !exact.Contains(x.TrainId) && string.Equals(x.Lead, lead, StringComparison.OrdinalIgnoreCase)
                                                      && Heads(_coTrains.Find(t => t.Id == x.TrainId)?.Vehicles) == heads);
                if (same != null)
                {
                    string other = _coTrains.FirstOrDefault(t => t.Id == same.TrainId)?.Name ?? lead;
                    if (_trainMode == "warn" && !quiet && _trainWarned.Add(companyId + "|" + key))
                        ThemedBox.Show(this, string.Format(Tr("«{0}» no es un tren de la empresa: el servicio se hace con un ejemplar de «{1}», que lleva la misma máquina de cabeza."), c.Name, other),
                                        "SelectOR", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    _lastUnitPlate = same.Plate; _lastUnitReasonCode = "ok";
                    return (same.Id, null);
                }
            }
            // las máquinas sueltas de antes valen como cabeza de cualquier tren que las lleve
            var (vid, reason) = await ResolveCompanyUnitReason(companyId, engs);
            if (vid != null) return (vid, null);
            string co = _empOnDutyCompany?.Name ?? _empSel?.Name ?? "";
            if (mine.Count > 0)
            {
                bool inUse = mine.Any(x => x.Status == "in_use"), maint = mine.Any(x => x.Status == "maintenance_due");
                _lastUnitReasonCode = inUse && !maint ? "inuse" : maint && !inUse ? "maint" : "unavail";
                string cuantos = mine.Count == 1 ? string.Format(Tr("el único ejemplar de «{0}»"), c.Name) : string.Format(Tr("los {0} ejemplares de «{1}»"), mine.Count, c.Name);
                return (null, inUse && !maint ? string.Format(Tr("Tren no operativo: {0} está(n) en servicio con otro maquinista. Espera a que termine o compra otro."), cuantos)
                            : maint && !inUse ? string.Format(Tr("Tren no operativo: {0} está(n) pasando por el taller, que es automático al cumplir sus km. Vuelve a intentarlo en unos minutos."), cuantos)
                            : string.Format(Tr("Tren no operativo: {0} no está(n) disponible(s)."), cuantos));
            }
            if (_lastUnitReasonCode == "notfleet")
                return (null, string.Format(Tr("«{0}» no es un tren de {1}. El gerente o un gestor lo compran en Empresas → Compra; también puedes pedirlo con «Solicitar compra»."), c.Name, co));
            return (null, reason);
        }

        // «Operativo / No operativo» (Conducción libre y Horarios), con los trenes de la empresa: ok · inuse · maint ·
        // unavail · none (no es de la empresa). Sin ventanas ni consultas de más (se usa la lista ya cargada).
        async Task<string> TrainStatusCodeAsync(string companyId, TrainItem c)
        {
            if (c?.FilePath == null || !await LoadCoTrainsAsync(companyId)) return "none";
            var copies = await TrainCopiesAsync(companyId);
            if (copies == null || copies.Count == 0) return "none";
            var vehicles = await Task.Run(() => TrainVehiclesOfCached(c));
            string key = TrainKey(vehicles), lead = vehicles.FirstOrDefault(v => !v.Wagon)?.Name ?? "";
            var exact = new HashSet<string>(_coTrains.Where(t => TrainKey(t.Vehicles) == key).Select(t => t.Id), StringComparer.OrdinalIgnoreCase);
            var mine = copies.Where(x => exact.Contains(x.TrainId)).ToList();
            if (mine.Any(x => x.Status == "available")) return "ok";
            if (mine.Count == 0) return "none";   // solo su composición exacta: un simple no hace «operativo» al doble
            bool inUse = mine.Any(x => x.Status == "in_use"), maint = mine.Any(x => x.Status == "maintenance_due");
            return inUse && !maint ? "inuse" : maint && !inUse ? "maint" : "unavail";
        }

        // ============================ Ajustes (administrador): el modo ============================
        FlowLayoutPanel _trainModeBar;
        Control BuildTrainModeRow()
        {
            var box = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, BackColor = Theme.Bg, Margin = new Padding(0, 8, 0, 0) };
            box.Controls.Add(EmpHeader("TRENES DE EMPRESA (GLOBAL, ADMINISTRADOR)"));
            box.Controls.Add(EmpFieldLabel(Tr("Al ponerse de servicio con un .con que no es un tren de la empresa (o sin ejemplar libre): «Aviso» deja usar un ejemplar de otro tren con la misma máquina de cabeza y lo avisa; «Obligatorio», no. Las máquinas anteriores valen siempre como cabeza.")));
            _trainModeBar = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Theme.Bg, Margin = new Padding(0, 4, 0, 0) };
            string[] modes = { "off", "warn", "enforce" }; string[] names = { "No se comprueba", "Aviso", "Obligatorio" };
            for (int k = 0; k < modes.Length; k++) { string m = modes[k]; var b = BankChip(Tr(names[k]), k == 1); b.Click += (s, e) => SetTrainMode(m); _trainModeBar.Controls.Add(b); }
            box.Controls.Add(_trainModeBar);
            return box;
        }

        async void LoadTrainMode()
        {
            if (_trainModeBar == null) return;
            var (jm, err) = await Supa.RpcAsync("train_mode", new { });
            if (err != null) { _trainModeBar.Parent.Visible = false; return; }
            _trainMode = JsonText(jm);
            SetChipActive(_trainModeBar, _trainMode == "off" ? 0 : _trainMode == "enforce" ? 2 : 1);
        }

        async void SetTrainMode(string mode)
        {
            if (!Supa.IsSuperadmin || mode == _trainMode) return;
            var (_, err) = await Supa.RpcAsync("train_set_mode", new { p_mode = mode });
            if (err != null) { ThemedBox.Show(this, Tr("Error: ") + err, "SelectOR", MessageBoxButtons.OK, MessageBoxIcon.Warning); LoadTrainMode(); return; }
            _trainMode = mode;
            SetChipActive(_trainModeBar, mode == "off" ? 0 : mode == "enforce" ? 2 : 1);
        }
    }
}
