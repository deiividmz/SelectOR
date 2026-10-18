// SALA COMPARTIDA DE CGL (desde la 1.2.65; sustituye a «Servidores SelectOR»): una sola sala, en el servidor de
// SelectOR, con la ruta CGL_V22N_RE y un despachador fijo (SelectOR Despachador: Open Rails sin 3D que no es ningún
// jugador). En Conducción libre con esa ruta, CONDUCIR entra solo en la sala si está marcada la casilla «Conducir en la
// sala compartida» (junto al botón; activada de serie, _prefs.SalaCgl):
//  · antes de lanzar Open Rails se le pregunta a la sala («SELECTOR PEEK» por una conexión corta): si se llega, si la
//    ruta es la misma versión (la huella del .tdb, la que comprueba Open Rails: con otra no deja entrar), si ya hay
//    alguien con tu nombre y si en la vía de salida del recorrido hay un tren de otro jugador (se avisa y se elige:
//    salir igualmente, conducir solo o elegir otro recorrido);
//  · la hora es la de España en ese momento y el tiempo, despejado (la sala es la misma para todos);
//  · se entra como cliente (-multiplayerclient); el chat de la sala, el mapa grande y los papeles van por la conexión
//    de siempre (SwitchPilot, MainMenuForm.Agujas.cs). Al cerrar Open Rails se devuelven el servidor y el puerto que
//    había en los ajustes de Open Rails.
// Si no se puede entrar (sala caída, otra versión de la ruta…), se ofrece conducir solo esa vez.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using ORTS.Settings;
using OrPath = ORTS.Menu.Path;

namespace SelectOR
{
    public partial class MainMenuForm
    {
        public static string SalaHost = "213.188.208.165";   // (las pruebas la cambian por un servidor local)
        public static int SalaPort = 30000;
        // Para las pruebas: si está puesto, recibe los argumentos en vez de escribir los ajustes de Open Rails y lanzarlo.
        public static Action<string> SalaLaunchHook;
        public static string SalaRoute = "CGL_V22N_RE";
        // La ruta, de su web oficial (spaintrainzrutas.com): el usuario la baja y la instala él (ROUTES, GLOBAL y SOUND).
        public const string SalaDownloadUrl = "https://www.spaintrainzrutas.com/descargas/descarga.php?dlid=1214";
        string _salaOrVersion;               // la versión de Open Rails del despachador de la sala (la dice el servidor)
        DateTime _salaVersionAt = DateTime.MinValue; bool _salaVersionAsking;
        Label _salaSub, _salaPromoVer; Panel _salaPromo;

        string _roomPending, _roomJoined;    // «cgl» al ir a entrar · mientras se juega en la sala
        string _roomHost; int _roomPort;     // a dónde conecta la sala (no pasa por «Servidor» ni «Puerto» de Multijugador)
        string _roomName;                    // para el chat y el registro del mapa
        Panel _salaHost; CheckBox _chkSala; bool _salaSynced;

        bool SalaRouteHere => _curRoute != null
            && string.Equals(Path.GetFileName((_curRoute.Path ?? "").TrimEnd('\\', '/')), SalaRoute, StringComparison.OrdinalIgnoreCase);

        bool SalaWanted => _activePage == 2 && SalaRouteHere && _chkSala != null && _chkSala.Checked;

        // La casilla, a la izquierda de CONDUCIR (BuildBottomBar): solo en Conducción libre con la ruta de la sala.
        Panel BuildSalaCheck()
        {
            _salaHost = new Panel { Dock = DockStyle.Right, Width = 290, BackColor = Theme.Surface, Visible = false };
            _chkSala = new ThemeCheck { Text = Tr("Conducir en la sala compartida"), ForeColor = Theme.Text, BackColor = Theme.Surface, Size = new Size(280, 28), Checked = true };
            var sub = new Label { Text = SalaSubText(), AutoSize = false, Size = new Size(260, 34),
                                  ForeColor = Theme.Subtle, BackColor = Theme.Surface, Font = Theme.Font(8.25f) };
            _salaSub = sub;
            _chkSala.CheckedChanged += (s, e) =>
            {
                if (_prefs == null || !_salaSynced || _prefs.SalaCgl == _chkSala.Checked) return;
                _prefs.SalaCgl = _chkSala.Checked;
                try { _prefs.Save(); } catch { }
            };
            Action place = () =>
            {
                int h = _chkSala.Height + sub.Height;
                int y = Math.Max(0, (_salaHost.ClientSize.Height - h) / 2);
                _chkSala.Location = new Point(6, y);
                sub.Location = new Point(6 + 26, y + _chkSala.Height);
            };
            _salaHost.Resize += (s, e) => place();
            _salaHost.HandleCreated += (s, e) => place();
            _salaHost.Controls.Add(_chkSala); _salaHost.Controls.Add(sub);
            return _salaHost;
        }

