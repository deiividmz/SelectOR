// SERVIDORES SELECTOR (salas-multijugador.sql), en Multijugador: las salas oficiales que crea el administrador (ruta,
// IP, puerto, contraseña, límite de jugadores, quién puede entrar, carné mínimo, abierta o cerrada).
//  · «Entrar»: el servidor comprueba el permiso, el carné, el sitio y la contraseña y da la IP y el puerto; SelectOR
//    pone la ruta de la sala, el modo cliente y esos datos, y lanza Open Rails con el recorrido y el tren elegidos en
//    Conducción libre. Mientras se juega avisa cada 45 s (para contar los jugadores) y al cerrar Open Rails sale.
//  · El administrador: «Nueva sala…», «Editar…» y «Quitar».

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SelectOR
{
    public partial class MainMenuForm
    {
        RoomCardGrid _roomGrid; Control _roomPage, _roomAdminBar; Label _roomMsg;
        RoundButton _roomEditBtn, _roomDelBtn;
        string _roomPending, _roomJoined;   // sala a la que se va a entrar · en la que se está jugando
        Timer _roomBeat;
        bool _roomsMissing;
        int _mpSection;                      // 0 servidores SelectOR · 1 servidores públicos
        FlowLayoutPanel _mpSections;

        // «Servidores SelectOR» (las salas) o «Servidores públicos» (la lista de siempre, con sus filtros).
        void SetMpSection(int i)
        {
            _mpSection = i;
            bool rooms = i == 0;
            if (_roomPage != null) _roomPage.Visible = rooms;
            if (_srvGrid != null) _srvGrid.Parent.Visible = !rooms;
            if (_srvTabs != null) _srvTabs.Visible = !rooms;
            if (_srvAge != null) _srvAge.Visible = !rooms;
            if (rooms) LoadRooms(); else if (_serversAll == null || _serversAll.Count == 0) LoadServers();
        }

        static bool NoRoomsOnServer(string err) => err != null && (err.Contains("PGRST202") || err.Contains("Could not find the function"));

        Control BuildRoomPage()
        {
            var page = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(0, 6, 0, 0) };
            _roomGrid = new RoomCardGrid
            {
                Dock = DockStyle.Fill,
                LblJoin = Tr("Entrar"), LblInside = Tr("Estás dentro"), LblNoRoute = Tr("Ruta que no tienes en tu contenido"), LblHave = "✓ " + Tr("la tienes"),
                LblNoPlayers = Tr("sin jugadores"), LblPass = "🔒 " + Tr("Con contraseña"), LblFree = "🔓 " + Tr("Sin contraseña"), LblAll = "👥 " + Tr("Para todos"),
                LblCompanies = "🏢 " + Tr("Empresas autorizadas"), LblUsers = "⭐ " + Tr("Por invitación"), LblPoints = "🪪 " + Tr("Carné ≥ {0} puntos"),
                LblClosed = "⛔ " + Tr("Cerrada"), LblFull = Tr("Llena"),
                ThumbOf = name =>
                {
                    var r = RouteByName(name);
                    if (r == null) return null;
                    var imgs = ContentImages.ForRoute(r.Path);
                    return ImageCache.Get(imgs.thumb, 300, 184, () => { try { BeginInvoke((Action)(() => _roomGrid?.Invalidate())); } catch { } });
                }
            };
            _roomGrid.Join += it => JoinRoom(it);
            _roomGrid.Selected += it => UpdateRoomButtons();
            _roomGrid.EmptyText = Tr("Cargando…");
            // administrador: crear, editar y quitar salas
            _roomAdminBar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = Theme.Px(52), FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Theme.Bg, Padding = new Padding(0, Theme.Px(8), 0, 0), Visible = false };
            var bNew = DialogKit.Fit(DialogKit.Primary("＋  " + Tr("Nueva sala…")), 160); bNew.Margin = new Padding(0, 0, Theme.Px(8), 0);
            _roomEditBtn = DialogKit.Fit(DialogKit.Secondary("✎  " + Tr("Editar…")), 120); _roomEditBtn.Margin = new Padding(0, 0, Theme.Px(8), 0);
            _roomDelBtn = DialogKit.Fit(DialogKit.Secondary(Tr("Quitar")), 100); _roomDelBtn.TextColor = Color.FromArgb(229, 115, 115); _roomDelBtn.Margin = new Padding(0);
            bNew.Click += (s, e) => EditRoomUi(null);
            _roomEditBtn.Click += (s, e) => { var it = _roomGrid.SelectedItem; if (it != null) EditRoomUi(it.Id); };
            _roomDelBtn.Click += (s, e) => DeleteRoom();
            _roomMsg = new Label { AutoSize = true, ForeColor = Theme.Subtle, Font = Theme.Font(9f), Margin = new Padding(Theme.Px(10), Theme.Px(10), 0, 0) };
            _roomAdminBar.Controls.AddRange(new Control[] { bNew, _roomEditBtn, _roomDelBtn, _roomMsg });
            page.Controls.Add(_roomGrid); page.Controls.Add(_roomAdminBar);
            _roomPage = page;
            UpdateRoomButtons();
            return page;
        }

        ORTS.Menu.Route RouteByName(string name)
            => _routesAll.FirstOrDefault(x => string.Equals(x.Name?.Trim(), name?.Trim(), StringComparison.OrdinalIgnoreCase)
                                           || string.Equals(System.IO.Path.GetFileName((x.Path ?? "").TrimEnd('\\', '/')), name?.Trim(), StringComparison.OrdinalIgnoreCase));

        void UpdateRoomButtons()
        {
            if (_roomAdminBar == null) return;
            _roomAdminBar.Visible = Supa.IsLoggedIn && Supa.IsSuperadmin;
            bool sel = _roomGrid?.SelectedItem != null;
            _roomEditBtn.Enabled = sel; _roomDelBtn.Enabled = sel;
        }

        async void LoadRooms()
        {
            if (_roomGrid == null) return;
            UpdateRoomButtons();
            if (!Supa.IsLoggedIn) { _roomGrid.EmptyText = Tr("Inicia sesión en Empresas para ver los servidores SelectOR y entrar en sus salas."); _roomGrid.SetItems(new List<RoomCardGrid.Item>()); return; }
            var (json, err) = await Supa.RpcAsync("room_list", new { });
            _roomsMissing = NoRoomsOnServer(err);
            if (err != null)
            {
                _roomGrid.EmptyText = _roomsMissing ? Tr("El servidor aún no tiene los servidores SelectOR (falta salas-multijugador.sql).") : Tr("Error: ") + err;
                _roomGrid.SetItems(new List<RoomCardGrid.Item>());
                return;
            }
            var list = new List<RoomCardGrid.Item>();
            try
            {
                using var d = JsonDocument.Parse(json);
                foreach (var e in d.RootElement.EnumerateArray())
                {
                    var names = new List<string>();
                    if (e.TryGetProperty("player_names", out var pn) && pn.ValueKind == JsonValueKind.Array)
                        foreach (var n in pn.EnumerateArray()) if (n.ValueKind == JsonValueKind.String) names.Add(n.GetString());
                    var it = new RoomCardGrid.Item
                    {
                        Id = Str(e, "id"), Name = Str(e, "name"), Description = Str(e, "description"), Route = Str(e, "route"), Access = Str(e, "access"),
                        MaxPlayers = (int)Num(e, "max_players"), Players = (int)Num(e, "players"), MinPoints = (int)Num(e, "min_points"), Names = names.ToArray(),
                        HasPassword = e.TryGetProperty("has_password", out var hp) && hp.ValueKind == JsonValueKind.True,
                        Active = !(e.TryGetProperty("active", out var ac) && ac.ValueKind == JsonValueKind.False),
                        Mine = e.TryGetProperty("mine", out var mi) && mi.ValueKind == JsonValueKind.True,
                        Denied = Str(e, "denied") is { Length: > 0 } dn ? dn : null,
                        Host = Str(e, "host") is { Length: > 0 } h ? h : null, Port = Num(e, "port") > 0 ? ((int)Num(e, "port")).ToString() : null
                    };
                    it.HaveRoute = RouteByName(it.Route) != null;
                    list.Add(it);
                }
            }
            catch { }
            _roomGrid.EmptyText = list.Count > 0 ? null : Supa.IsSuperadmin ? Tr("Todavía no hay salas. Crea la primera con «Nueva sala…».") : Tr("Todavía no hay servidores SelectOR.");
            _roomGrid.SetItems(list);
            UpdateRoomButtons();
        }

        // ---------------- entrar ----------------
        async void JoinRoom(RoomCardGrid.Item it)
        {
            if (it == null) return;
            if (!Supa.IsLoggedIn) { Warn(Tr("Inicia sesión en Empresas para entrar en los servidores SelectOR.")); return; }
            if (it.Denied != null) { Warn(it.Denied + "."); return; }
            var route = RouteByName(it.Route);
            if (route == null) { Warn(string.Format(Tr("No tienes la ruta «{0}» en esta carpeta de contenido: hace falta para entrar en la sala."), it.Route)); return; }
            if (!MPUserValid(_txtMPUser.Text.Trim()))
            {
                Warn(Tr("El usuario de Multijugador debe tener de 4 a 10 caracteres, sin espacios ni ' \" -, y no puede empezar por un número."));
                return;
            }
            // contraseña (si la tiene) y el permiso: lo comprueba el servidor y da la IP y el puerto
            JsonElement? ok = null;
            async Task<string> Try(string pw)
            {
                var (json, err) = await Supa.RpcAsync("room_join", new { p_room = it.Id, p_password = pw });
                if (err != null) return NoRoomsOnServer(err) ? Tr("El servidor aún no tiene los servidores SelectOR (falta salas-multijugador.sql).") : err;
                using var d = JsonDocument.Parse(json);
                var r = d.RootElement;
                if (r.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True) { ok = r.Clone(); return null; }
                return Str(r, "reason") is { Length: > 0 } why ? why : Tr("No se puede entrar en la sala.");
            }
            if (it.HasPassword && !Supa.IsSuperadmin)
            {
                using var dlg = new FormDialog(it.Name, Tr("Entrar"), 460, "🔒", Tr("Esta sala tiene contraseña: pídesela al administrador."));
                var pw = dlg.AddText("pw", Tr("Contraseña de la sala"), "", "", password: true);
                dlg.Shown += (s, e) => pw.Box.Focus();
                dlg.Validate = async f => await Try(f.Get("pw"));
                if (dlg.ShowDialog(this) != DialogResult.OK || ok == null) return;
            }
            else
            {
                string e1 = await Try(null);
                if (e1 != null) { Warn(e1); LoadRooms(); return; }
            }
            var j = ok.Value;
            // la ruta de la sala, modo cliente, la IP y el puerto
            if (_curRoute?.Path != route.Path)
            {
                if (!_lstRoutes.Items.Cast<object>().Any(o => o is ORTS.Menu.Route rr && rr.Path == route.Path))
                {
                    _routeSearch.Box.Text = ""; if (_chkFavOnly.Checked) _chkFavOnly.Checked = false;
                    RefreshRouteList();
                }
                SelectRouteInList(route);
                var t0 = DateTime.UtcNow;
                while ((_curRoute?.Path != route.Path || _routeLoadedToken != _routeToken) && (DateTime.UtcNow - t0).TotalSeconds < 30) await Task.Delay(100);
            }
            _rbClient.Checked = true;
            _txtMPHost.Text = Str(j, "host");
            _txtMPPort.Text = ((int)Num(j, "port")).ToString();
            UpdateStatus();
            _roomPending = it.Id;
            PlayMultiplayer();
        }

        // Open Rails ya está en marcha en la sala: avisos cada 45 s (para contar los jugadores).
        void RoomLaunched()
        {
            if (_roomPending == null) return;
            _roomJoined = _roomPending; _roomPending = null;
            if (_roomBeat == null)
            {
                _roomBeat = new Timer { Interval = 45000 };
                _roomBeat.Tick += async (s, e) => { if (_roomJoined != null) { try { await Supa.RpcAsync("room_heartbeat", new { p_room = _roomJoined }); } catch { } } };
            }
            _roomBeat.Start();
        }

        // Al cerrar Open Rails: fuera de la sala.
        async void RoomLeave()
        {
            var id = _roomJoined;
            _roomJoined = null; _roomPending = null; _roomBeat?.Stop();
            if (id != null) { try { await Supa.RpcAsync("room_leave", new { p_room = id }); } catch { } }
        }

        // ---------------- administrador ----------------
        async void EditRoomUi(string id)
        {
            if (!Supa.IsSuperadmin) return;
            var d = new RoomDialog.Data { Route = _curRoute?.Name ?? "" };
            bool hasPw = false;
            if (id != null)
            {
                var (gj, ge) = await Supa.RpcAsync("room_get", new { p_room = id });
                if (ge != null) { Msg(_roomMsg, Tr("Error: ") + ge, true); return; }
                try
                {
                    using var doc = JsonDocument.Parse(gj);
                    var c = doc.RootElement;
                    d.Name = Str(c, "name"); d.Description = Str(c, "description"); d.Route = Str(c, "route"); d.Host = Str(c, "host");
                    d.Port = (int)Num(c, "port"); d.MaxPlayers = (int)Num(c, "max_players"); d.MinPoints = (int)Num(c, "min_points");
                    d.Access = Str(c, "access"); d.Active = !(c.TryGetProperty("active", out var av) && av.ValueKind == JsonValueKind.False);
                    hasPw = c.TryGetProperty("has_password", out var hp) && hp.ValueKind == JsonValueKind.True;
                    if (c.TryGetProperty("companies", out var cs) && cs.ValueKind == JsonValueKind.Array) foreach (var x in cs.EnumerateArray()) d.CompanyIds.Add(Str(x, "id"));
                    if (c.TryGetProperty("users", out var us) && us.ValueKind == JsonValueKind.Array) foreach (var u in us.EnumerateArray()) if (u.ValueKind == JsonValueKind.String) d.Users.Add(u.GetString());
                }
                catch { }
            }
            var companies = new List<(string id, string name)>();
            try
            {
                var (cj, ce) = await Supa.RpcAsync("room_companies", new { });
                if (ce == null) { using var doc = JsonDocument.Parse(cj); foreach (var e in doc.RootElement.EnumerateArray()) companies.Add((Str(e, "id"), Str(e, "name"))); }
            }
            catch { }
            var routes = _routesAll.Select(r => r.Name).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();
            if (!string.IsNullOrEmpty(d.Route) && !routes.Contains(d.Route, StringComparer.OrdinalIgnoreCase)) routes.Insert(0, d.Route);

            using var dlg = new RoomDialog(id != null, d, routes, companies, hasPw);
            dlg.Submit = async r =>
            {
                var (json, err) = await Supa.RpcAsync("room_save", new
                {
                    p_id = id, p_name = r.Name, p_description = r.Description, p_route = r.Route, p_host = r.Host, p_port = r.Port,
                    p_pass_mode = r.PassMode, p_password = r.Password, p_max_players = r.MaxPlayers, p_access = r.Access,
                    p_companies = r.CompanyIds.ToArray(), p_users = r.Users.ToArray(), p_min_points = r.MinPoints, p_active = r.Active, p_sort = 0
                });
                if (err != null) return NoRoomsOnServer(err) ? Tr("El servidor aún no tiene los servidores SelectOR (falta salas-multijugador.sql).") : err;
                return null;
            };
            if (dlg.ShowDialog(this) == DialogResult.OK) { Msg(_roomMsg, Tr("Sala guardada."), false); LoadRooms(); }
        }

        async void DeleteRoom()
        {
            var it = _roomGrid?.SelectedItem;
            if (it == null || !Supa.IsSuperadmin) return;
            if (ThemedBox.Show(this, string.Format(Tr("¿Quitar la sala «{0}»? Los que estén dentro siguen jugando, pero dejará de salir en la lista."), it.Name),
                               "SelectOR", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            var (_, err) = await Supa.RpcAsync("room_delete", new { p_room = it.Id });
            if (err != null) { Msg(_roomMsg, Tr("Error: ") + err, true); return; }
            Msg(_roomMsg, Tr("Sala quitada."), false);
            LoadRooms();
        }
    }
}
