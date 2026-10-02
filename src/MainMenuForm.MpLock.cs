// Multijugador · contraseña en los servidores PÚBLICOS (servidores-contrasena.sql).
// El primero de SelectOR que entra en un servidor público recibe una contraseña; mientras su Open Rails
// siga abierto (lo avisa cada 45 s), los demás de SelectOR tienen que escribirla. Al cerrar Open Rails
// se suelta. Si el servidor no tiene el SQL, se entra como siempre.

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
        ServerCardGrid _srvGrid;
        MpModeCards _mpMode;
        FlowLayoutPanel _srvTabs;
        int _srvFilter;                     // 0 todos · 1 con jugadores · 2 rutas que tengo
        DateTime _srvLoadedUtc = DateTime.MinValue;
        Label _srvAge;
        readonly Dictionary<string, (string owner, bool prot, DateTime since)> _mpLocks = new(StringComparer.OrdinalIgnoreCase);
        string _mpOwnedServer;              // servidor que protejo yo (mientras mi Open Rails esté abierto)
        Timer _mpBeat, _mpStatusTimer;

        static string MpKey(string ip, string port) => ((ip ?? "").Trim() + ":" + (port ?? "").Trim()).ToLowerInvariant();

        static bool NoMpOnServer(string err) => err != null && (err.IndexOf("PGRST202", StringComparison.OrdinalIgnoreCase) >= 0
                                                                || err.IndexOf("Could not find the function", StringComparison.OrdinalIgnoreCase) >= 0);

        // ¿Es uno de los servidores públicos de la lista?
        GameServer PublicServerFor(string host, string port) =>
            _serversAll?.FirstOrDefault(s => string.Equals(s.Ip?.Trim(), host?.Trim(), StringComparison.OrdinalIgnoreCase) && (s.Port ?? "").Trim() == (port ?? "").Trim());

        // Antes de lanzar Open Rails contra un servidor público: true = adelante.
        async Task<bool> MpGate(GameServer sv)
        {
            if (!Supa.IsLoggedIn)
            {
                Warn(Tr("Para unirte a un servidor público inicia sesión en Empresas: SelectOR se encarga de la contraseña de esos servidores."));
                return false;
            }
            string key = MpKey(sv.Ip, sv.Port);
            SetBusy(Tr("Comprobando el servidor…"));
            var (json, err) = await Supa.RpcAsync("mp_join", new { p_server = key, p_route = sv.Route ?? "" });
            SetIdle();
            if (NoMpOnServer(err)) return true;             // servidor sin el SQL: como siempre
            if (err != null) { Warn(Tr("Error: ") + err); return false; }
            string status = "", owner = "", password = null; DateTime since = DateTime.UtcNow;
            try
            {
                using var d = JsonDocument.Parse(json);
                var r = d.RootElement;
                status = Str(r, "status"); owner = Str(r, "owner");
                if (r.TryGetProperty("password", out var p) && p.ValueKind == JsonValueKind.String) password = p.GetString();
                DateTime.TryParse(Str(r, "since"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal, out since);
            }
            catch { return true; }

            if (status == "owner")
            {
                using var dlg = new MpOwnerDialog(key, string.IsNullOrWhiteSpace(sv.Route) ? key : sv.Route, password, async mode =>
                {
                    var (j2, e2) = await Supa.RpcAsync("mp_set_password", new { p_server = key, p_mode = mode });
                    if (e2 != null) return (null, Tr("Error: ") + e2);
                    try { using var d2 = JsonDocument.Parse(j2); return (Str(d2.RootElement, "password"), null); } catch { return (null, null); }
                });
                if (dlg.ShowDialog(this) != DialogResult.OK) { await Supa.RpcAsync("mp_release", new { p_server = key }); return false; }
                MpStartOwning(key);
                return true;
            }
            if (status == "open") return true;               // lo protege otro, pero sin contraseña
            // protegido: pedir la contraseña
            using (var dlg = new MpPasswordDialog(owner, string.IsNullOrWhiteSpace(sv.Route) ? key : sv.Route, since, async pw =>
            {
                var (j3, e3) = await Supa.RpcAsync("mp_check", new { p_server = key, p_password = pw });
                if (e3 != null) return (false, Tr("Error: ") + e3);
                try
                {
                    using var d3 = JsonDocument.Parse(j3);
                    var r3 = d3.RootElement;
                    if (r3.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True) return (true, null);
                    double wait = Num(r3, "wait_s");
                    return (false, wait > 0 ? string.Format(Tr("Demasiados intentos: espera {0} s."), wait.ToString("0")) : Tr("Contraseña incorrecta."));
                }
                catch { return (false, Tr("Contraseña incorrecta.")); }
            }))
                return dlg.ShowDialog(this) == DialogResult.OK;
        }

        // Mientras mi Open Rails siga abierto, aviso de que sigo conectado.
        void MpStartOwning(string key)
        {
            _mpOwnedServer = key;
            if (_mpBeat == null) { _mpBeat = new Timer { Interval = 45000 }; _mpBeat.Tick += async (s, e) => { if (_mpOwnedServer != null) await Supa.RpcAsync("mp_heartbeat", new { p_server = _mpOwnedServer }); }; }
            _mpBeat.Start();
        }

        // Al cerrar Open Rails: el servidor queda libre.
        async void MpRelease()
        {
            var key = _mpOwnedServer;
            _mpOwnedServer = null; _mpBeat?.Stop();
            if (key != null) { try { await Supa.RpcAsync("mp_release", new { p_server = key }); } catch { } }
        }

        // Quién protege cada servidor de la lista (para las tarjetas).
        async Task LoadMpLocks()
        {
            if (!Supa.IsLoggedIn || _serversAll == null || _serversAll.Count == 0) { _mpLocks.Clear(); return; }
            var keys = _serversAll.Select(s => MpKey(s.Ip, s.Port)).Distinct().ToArray();
            var (json, err) = await Supa.RpcAsync("mp_status", new { p_servers = keys });
            if (err != null) return;
            _mpLocks.Clear();
            try
            {
                using var d = JsonDocument.Parse(json);
                foreach (var e in d.RootElement.EnumerateArray())
                {
                    DateTime.TryParse(Str(e, "since"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal, out var since);
                    _mpLocks[Str(e, "server")] = (Str(e, "owner_name"), e.TryGetProperty("protected", out var p) && p.ValueKind == JsonValueKind.True, since);
                }
            }
            catch { }
        }

        // Tarjetas de los servidores con el filtro elegido.
        void FillServerCards()
        {
            if (_srvGrid == null) return;
            var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in _routesAll) { if (!string.IsNullOrEmpty(r.Name)) have.Add(r.Name.Trim()); try { have.Add(System.IO.Path.GetFileName(r.Path.TrimEnd('\\', '/'))); } catch { } }
            var items = new List<ServerCardGrid.Item>();
            int nAll = 0, nPlayers = 0, nHave = 0;
            foreach (var s in (_serversAll ?? new List<GameServer>()).OrderByDescending(v => { int.TryParse(v.Players, out int n); return n; }))
            {
                int.TryParse(s.Players, out int pl);
                string route = string.IsNullOrWhiteSpace(s.Route) || s.Route == "-" ? "" : s.Route.Trim();
                bool h = route.Length > 0 && have.Contains(route);
                nAll++; if (pl > 0) nPlayers++; if (h) nHave++;
                if (_srvFilter == 1 && pl <= 0 || _srvFilter == 2 && !h) continue;
                var it = new ServerCardGrid.Item { S = s, Route = route, Names = s.PlayerNames == "-" ? "" : s.PlayerNames, Players = pl, HaveRoute = h };
                if (_mpLocks.TryGetValue(it.Key, out var lk)) { it.LockOwner = lk.owner; it.Protected = lk.prot; it.LockSince = lk.since; }
                items.Add(it);
            }
            _srvGrid.EmptyText = (_serversAll?.Count ?? 0) == 0 ? Tr("No hay servidores públicos ahora (o no se ha podido leer la lista).")
                               : items.Count == 0 ? Tr("Nada coincide con el filtro.") : null;
            _srvGrid.SetItems(items);
            if (_srvTabs != null)
            {
                string[] names = { "Todos", "Con jugadores", "Rutas que tengo" }; int[] counts = { nAll, nPlayers, nHave };
                using var f = Theme.Font(9.75f, FontStyle.Bold);
                for (int k = 0; k < names.Length && k < _srvTabs.Controls.Count; k++)
                    if (_srvTabs.Controls[k] is RoundButton b)
                    {
                        string t = Tr(names[k]) + "  " + counts[k];
                        if (b.Text == t) continue;   // sin cambios: ni se toca (nada parpadea)
                        b.Text = t; b.Width = TextRenderer.MeasureText(t, f).Width + 34; b.Invalidate();
                    }
            }
            UpdateServerAge();
        }

        void UpdateServerAge()
        {
            if (_srvAge == null) return;
            if (_srvLoadedUtc == DateTime.MinValue) { if (_srvAge.Text != "") _srvAge.Text = ""; return; }
            int s = (int)(DateTime.UtcNow - _srvLoadedUtc).TotalSeconds;
            string t = s < 60 ? string.Format(Tr("actualizado hace {0} s"), s) : string.Format(Tr("actualizado hace {0} min"), s / 60);
            if (_srvAge.Text != t) _srvAge.Text = t;
        }
    }
}