        // (ShowPage y OnRouteChanged)
        void SalaCheckUpdate()
        {
            if (_salaHost == null) return;
            if (!_salaSynced && _prefs != null) { _chkSala.Checked = _prefs.SalaCgl; _salaSynced = true; }
            _salaHost.Visible = _activePage == 2 && SalaRouteHere;
            if (_salaHost.Visible) SalaVersionRefresh();
        }

        string SalaSubText() => Tr("Con los demás jugadores · hora de España")
                                + (_salaOrVersion != null ? "\n" + string.Format(Tr("Sala con Open Rails {0}"), _salaOrVersion) : "");

        // La tarjeta de la ruta de la sala: el distintivo.
        (string text, Color color)? SalaRouteBadge(ORTS.Menu.Route r)
            => r != null && string.Equals(Path.GetFileName((r.Path ?? "").TrimEnd('\\', '/')), SalaRoute, StringComparison.OrdinalIgnoreCase)
               ? ("🌐 " + Tr("Multijugador SelectOR"), Theme.AccentHi) : null;

        bool SalaRouteInstalled => _routesAll != null && _routesAll.Any(r => SalaRouteBadge(r) != null);

        // En la lista de rutas, si no se tiene la de la sala: qué es y el botón para bajarla de su web.
        Panel BuildSalaPromo()
        {
            _salaPromo = new Panel { Dock = DockStyle.Top, Height = 168, BackColor = Theme.BgSidebar, Padding = new Padding(0, 0, 0, 8), Visible = false };
            var card = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Padding = new Padding(12, 10, 12, 10) };
            var title = new Label { Text = "🌐  " + Tr("Multijugador SelectOR"), Dock = DockStyle.Top, Height = 22, ForeColor = Theme.AccentHi, BackColor = Theme.Surface, Font = Theme.Font(9.5f, FontStyle.Bold) };
            var text = new Label { Text = Tr("La sala compartida es en la ruta CGL_V22N_RE. Descárgala de su web oficial y descomprímela en tu contenido (ROUTES, GLOBAL y SOUND); luego vuelve a abrir SelectOR."),
                                   Dock = DockStyle.Top, Height = 64, ForeColor = Theme.Text, BackColor = Theme.Surface, Font = Theme.Font(8.5f) };
            _salaPromoVer = new Label { Text = "", Dock = DockStyle.Top, Height = 18, ForeColor = Theme.Subtle, BackColor = Theme.Surface, Font = Theme.Font(8f) };
            var btn = DialogKit.Fit(DialogKit.Primary("⬇  " + Tr("Descargar la ruta")), 190);
            btn.Dock = DockStyle.Bottom;
            btn.Click += (s, e) => OpenLink(SalaDownloadUrl);
            // ✕: quien no quiera la ruta, lo cierra y no vuelve a salir
            var close = new Label { Text = "✕", AutoSize = false, Size = new Size(24, 22), Dock = DockStyle.Right, TextAlign = ContentAlignment.MiddleCenter,
                                    ForeColor = Theme.Subtle, BackColor = Theme.Surface, Font = Theme.Font(10f, FontStyle.Bold), Cursor = Cursors.Hand };
            close.MouseEnter += (s, e) => close.ForeColor = Theme.Text;
            close.MouseLeave += (s, e) => close.ForeColor = Theme.Subtle;
            close.Click += (s, e) => { if (_prefs != null) { _prefs.SalaPromoHidden = true; try { _prefs.Save(); } catch { } } _salaPromo.Visible = false; };
            new ToolTip().SetToolTip(close, Tr("Cerrar (no volver a enseñarlo)"));
            title.Controls.Add(close);
            card.Controls.Add(btn); card.Controls.Add(_salaPromoVer); card.Controls.Add(text); card.Controls.Add(title);
            _salaPromo.Controls.Add(card);
            return _salaPromo;
        }

