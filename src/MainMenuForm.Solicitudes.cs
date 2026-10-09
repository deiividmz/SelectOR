// Solicitudes de COMPRA: un maquinista pide a su empresa que compre (o alquile) la máquina de un tren
// que no tiene. En Conducción libre y Horarios el mismo botón que el gerente/gestor ve como «Comprar este
// tren» es, para el maquinista, «Solicitar compra». Las solicitudes llegan a Empresas → Compra →
// Solicitudes, donde el gerente o un gestor la compra, la alquila o la rechaza (servidor:
// sql/solicitudes-compra.sql).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SelectOR
{
    public partial class MainMenuForm
    {
        TrainRowList _reqList;
        Label _reqMsg;
        FlowLayoutPanel _buyTabs;                 // [Comprar | Solicitudes (n)] en Compra
        Control _buyShopView, _buyReqView;        // las dos vistas de Compra
        readonly List<PurchaseReq> _reqs = new();
        sealed class PurchaseReq
        {
            public string Id, Date, User, Machine, Folder, Consist, Note;
            public Specs Data;     // datos de tasación que envió el maquinista (null = servidor antiguo o sin datos)
            public bool Local;     // el gerente/gestor tiene esa máquina, en esa librea, en su contenido
            public string Kind = "machine";   // machine · train (tren completo, con su .con) · units (vehículos sueltos)
            public List<TrainVeh> Vehicles = new();
            public bool HasCon, HasImage;
        }

        // Datos con los que se tasa la compra (los mismos que pide buy_vehicle / rent_vehicle).
        sealed class Specs
        {
            public string etype { get; set; } = "";
            public bool automotor { get; set; }
            public double kw { get; set; }
            public double kmh { get; set; }
            public int cars { get; set; }
            public double mass { get; set; }
            public double brake { get; set; }
            public double capacity { get; set; }
            public double comfort { get; set; }
        }

        // ¿Puede pedir compras? Cualquier socio de la empresa elegida que no pueda comprar él mismo.
        bool CanRequestPurchase() => Supa.IsLoggedIn && _empSel != null && _myRole != null && !CanBuyTrains();

        // ---------------- maquinista: pedir la compra ----------------
        async void RequestPurchase(TrainItem c)
        {
            if (c == null || !CanRequestPurchase()) return;
            string machine = MachineNameForConsist(c);
            if (machine == null)
            {
                ThemedBox.Show(this, Tr("Ese tren no tiene ninguna máquina de tracción reconocible."), "SelectOR", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string folder = null;
            foreach (var en in EngineNamesOf(c))
                if (string.Equals(en.name, machine, StringComparison.OrdinalIgnoreCase)) { folder = en.folder; break; }
            // Quien atiende la solicitud puede no tener este tren (o no en esta librea): se envían los datos
            // con los que se tasa, leídos de su .eng y de este tren, que es la composición de referencia.
            Specs specs = null;
            try
            {
                string eng = ResolveCarFile(machine, folder);
                if (!string.IsNullOrEmpty(eng) && System.IO.File.Exists(eng))
                {
                    var sp = BuySpecsOf(new BuyMachine { Name = machine, Folder = folder ?? "", Path = eng });
                    // un automotor, con su formación (este tren); una locomotora, con sus propios datos
                    var tot = sp.automotor ? ConsistTotals(c) : (0, 0, 0, 0);
                    specs = new Specs
                    {
                        etype = sp.type, automotor = sp.automotor, kw = sp.kw, kmh = sp.kmh, comfort = sp.comfort,
                        cars = tot.cars > 0 ? tot.cars : sp.cars, mass = tot.cars > 0 ? tot.mass : sp.mass,
                        brake = tot.cars > 0 ? tot.brake : sp.brake, capacity = tot.cars > 0 ? tot.capacity : sp.capacity
                    };
                }
            }
            catch { specs = null; }

            string note;
            using (var d = new TextPromptDialog(Tr("Solicitar compra"),
                       string.Format(Tr("Se pedirá al gerente y a los gestores de «{0}» que compren o alquilen «{1}». Mensaje para ellos (opcional):"), _empSel.Name, machine), "", "", Tr("Enviar")))
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                note = (d.Value ?? "").Trim();
            }
            var (_, err) = await Supa.RpcAsync("request_purchase", new
            { p_company = _empSel.Id, p_machine = machine, p_folder = folder, p_consist = c.Name, p_note = note, p_specs = specs });
            if (err != null && err.IndexOf("p_specs", StringComparison.OrdinalIgnoreCase) >= 0)   // servidor con el SQL sin los datos
                (_, err) = await Supa.RpcAsync("request_purchase", new
                { p_company = _empSel.Id, p_machine = machine, p_folder = folder, p_consist = c.Name, p_note = note });
            if (err != null && (err.IndexOf("PGRST202", StringComparison.OrdinalIgnoreCase) >= 0 || err.IndexOf("request_purchase", StringComparison.OrdinalIgnoreCase) >= 0))
                err = Tr("El servidor aún no admite solicitudes de compra.");
            ThemedBox.Show(this,
                err == null ? string.Format(Tr("Solicitud enviada: el gerente o un gestor de «{0}» decidirán si compran o alquilan «{1}»."), _empSel.Name, machine)
                            : Tr("No se pudo enviar la solicitud: ") + err,
                "SelectOR", MessageBoxButtons.OK, err == null ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }

        // ---------------- gerente / gestor: la tabla de solicitudes ----------------
        // Envuelve el escaparate de Compra con dos pestañas: «Comprar» (lo de siempre) y «Solicitudes».
        Control WrapBuyWithRequests(Control shop)
        {
            _buyShopView = shop;
            _buyReqView = BuildPurchaseRequestsView();
            _buyReqView.Visible = false;
            _buyTabs = MakeSubTabs(new[] { "Comprar", "Solicitudes" }, i =>
            {
                _buyShopView.Visible = i == 0; _buyReqView.Visible = i == 1;
                if (i == 1) LoadPurchaseRequests();
            });
            var host = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            shop.Dock = DockStyle.Fill; _buyReqView.Dock = DockStyle.Fill;
            host.Controls.Add(shop); host.Controls.Add(_buyReqView);
            var outer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Theme.Bg };
            outer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _buyTabs.Dock = DockStyle.Fill;
            outer.Controls.Add(_buyTabs, 0, 0); outer.Controls.Add(host, 0, 1);
            return outer;
        }

        void ShowBuyTab(int i)
        {
            if (_buyTabs == null || i >= _buyTabs.Controls.Count) return;
            typeof(Control).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?.Invoke(_buyTabs.Controls[i], new object[] { EventArgs.Empty });
        }

        Control BuildPurchaseRequestsView()
        {
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, BackColor = Theme.Bg };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.Controls.Add(EmpHeader("SOLICITUDES DE COMPRA DE LOS MAQUINISTAS"));
            t.Controls.Add(EmpIntro("Los trenes que los socios piden que la empresa compre o alquile, con su composición. «Comprar» y «Alquilar» añaden un ejemplar a la flota (aunque no tengas su material); «Rechazar» la descarta."));
            // Una fila por solicitud, con el tren entero en 2D: quién lo pide, si tienes su material, su precio y su mensaje.
            _thumbs ??= new VehicleThumbs(this);
            _reqList = new TrainRowList
            {
                Dock = DockStyle.Fill, Thumbs = _thumbs, Margin = new Padding(0, 4, 0, 4),
                TitleOf = o => o is PurchaseReq r ? (r.Kind == "train" ? r.Machine : r.Kind == "units" ? Tr("Vehículos sueltos") + ": " + r.Machine : Tr("Máquina") + ": " + r.Machine) : "",
                PillsOf = o => o is PurchaseReq r ? new List<(string, Color, Color)>
                {
                    (string.Format(Tr("Pide {0}"), r.User.Length > 0 ? r.User : "—"), Theme.AccentHi, Color.FromArgb(40, 76, 175, 80)),
                    (r.Date, Theme.Subtle, CardPaint.Rail),
                    (r.Local ? "✓ " + Tr("tienes su material") : "✗ " + Tr("no tienes su material"), r.Local ? Theme.Accent : ColOrange, CardPaint.Rail),
                } : null,
                SubOf = o => o is PurchaseReq r ? string.Join("  ·  ", new[] { r.Vehicles.Count > 0 ? TrainSummary(r.Vehicles) : string.Format(Tr("Tren: {0}"), r.Consist.Length > 0 ? r.Consist : "—"),
                                                                              string.IsNullOrEmpty(r.Note) ? null : "«" + r.Note + "»" }.Where(x => !string.IsNullOrEmpty(x))) : "",
                RightOf = o => o is PurchaseReq r && r.Kind == "train" && r.Vehicles.Any(v => !v.Wagon) ? TEur(TrainPriceLocal(r.Vehicles)) : null,
                PathOf = o => o is PurchaseReq r ? RequestImagePath(r) : null,
            };
            _reqList.MultiSelect = true;   // Ctrl / Mayús: comprar, alquilar o rechazar varias a la vez
            _reqList.ItemActivated += o => ResolveSelectedRequest("bought");
            _reqList.InfoClicked += o =>
            {
                if (o is PurchaseReq r && r.Vehicles.Count > 0) ShowPriceBreakdown(r.Machine, r.Vehicles, string.Format(Tr("lo pide {0}"), r.User));
                else Msg(_reqMsg, Tr("Esa solicitud no trae los datos de sus vehículos."), true);
            };
            t.Controls.Add(_reqList);
            var row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Theme.Bg, Margin = new Padding(0, 6, 0, 0) };
            var buy = EmpButton(Tr("Comprar"), primary: true); buy.Width = 150; buy.Click += (s, e) => ResolveSelectedRequest("bought");
            var rent = EmpButton(Tr("Alquilar")); rent.Width = 150; rent.Click += (s, e) => ResolveSelectedRequest("rented");
            var rej = EmpButton(Tr("Rechazar")); rej.Width = 150; rej.BaseColor = Theme.Surface2; rej.HoverColor = Color.FromArgb(150, 60, 60); rej.TextColor = RedC;
            rej.Click += (s, e) => ResolveSelectedRequest("rejected");
            // tren completo: además, su .con a tu contenido (aunque no lo compres)
            var addMine = EmpButton(Tr("Añadir a mi contenido")); addMine.Width = 210; addMine.Margin = new Padding(8, 10, 2, 2); addMine.Click += (s, e) => AddRequestTrainToContent();
            row.Controls.Add(buy); row.Controls.Add(rent); row.Controls.Add(rej); row.Controls.Add(addMine);
            t.Controls.Add(row);
            _reqMsg = EmpMsg(); t.Controls.Add(_reqMsg);
            return t;
        }

        // Recarga las solicitudes pendientes (y el contador de la pestaña y del menú lateral).
        async void LoadPurchaseRequests()
        {
            if (_empSel == null || !(CanManage() || Supa.IsSuperadmin)) { _reqs.Clear(); UpdateRequestCount(); return; }
            var co = _empSel;
            var (json, err) = await Supa.RpcAsync("list_purchase_requests", new { p_company = co.Id });
            if (co != _empSel) return;   // cambió la empresa mientras se consultaba
            _reqs.Clear();
            if (err == null)
                try
                {
                    using var d = JsonDocument.Parse(json);
                    foreach (var e in d.RootElement.EnumerateArray())
                        _reqs.Add(new PurchaseReq
                        {
                            Id = Str(e, "id"), Date = FmtDate(Str(e, "created_at")), User = Str(e, "username"),
                            Machine = Str(e, "machine"), Folder = Str(e, "folder"), Consist = Str(e, "consist"), Note = Str(e, "note"),
                            Data = e.TryGetProperty("specs", out var sp) && sp.ValueKind == JsonValueKind.Object
                                   ? JsonSerializer.Deserialize<Specs>(sp.GetRawText()) : null,
                            // trenes-empresa.sql: tren completo o vehículos sueltos
                            Kind = Str(e, "kind").Length > 0 ? Str(e, "kind") : "machine",
                            Vehicles = e.TryGetProperty("vehicles", out var vs) && vs.ValueKind == JsonValueKind.Array ? vs.EnumerateArray().Select(TrainVeh.FromJson).ToList() : new List<TrainVeh>(),
                            HasCon = e.TryGetProperty("has_con", out var hc) && hc.ValueKind == JsonValueKind.True,
                            HasImage = e.TryGetProperty("has_image", out var hi) && hi.ValueKind == JsonValueKind.True,
                        });
                }
                catch { }
            foreach (var r in _reqs) r.Local = r.Kind == "machine" ? LocalBuyMachine(r) != null : r.Vehicles.All(v => ResolveCarFile(v.Name, v.Folder) != null);
            if (_reqList != null)
            {
                string prev = (_reqList.SelectedItem as PurchaseReq)?.Id;
                _reqList.BeginUpdate();
                _reqList.Items.Clear(); _reqList.Items.AddRange(_reqs.ToArray());
                _reqList.EmptyText = err != null && err.IndexOf("PGRST202", StringComparison.OrdinalIgnoreCase) >= 0
                    ? Tr("El servidor aún no admite solicitudes de compra.")
                    : err != null ? Tr("Error: ") + err : Tr("No hay solicitudes de compra pendientes.");
                _reqList.EndUpdate();
                int sel = _reqs.FindIndex(r => r.Id == prev);
                _reqList.SelectedIndex = sel >= 0 ? sel : _reqs.Count > 0 ? 0 : -1;
                FetchRequestImages(co);
            }
            UpdateRequestCount();
        }

        // La composición de cada solicitud: la que envió el maquinista (se descarga una vez) o, si tienes ese tren en tu
        // contenido, la suya.
        string RequestImagePath(PurchaseReq r)
        {
            string saved = SavedImageFile("solicitud", r.Id);
            if (File.Exists(saved)) return saved;
            return r.Consist.Length > 0 ? ResolveConsist(r.Consist)?.FilePath : null;
        }

        async void FetchRequestImages(EmpCompany co)
        {
            foreach (var r in _reqs.ToList())
            {
                if (!r.HasImage || File.Exists(SavedImageFile("solicitud", r.Id))) continue;
                var (json, err) = await Supa.RpcAsync("purchase_request_files", new { p_request = r.Id });
                if (err != null || co != _empSel) return;
                string img = null;
                try { using var d = JsonDocument.Parse(json); img = Str(d.RootElement, "image"); } catch { }
                if (await Task.Run(() => SaveB64Image(img, SavedImageFile("solicitud", r.Id))) != null) _reqList?.Invalidate();
            }
        }

        // «Solicitudes (2)» en la pestaña de Compra y «Compra (2)» en el menú lateral.
        void UpdateRequestCount()
        {
            int n = _reqs.Count;
            if (_buyTabs != null && _buyTabs.Controls.Count > 1 && _buyTabs.Controls[1] is RoundButton rb)
            {
                rb.Text = Tr("Solicitudes") + (n > 0 ? "  (" + n + ")" : "");
                using var f = Theme.Font(9.75f, FontStyle.Bold);
                rb.Width = TextRenderer.MeasureText(rb.Text, f).Width + 34;
                rb.Invalidate();
            }
            if (_empSubtabs != null && _empSubtabs.Length > 10 && _empSubtabs[10] != null)
            {
                _empSubtabs[10].Text = Tr(SubNames[10]) + (n > 0 ? "  (" + n + ")" : "");
                _empSubtabs[10].Invalidate();
            }
        }

        // La máquina pedida en el contenido de ESTE equipo: mismo nombre y misma carpeta (librea). Si la
        // solicitud no trae carpeta, basta el nombre.
        BuyMachine LocalBuyMachine(PurchaseReq r)
        {
            foreach (var m in _buyMachines)
                if (string.Equals(m.Name, r.Machine, StringComparison.OrdinalIgnoreCase)
                    && (string.IsNullOrEmpty(r.Folder) || string.Equals(m.Folder ?? "", r.Folder, StringComparison.OrdinalIgnoreCase)))
                    return m;
            return null;
        }

        // Comprar / Alquilar:
        //  · si esa máquina (en esa librea) está en tu contenido, se abre la compra de siempre, con su
        //    tasación y la elección de composición;
        //  · si no la tienes, se compra o alquila con los datos que envió el maquinista (el precio lo
        //    calcula el servidor, como siempre).
        // La solicitud queda resuelta solo si la compra sale bien. Rechazar: solo la marca.
        async void ResolveSelectedRequest(string status)
        {
            if (_reqList == null) return;
            if (_reqList.MarkedCount > 1) { await ResolveRequestsAsync(_reqList.SelectedItems.OfType<PurchaseReq>().ToList(), status); return; }
            int i = _reqList.SelectedIndex;
            if (i < 0 || i >= _reqs.Count) { Msg(_reqMsg, Tr("Selecciona una solicitud de la lista."), true); return; }
            var r = _reqs[i];
            if (status == "rejected")
            {
                if (ThemedBox.Show(this, string.Format(Tr("¿Rechazar la solicitud de {0} para «{1}»?"), r.User, r.Machine), "SelectOR",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                var (_, err) = await Supa.RpcAsync("resolve_purchase_request", new { p_request = r.Id, p_status = "rejected" });
                if (err != null) { Msg(_reqMsg, Tr("Error: ") + err, true); return; }
                Msg(_reqMsg, Tr("Solicitud rechazada."), false);
                LoadPurchaseRequests();
                return;
            }
            bool rent = status == "rented";
            if (r.Kind == "train") { ResolveTrainRequest(r, rent); return; }   // un tren (TrenesEmpresa)
            // Solicitudes antiguas (una máquina o vehículos sueltos): ya no se compran sueltos. Si tienes el tren en el que
            // iba la máquina, se compra ese tren; si no, que el maquinista pida el tren.
            var c = r.Kind == "machine" && r.Consist.Length > 0 ? ResolveConsist(r.Consist) : null;
            if (c == null)
            {
                Msg(_reqMsg, string.Format(Tr("Solicitud antigua de {0}: ya no se compran máquinas ni vehículos sueltos. Recházala y pídele que solicite el tren."), r.User), true);
                return;
            }
            if (!await BuyTrainAsync(c, rent, _reqMsg)) return;
            var (_, rerr) = await Supa.RpcAsync("resolve_purchase_request", new { p_request = r.Id, p_status = status });
            Msg(_reqMsg, rerr == null ? string.Format(Tr("Solicitud de {0} atendida."), r.User) : Tr("No se pudo cerrar la solicitud: ") + rerr, rerr != null);
            LoadPurchaseRequests();
        }
        // Solicitud de un tren: un ejemplar (con los datos y el .con que envió el maquinista, aunque no tengas su material).
        // Si la empresa ya tiene ese tren, solo se añade el ejemplar. La solicitud queda resuelta al comprar.
        // Varias solicitudes a la vez: rechazarlas, o comprar (o alquilar) los trenes que piden, con una sola confirmación.
        async Task ResolveRequestsAsync(List<PurchaseReq> list, string status)
        {
            var co = _empSel; if (co == null || list.Count == 0) return;
            if (status == "rejected")
            {
                if (ThemedBox.Show(this, string.Format(Tr("¿Rechazar las {0} solicitudes elegidas?"), list.Count), "SelectOR", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                int n = 0; foreach (var r in list) { var (_, e) = await Supa.RpcAsync("resolve_purchase_request", new { p_request = r.Id, p_status = "rejected" }); if (e == null) n++; }
                Msg(_reqMsg, string.Format(Tr("{0} de {1} solicitudes rechazadas."), n, list.Count), n < list.Count);
                LoadPurchaseRequests(); return;
            }
            bool rent = status == "rented";
            var trains = list.Where(r => r.Kind == "train" && r.Vehicles.Any(v => !v.Wagon)).ToList();
            if (trains.Count == 0) { Msg(_reqMsg, Tr("Ninguna de esas solicitudes es de un tren (las antiguas, de máquinas o vehículos sueltos, solo se pueden rechazar)."), true); return; }
            double price = trains.Sum(r => TrainPriceLocal(r.Vehicles));
            string q = rent ? string.Format(Tr("¿Alquilar los {0} trenes que piden ({1} por servicio entre todos)?"), trains.Count, TEur(price * _fleetRentPct))
                            : string.Format(Tr("¿Comprar los {0} trenes que piden por {1}? La tesorería tiene {2}."), trains.Count, TEur(price), TEur(co.Balance));
            if (trains.Count < list.Count) q += "\n\n" + string.Format(Tr("{0} son antiguas (máquinas o vehículos sueltos): se quedan sin atender."), list.Count - trains.Count);
            if (ThemedBox.Show(this, q, "SelectOR", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            int ok = 0; string err = null;
            foreach (var r in trains)
            {
                Msg(_reqMsg, string.Format(rent ? Tr("Alquilando {0} de {1}…") : Tr("Comprando {0} de {1}…"), ok + 1, trains.Count), false);
                string e = await BuyRequestedTrainAsync(r, rent);
                if (e == null) ok++; else { err ??= e; if (!rent) break; }
            }
            Msg(_reqMsg, string.Format(Tr("{0} de {1} solicitudes atendidas."), ok, trains.Count) + (err != null ? "  " + Tr("Error: ") + err : ""), err != null);
            LoadCompanies(); LoadFleet(); LoadPurchaseRequests();
        }

        // Compra (o alquila) el tren de una solicitud y la da por atendida; null si todo fue bien.
        async Task<string> BuyRequestedTrainAsync(PurchaseReq r, bool rent)
        {
            var co = _empSel; if (co == null) return Tr("Selecciona una empresa.");
            await LoadCoTrainsAsync(co.Id, force: true);
            var known = _coTrains.FirstOrDefault(t => TrainKey(t.Vehicles) == TrainKey(r.Vehicles));
            string conFile = null, conText = null, image = null;
            if (known == null && (r.HasCon || r.HasImage))
            {
                var (json, err) = await Supa.RpcAsync("purchase_request_files", new { p_request = r.Id });
                if (err == null)
                    try { using var d = JsonDocument.Parse(json); conFile = Str(d.RootElement, "con_file"); conText = Str(d.RootElement, "con_text"); image = Str(d.RootElement, "image"); }
                    catch { }
            }
            if (known == null && string.IsNullOrEmpty(conText)) return Tr("No se ha podido descargar el .con del tren solicitado.");
            string perr = await BuyTrainCoreAsync(co.Id, known?.Id, r.Machine, conFile ?? r.Machine, conText, r.Vehicles, string.IsNullOrEmpty(image) ? null : image, rent);
            if (perr != null) return perr;
            var (_, rerr) = await Supa.RpcAsync("resolve_purchase_request", new { p_request = r.Id, p_status = rent ? "rented" : "bought" });
            return rerr;
        }

        async void ResolveTrainRequest(PurchaseReq r, bool rent)
        {
            var co = _empSel; if (co == null) return;
            if (r.Vehicles.Count == 0 || !r.Vehicles.Any(v => !v.Wagon)) { Msg(_reqMsg, Tr("La solicitud no trae los datos del tren: pide al maquinista que la vuelva a enviar."), true); return; }
            double price = TrainPriceLocal(r.Vehicles);
            await LoadCoTrainsAsync(co.Id, force: true);
            var known = _coTrains.FirstOrDefault(t => TrainKey(t.Vehicles) == TrainKey(r.Vehicles));
            string q = rent ? string.Format(Tr("¿Alquilar «{0}» para {1}? Se paga {2} por servicio."), r.Machine, r.User, TEur(price * _fleetRentPct))
                            : string.Format(Tr("¿Comprar «{0}» para {1} por {2}? La tesorería tiene {3}."), r.Machine, r.User, TEur(price), TEur(co.Balance));
            if (known != null) q += "\n\n" + string.Format(Tr("La empresa ya tiene este tren («{0}»): se añade otro ejemplar."), known.Name);
            if (ThemedBox.Show(this, q, "SelectOR", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            string conFile = null, conText = null, image = null;
            if (known == null && (r.HasCon || r.HasImage))
            {
                var (json, err) = await Supa.RpcAsync("purchase_request_files", new { p_request = r.Id });
                if (err == null)
                    try { using var d = JsonDocument.Parse(json); conFile = Str(d.RootElement, "con_file"); conText = Str(d.RootElement, "con_text"); image = Str(d.RootElement, "image"); }
                    catch { }
            }
            if (known == null && string.IsNullOrEmpty(conText)) { Msg(_reqMsg, Tr("No se ha podido descargar el .con del tren solicitado."), true); return; }
            Msg(_reqMsg, rent ? Tr("Alquilando…") : Tr("Comprando…"), false);
            string perr = await BuyTrainCoreAsync(co.Id, known?.Id, r.Machine, conFile ?? r.Machine, conText, r.Vehicles, string.IsNullOrEmpty(image) ? null : image, rent);
            if (perr != null) { Msg(_reqMsg, Tr("Error: ") + perr, true); return; }
            var (_, rerr) = await Supa.RpcAsync("resolve_purchase_request", new { p_request = r.Id, p_status = rent ? "rented" : "bought" });
            Msg(_reqMsg, rerr == null ? string.Format(Tr("Solicitud de {0} atendida."), r.User) : Tr("No se pudo cerrar la solicitud: ") + rerr, rerr != null);
            LoadCompanies(); LoadFleet(); LoadPurchaseRequests();
        }

        // El .con de un tren solicitado, a tu carpeta TRAINS\CONSISTS (para conducirlo o revisarlo).
        async void AddRequestTrainToContent()
        {
            int i = _reqList?.SelectedIndex ?? -1;
            if (i < 0 || i >= _reqs.Count) { Msg(_reqMsg, Tr("Selecciona una solicitud de la lista."), true); return; }
            var r = _reqs[i];
            if (r.Kind != "train" || !r.HasCon) { Msg(_reqMsg, Tr("Esa solicitud no trae un tren (.con)."), true); return; }
            var (json, err) = await Supa.RpcAsync("purchase_request_files", new { p_request = r.Id });
            if (err != null) { Msg(_reqMsg, Tr("Error: ") + err, true); return; }
            string conFile = null, conText = null;
            try { using var d = JsonDocument.Parse(json); conFile = Str(d.RootElement, "con_file"); conText = Str(d.RootElement, "con_text"); } catch { }
            if (string.IsNullOrEmpty(conText)) { Msg(_reqMsg, Tr("No se ha podido descargar el tren."), true); return; }
            string file = WriteConToContent(conFile ?? r.Machine, conText, r.User, out var werr);
            if (file == null) { Msg(_reqMsg, Tr("No se ha podido guardar: ") + werr, true); return; }
            var miss = r.Vehicles.Where(v => ResolveCarFile(v.Name, v.Folder) == null).Select(v => string.IsNullOrEmpty(v.Folder) ? v.Name : v.Folder).Distinct().ToList();
            Msg(_reqMsg, string.Format(Tr("«{0}» añadido a tu contenido: {1}"), r.Machine, System.IO.Path.GetFileName(file))
                         + (miss.Count > 0 ? "  " + string.Format(Tr("Te falta material: {0}"), string.Join(", ", miss.Take(4))) : ""), miss.Count > 0);
        }
    }
}
