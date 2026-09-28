// Solicitudes de COMPRA: un maquinista pide a su empresa que compre (o alquile) la máquina de un tren
// que no tiene. En Exploración y Horarios el mismo botón que el gerente/gestor ve como «Comprar este
// tren» es, para el maquinista, «Solicitar compra». Las solicitudes llegan a Empresas → Compra →
// Solicitudes, donde el gerente o un gestor la compra, la alquila o la rechaza (servidor:
// sql/solicitudes-compra.sql).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SelectOR
{
    public partial class MainMenuForm
    {
        StyledTable _reqList;
        Label _reqMsg;
        FlowLayoutPanel _buyTabs;                 // [Comprar | Solicitudes (n)] en Compra
        Control _buyShopView, _buyReqView;        // las dos vistas de Compra
        readonly List<PurchaseReq> _reqs = new();
        sealed class PurchaseReq
        {
            public string Id, Date, User, Machine, Folder, Consist, Note;
            public Specs Data;     // datos de tasación que envió el maquinista (null = servidor antiguo o sin datos)
            public bool Local;     // el gerente/gestor tiene esa máquina, en esa librea, en su contenido
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
                MessageBox.Show(this, Tr("Ese tren no tiene ninguna máquina de tracción reconocible."), "SelectOR", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
                    var tot = ConsistTotals(c);
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
            MessageBox.Show(this,
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
            t.Controls.Add(EmpIntro("Máquinas que los socios piden que la empresa compre o alquile. «Comprar» y «Alquilar» abren la compra de esa máquina (con su tasación); «Rechazar» la descarta."));
            _reqList = EmpTable();
            _reqList.SetColumns(
                new StyledTable.Col("FECHA", 92),
                new StyledTable.Col("MAQUINISTA", 150),
                new StyledTable.Col("MÁQUINA", 220),
                new StyledTable.Col("EN TU EQUIPO", 150),
                new StyledTable.Col("TREN", 200),
                new StyledTable.Col("MENSAJE", 200, true));
            _reqList.Dock = DockStyle.Fill;
            _reqList.DoubleClick += (s, e) => ResolveSelectedRequest("bought");
            t.Controls.Add(_reqList);
            var row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Theme.Bg, Margin = new Padding(0, 6, 0, 0) };
            var buy = EmpButton(Tr("Comprar"), primary: true); buy.Width = 150; buy.Click += (s, e) => ResolveSelectedRequest("bought");
            var rent = EmpButton(Tr("Alquilar")); rent.Width = 150; rent.Click += (s, e) => ResolveSelectedRequest("rented");
            var rej = EmpButton(Tr("Rechazar")); rej.Width = 150; rej.BaseColor = Theme.Surface2; rej.HoverColor = Color.FromArgb(150, 60, 60); rej.TextColor = RedC;
            rej.Click += (s, e) => ResolveSelectedRequest("rejected");
            row.Controls.Add(buy); row.Controls.Add(rent); row.Controls.Add(rej);
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
                                   ? JsonSerializer.Deserialize<Specs>(sp.GetRawText()) : null
                        });
                }
                catch { }
            foreach (var r in _reqs) r.Local = LocalBuyMachine(r) != null;
            if (_reqList != null)
            {
                _reqList.BeginReload(co.Id);
                _reqList.ClearRows();
                foreach (var r in _reqs)
                    _reqList.AddRow(new[] { r.Date, r.User.Length > 0 ? r.User : "—", r.Machine + (string.IsNullOrEmpty(r.Folder) || string.Equals(r.Folder, r.Machine, StringComparison.OrdinalIgnoreCase) ? "" : "  ·  " + r.Folder)
                                            ,
                                            // ¿Tienes esa máquina, en esa librea? Si no, se compra con los datos del maquinista.
                                            r.Local ? "✓  " + Tr("la tienes") : "✗  " + Tr(r.Data != null ? "no: datos del maquinista" : "no la tienes"),
                                            r.Consist.Length > 0 ? r.Consist : "—", r.Note },
                                    new Color?[] { Theme.Subtle, null, Theme.AccentHi, r.Local ? Theme.Accent : ColOrange, Theme.Subtle, null }, null, r.Id);
                if (_reqs.Count == 0)
                    _reqList.SetEmpty(err != null && err.IndexOf("PGRST202", StringComparison.OrdinalIgnoreCase) >= 0
                        ? Tr("El servidor aún no admite solicitudes de compra.")
                        : err != null ? Tr("Error: ") + err : Tr("No hay solicitudes de compra pendientes."));
                _reqList.EndReload();
            }
            UpdateRequestCount();
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
            int i = _reqList.SelectedRow;
            if (i < 0 || i >= _reqs.Count) { Msg(_reqMsg, Tr("Selecciona una solicitud de la lista."), true); return; }
            var r = _reqs[i];
            if (status == "rejected")
            {
                if (MessageBox.Show(this, string.Format(Tr("¿Rechazar la solicitud de {0} para «{1}»?"), r.User, r.Machine), "SelectOR",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                var (_, err) = await Supa.RpcAsync("resolve_purchase_request", new { p_request = r.Id, p_status = "rejected" });
                if (err != null) { Msg(_reqMsg, Tr("Error: ") + err, true); return; }
                Msg(_reqMsg, Tr("Solicitud rechazada."), false);
                LoadPurchaseRequests();
                return;
            }
            bool rent = status == "rented";
            bool ok;
            var local = LocalBuyMachine(r);
            if (local != null)
            {
                // A la pestaña «Comprar», con esa máquina (y esa librea) elegida en la lista.
                ShowBuyTab(0);
                if (_fleetConsistSearch != null && _fleetConsistSearch.Box.Text.Length > 0) _fleetConsistSearch.Box.Text = "";
                int idx = _fleetEngList?.Items.IndexOf(local) ?? -1;
                if (idx < 0) { ShowBuyTab(1); Msg(_reqMsg, string.Format(Tr("«{0}» no figura entre las máquinas de este contenido."), r.Machine), true); return; }
                _fleetEngList.SelectedIndex = idx;
                _fleetEngList.TopIndex = Math.Max(0, idx - 3);
                ok = await AcquireMachine(rent);
                if (!ok) return;   // cancelada o con error: el mensaje ya está en Comprar; la solicitud sigue pendiente
            }
            else if (r.Data != null)
            {
                var d = r.Data;
                string q = string.Format(rent
                        ? Tr("No tienes «{0}» en tu contenido. ¿Alquilarla con los datos que envió {1} ({2} coches, {3} t)? El precio lo calcula el servidor.")
                        : Tr("No tienes «{0}» en tu contenido. ¿Comprarla con los datos que envió {1} ({2} coches, {3} t)? El precio lo calcula el servidor."),
                    r.Machine + (string.IsNullOrEmpty(r.Folder) ? "" : " · " + r.Folder), r.User, d.cars, d.mass.ToString("N0", EsEs));
                if (MessageBox.Show(this, q, "SelectOR", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                Msg(_reqMsg, rent ? Tr("Alquilando…") : Tr("Comprando…"), false);
                string err = await RpcAcquire(rent, _empSel.Id, r.Machine, r.Folder, d.etype, d.automotor,
                                              d.kw, d.kmh, d.cars, d.mass, d.brake, d.capacity, d.comfort);
                if (err != null) { Msg(_reqMsg, Tr("Error: ") + err, true); return; }
                LoadCompanies();
                LoadFleet();
            }
            else
            {
                Msg(_reqMsg, string.Format(Tr("«{0}» no está en tu contenido y la solicitud no trae sus datos: pide al maquinista que la vuelva a enviar."), r.Machine), true);
                return;
            }
            var (_, rerr) = await Supa.RpcAsync("resolve_purchase_request", new { p_request = r.Id, p_status = status });
            string done = (rent ? Tr("Vehículo alquilado.") : Tr("Vehículo comprado.")) + "  " +
                (rerr == null ? string.Format(Tr("Solicitud de {0} atendida."), r.User) : Tr("No se pudo cerrar la solicitud: ") + rerr);
            Msg(local != null ? _buyMsg : _reqMsg, done, rerr != null);
            LoadPurchaseRequests();
        }
    }
}