        // (RefreshRouteList) A la vista solo sin la ruta de la sala en el contenido elegido.
        void SalaPromoUpdate()
        {
            if (_salaPromo == null) return;
            bool show = !SalaRouteInstalled && _prefs?.SalaPromoHidden != true;
            if (_salaPromo.Visible != show) _salaPromo.Visible = show;
            if (show) SalaVersionRefresh();
        }

        // La versión de Open Rails de la sala, preguntándole (como mucho cada 5 min).
        async void SalaVersionRefresh()
        {
            if (_salaVersionAsking || DateTime.UtcNow - _salaVersionAt < TimeSpan.FromMinutes(5)) return;
            _salaVersionAsking = true;
            try
            {
                string me = (_txtMPUser?.Text ?? "").Trim();
                var info = await SalaPeek(MPUserValid(me) ? me : "SelectOR");
                _salaVersionAt = DateTime.UtcNow;
                if (info?.OrVersion != null) _salaOrVersion = info.OrVersion;
            }
            catch { }
            finally { _salaVersionAsking = false; }
            try
            {
                if (_salaSub != null) _salaSub.Text = SalaSubText();
                if (_salaPromoVer != null) _salaPromoVer.Text = _salaOrVersion != null ? string.Format(Tr("Sala con Open Rails {0}"), _salaOrVersion) : "";
            }
            catch { }
        }

        // ---------------- entrar ----------------
        async void PlaySala()
        {
            var c = _lstConsists.SelectedItem as TrainItem;
            var p = CurrentPath();
            if (c == null) { Warn(Tr("Selecciona un tren.")); return; }
            if (p == null) { Warn(Tr("Esta ruta no tiene recorridos disponibles.")); return; }
            string title = Tr("Sala compartida de CGL");
            string me = await MpPlayerName(title);   // el nombre de SelectOR (MainMenuForm.MpConectar.cs)
            if (me == null) return;

            SetBusy(Tr("Comprobando la sala compartida…"));
            SalaInfo info; string mine; string occ = null;
            try
            {
                info = await SalaPeek(me);
                string dir = _curRoute.Path;
                mine = await Task.Run(() => TdbFingerprint(dir));
            }
            finally { SetIdle(); UpdateStatus(); }

            if (info == null)
            {
                if (SalaSolo(Tr("No se puede conectar con la sala compartida de CGL ahora mismo."))) LaunchSolo();
                return;
            }
            if (info.Tdb != null && mine != null && info.Tdb != mine)
            {
                if (SalaSolo(Tr("Tu ruta CGL_V22N_RE no es la misma versión que la de la sala, y Open Rails no te dejaría entrar. Actualiza la ruta para conducir con los demás."))) LaunchSolo();
                return;
            }
            // dos con el mismo nombre: Open Rails echa al segundo. Los nombres de SelectOR son únicos: es una conexión
            // tuya de antes que la sala aún no ha soltado
            if (info.Trains.Any(t => t.player && string.Equals(t.name, me, StringComparison.Ordinal)))
            {
                ThemedBox.Show(this, string.Format(Tr("En la sala ya hay un jugador «{0}»: será tu conexión anterior, que la sala aún no ha soltado. Espera un momento y vuelve a intentarlo."), me), title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try { occ = await SalaOccupiedBy(p, info, me); } catch { occ = null; }
            if (occ != null)
            {
                var r = ThemedBox.Show(this,
                    string.Format(Tr("En la vía de salida de «{0}» hay un tren de {1}. Si sales igualmente, los dos trenes empezarán uno encima del otro."), p.Name, occ),
                    title, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning,
                    new[] { Tr("Salir igualmente"), Tr("Conducir solo"), Tr("Elegir otro recorrido") }, 2);
                if (r == DialogResult.Cancel) return;
                if (r == DialogResult.No) { LaunchSolo(); return; }
            }

            var (time, season) = SpainNow();
            string args = $"-multiplayerclient -explorer \"{p.FilePath}\" \"{c.FilePath}\" {time} {season} 0";
            if (SalaLaunchHook != null) { SalaLaunchHook(args); return; }
            // a la sala: su servidor en los ajustes de Open Rails (se devuelve lo de antes al salir), modo cliente
            _roomHost = SalaHost; _roomPort = SalaPort; _roomName = title; _roomPending = "cgl";
            try
            {
                try { _settings = new UserSettings(new string[0]); } catch { }
                _settings.Multiplayer_User = me;
                RoomWriteSettings();
                _settings.Save();
            }
            catch { }
            Launch(args);
        }

        // Conducir solo esta vez (como sin la casilla).
        void LaunchSolo()
        {
            var args = BuildArguments(out string err);
            if (args == null) { Warn(err); return; }
            Launch(args);
        }

        bool SalaSolo(string why)
            => ThemedBox.Show(this, why + "\n\n" + Tr("¿Conducir solo esta vez?"), Tr("Sala compartida de CGL"), MessageBoxButtons.YesNo,
                              MessageBoxIcon.Information, new[] { Tr("Conducir solo"), Tr("Cancelar") }) == DialogResult.Yes;

        // La hora de España ahora mismo («HH:mm:ss», como la entiende Open Rails) y la estación (0 primavera · 1 verano ·
        // 2 otoño · 3 invierno), como el despachador de la sala.
        internal static (string time, int season) SpainNow()
        {
            DateTime t;
            try { t = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Romance Standard Time")); }
            catch
            {
                try { t = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Europe/Madrid")); }
                catch { t = DateTime.Now; }
            }
            int m = t.Month;
            int season = m >= 3 && m <= 5 ? 0 : m >= 6 && m <= 8 ? 1 : m >= 9 && m <= 11 ? 2 : 3;
            return (t.ToString("HH:mm:ss", CultureInfo.InvariantCulture), season);
        }

        // ---------------- lo que dice la sala antes de entrar ----------------
        public sealed class SalaInfo
        {
            public string Route, Tdb, Disp, OrVersion; public int Players;
            public List<(string name, int tx, int tz, double x, double z, bool player)> Trains = new();
            public Dictionary<string, (int node, int mu, int dir, double len)> Track = new(StringComparer.Ordinal);
        }

        // «SELECTOR HELLO nombre 0» (conexión corta: no entra en la sala) y «SELECTOR PEEK»: el servidor contesta
        // «SELECTOR ROOM ruta huella despachador jugadores» y los trenes (SELECTOR TRAINS). null = no se llega.
        internal static async Task<SalaInfo> SalaPeek(string me, string host = null, int port = 0)
        {
            host ??= SalaHost; if (port <= 0) port = SalaPort;
            try
            {
                using var tcp = new TcpClient();
                var conn = tcp.ConnectAsync(host, port);
                if (await Task.WhenAny(conn, Task.Delay(5000)) != conn || conn.IsFaulted) return null;
                var st = tcp.GetStream();
                async Task Send(string body) { var b = Encoding.Unicode.GetBytes(" " + body.Length + ": " + body); await st.WriteAsync(b, 0, b.Length); }
                await Send($"SELECTOR HELLO {me} 0");
                await Send("SELECTOR PEEK");
                var sb = new StringBuilder(); var dec = Encoding.Unicode.GetDecoder();
                var buf = new byte[65536]; var chars = new char[65536];
                SalaInfo info = null;
                var until = DateTime.UtcNow.AddSeconds(5);
                while (DateTime.UtcNow < until)
                {
                    var rd = st.ReadAsync(buf, 0, buf.Length);
                    var left = until - DateTime.UtcNow;
                    if (left <= TimeSpan.Zero || await Task.WhenAny(rd, Task.Delay(left)) != rd) break;
                    int n = rd.Result;
                    if (n <= 0) break;
                    int cn = dec.GetChars(buf, 0, n, chars, 0);
                    sb.Append(chars, 0, cn);
                    string f;
                    while ((f = SwitchPilot.NextFrame(sb)) != null)
                    {
                        if (f.StartsWith("SELECTOR ROOM ", StringComparison.Ordinal))
                        {
                            var t = f.Substring(14).Split(' ');
                            info = new SalaInfo
                            {
                                Route = t[0], Tdb = t.Length > 1 && t[1] != "-" ? t[1] : null, Disp = t.Length > 2 ? t[2] : "-",
                                Players = t.Length > 3 && int.TryParse(t[3], out int np) ? np : 0,
                                OrVersion = t.Length > 4 && t[4] != "-" ? t[4].Replace('_', ' ') : null   // (servidor de antes: sin ella)
                            };
                        }
                        else if (f.StartsWith("SELECTOR TRAINS ", StringComparison.Ordinal) && info != null)
                        {
                            ParseSalaTrains(info, f.Substring(16));
                            return info;
                        }
                    }
                }
                return info;   // (sin los trenes: no se sabe si la vía está ocupada)
            }
            catch { return null; }
        }

        // «nombre tx tz x z kmh p nodo mu sentido largo;…» (como SwitchPilot.OnTrains)
        static void ParseSalaTrains(SalaInfo info, string s)
        {
            var ci = CultureInfo.InvariantCulture;
            foreach (var part in s.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var f = part.Trim().Split(' ');
                if (f.Length < 7 || !int.TryParse(f[1], out int tx) || !int.TryParse(f[2], out int tz)
                    || !double.TryParse(f[3], NumberStyles.Float, ci, out double x) || !double.TryParse(f[4], NumberStyles.Float, ci, out double z)) continue;
                info.Trains.Add((f[0], tx, tz, x, z, f[6] == "1"));
                if (f.Length >= 11 && int.TryParse(f[7], out int node) && node >= 0 && int.TryParse(f[8], out int mu) && int.TryParse(f[9], out int dir)
                    && double.TryParse(f[10], NumberStyles.Float, ci, out double len)) info.Track[f[0]] = (node, mu, dir, len);
            }
        }

        // La huella del .tdb como la manda Open Rails en su «PLAYER» (MPManager.GetMD5HashFromTDBFile: los 16 bytes del
        // MD5 leídos como texto UTF-16), en hexadecimal, igual que la cuenta el servidor. El .tdb, el del .trk (FileName).
        internal static string TdbFingerprint(string dir)
        {
            try
            {
                string name = null;
                string trk = Directory.GetFiles(dir, "*.trk").OrderBy(x => x, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
                if (trk != null)
                {
                    var m = Regex.Match(File.ReadAllText(trk), @"\bFileName\s*\(\s*""?([^""\)\s]+)""?\s*\)", RegexOptions.IgnoreCase);
                    if (m.Success) name = m.Groups[1].Value;
                }
                string tdb = name != null ? Path.Combine(dir, name + ".tdb") : null;
                if (tdb == null || !File.Exists(tdb)) tdb = Directory.GetFiles(dir, "*.tdb").OrderBy(x => x, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
                if (tdb == null) return null;
                byte[] h;
                using (var md5 = MD5.Create()) using (var fs = File.OpenRead(tdb)) h = md5.ComputeHash(fs);
                return Convert.ToHexString(Encoding.Unicode.GetBytes(Encoding.Unicode.GetString(h))).ToLowerInvariant();
            }
            catch { return null; }
        }

        // ¿Hay un tren de otro jugador en la vía de salida del recorrido? El nombre (o null). Su tren va de la cola (lo
        // que manda Open Rails) a la cabeza por la vía (RouteGraph.PathAhead con su largo); cuenta si pasa a menos de
        // 40 m del punto de salida del .pat.
        async Task<string> SalaOccupiedBy(OrPath p, SalaInfo info, string me)
        {
            var others = info.Trains.Where(t => t.player && !string.Equals(t.name, me, StringComparison.Ordinal)).ToList();
            if (others.Count == 0) return null;
            var pts = PatWorldPoints(p.FilePath);
            if (pts.Count == 0 || !OrGeo.TryLatLon(pts[0].x, pts[0].z, out double sla, out double slo)) return null;
            RouteGraph g = null;
            try { g = await RouteGraphFor(_curRoute.Path); } catch { }
            foreach (var t in others)
            {
                if (!OrGeo.TryLatLon(t.tx * 2048.0 + t.x, t.tz * 2048.0 + t.z, out double la, out double lo)) continue;
                var line = new List<(double lat, double lon)> { (la, lo) };
                if (g != null && info.Track.TryGetValue(t.name, out var k))
                {
                    try { var body = g.PathAhead(k.node, la, lo, k.dir, k.mu, k.len, 0, n => -1, out _); if (body != null && body.Count > 0) line = body; } catch { }
                }
                if (DistToLineM(sla, slo, line) < 40) return t.name;
            }
            return null;
        }

        static double DistToLineM(double lat, double lon, List<(double lat, double lon)> line)
        {
            double kx = 111320 * Math.Cos(lat * Math.PI / 180), ky = 110540;
            (double x, double y) P((double lat, double lon) q) => ((q.lon - lon) * kx, (q.lat - lat) * ky);
            double best = double.MaxValue;
            for (int i = 0; i < line.Count; i++)
            {
                var a = P(line[i]);
                if (i == 0 || line.Count == 1) best = Math.Min(best, Math.Sqrt(a.x * a.x + a.y * a.y));
                if (i == 0) continue;
                var b = P(line[i - 1]);
                double dx = a.x - b.x, dy = a.y - b.y, l2 = dx * dx + dy * dy;
                double u = l2 > 0 ? Math.Max(0, Math.Min(1, -(b.x * dx + b.y * dy) / l2)) : 0;
                double cx = b.x + u * dx, cy = b.y + u * dy;
                best = Math.Min(best, Math.Sqrt(cx * cx + cy * cy));
            }
            return best;
        }

        // ---------------- mientras se juega ----------------
        // Open Rails ya está en marcha en la sala: la conexión de SelectOR (chat, mapa, papeles).
        void RoomLaunched()
        {
            if (_roomPending == null) return;
            _roomJoined = _roomPending; _roomPending = null;
            PilotCreate();
        }

        // No se llega a lanzar Open Rails: los ajustes como estaban.
        void RoomAbort()
        {
            _roomPending = null;
            RoomRestoreSettings();
        }

        // Al cerrar Open Rails: fuera de la sala.
        void RoomLeave()
        {
            _roomJoined = null; _roomPending = null;
            PilotStop();
            RoomRestoreSettings();
        }

        // La sala se lanza escribiendo su IP y su puerto en los ajustes de Open Rails (RunActivity los lee de ahí):
        // al salir se devuelve lo que había, para que no aparezcan en «Servidor» y «Puerto».
        void RoomWriteSettings()
        {
            if (string.IsNullOrEmpty(_prefs.RoomRegHost))   // (si ya hay una guardada, lo de antes sigue siendo lo bueno)
            {
                _prefs.RoomPrevHost = _settings.Multiplayer_Host ?? "";
                _prefs.RoomPrevPort = _settings.Multiplayer_Port;
            }
            _prefs.RoomRegHost = _roomHost;
            try { _prefs.Save(); } catch { }
            _settings.Multiplayer_Host = _roomHost;
            _settings.Multiplayer_Port = _roomPort;
        }

        void RoomRestoreSettings()
        {
            if (string.IsNullOrEmpty(_prefs.RoomRegHost)) return;
            try
            {
                try { _settings = new UserSettings(new string[0]); } catch { }
                if (_settings.Multiplayer_Host == _prefs.RoomRegHost)
                {
                    _settings.Multiplayer_Host = _prefs.RoomPrevHost ?? "";
                    if (_prefs.RoomPrevPort > 0) _settings.Multiplayer_Port = _prefs.RoomPrevPort;
                    _settings.Save();
                }
            }
            catch { }
            _prefs.RoomRegHost = null; _prefs.RoomPrevHost = null; _prefs.RoomPrevPort = 0;
            try { _prefs.Save(); } catch { }
        }
    }
}
