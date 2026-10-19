// Mapa grande en una sala de Servidores SelectOR, como el mapa de Open Rails (Ctrl+9): agujas, señales y trenes
// (RoomMap.cs). Lo de la ruta se prepara una vez por ruta en segundo plano; lo que cambia llega por la conexión aparte
// con la sala (SwitchPilot, servidor con «READY 4»). Un clic en una aguja la pide al despachador en tu nombre; en una
// señal, su menú (solo si el despachador te ha nombrado ayudante: Open Rails no acepta cambios de señal de nadie más).
//  · Comprobación: tras pedir «Parada» en una señal, la siguiente foto del despachador tiene que traerla en parada; si
//    no (la numeración no es la suya: otra versión de Open Rails), se dejan de pedir señales en esta ruta.
//  · Los demás trenes de la sala (también los de quien no usa SelectOR) salen en el mapa y en el mini-mapa con su
//    nombre en Open Rails, en vez de los usuarios de la comunidad; los de la IA, en gris.
//  · Registro en %AppData%\Open Rails\SelectOR\mapa-sala.log: lo preparado de la ruta, si llegan y cuadran las fotos de
//    agujas y señales, cada clic y lo que contesta el despachador.

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
        RoomMapGeo _roomGeo; string _roomGeoFor; bool _roomGeoBuilding;
        readonly Dictionary<int, (int pos, DateTime at)> _roomSwSet = new();   // agujas cambiadas al momento (después de la última foto)
        (int obj, DateTime at, bool seen)? _roomSigCheck;                     // la última «Parada» pedida, por comprobar
        // Agujas pedidas desde aquí y ya cambiadas por el despachador: si una foto posterior la trae como estaba, es que
        // Open Rails la ha vuelto a poner (está en la ruta que tiene preparada para un tren) y se avisa.
        readonly Dictionary<int, (int pos, DateTime at)> _roomMine = new(), _roomAsked = new();
        readonly Dictionary<string, Color> _roomColors = new(StringComparer.Ordinal);

        bool RoomMapOn => _pilot != null && _pilot.Ready && _pilot.MapOk;
        int _roomSwVer;   // cambia con cada aguja cambiada al momento (para rehacer los caminos de los trenes)
        DateTime _roomStatesSeen = DateTime.MinValue, _roomSigsSeen = DateTime.MinValue;

        void RoomLog(string line)
        {
            try { System.IO.File.AppendAllText(System.IO.Path.Combine(AppDataTidy.CacheRoot, "mapa-sala.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}"); } catch { }
        }

        void RoomMapHook(SwitchPilot p)
        {
            _roomSwSet.Clear(); _roomMine.Clear(); _roomSigCheck = null; _roomStatesSeen = _roomSigsSeen = DateTime.MinValue;
            RoomLog($"── sala {_roomName ?? "(unirse a un servidor)"} · {_curRoute?.Name}");
            p.SwitchSet += (tx, tz, id, pos) => { try { BeginInvoke((Action)(() => RoomSwitchSet(tx, tz, id, pos))); } catch { } };
            p.MapError += e => { try { BeginInvoke((Action)(() => RoomMapError(e))); } catch { } };
        }

        // Cada segundo (PilotTick): preparar lo de la ruta, comprobar la última señal pedida y repintar el mapa grande.
        void RoomMapTick()
        {
            if (!RoomMapOn) return;
            MapGeoEnsure();
            RoomSignalCheck();
            RoomStatesCheck();
            try { _serviceHud?.BigMapInvalidate(); } catch { }
        }

        // Lo de la ruta (agujas y señales con su número), una vez por ruta y en segundo plano.
        void MapGeoEnsure()
        {
            string dir = _curRoute?.Path;
            if (!string.IsNullOrEmpty(dir) && _roomGeoFor != dir && !_roomGeoBuilding)
            {
                _roomGeoBuilding = true; _roomGeoFor = dir; _roomGeo = null;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        var g = await RouteGraphFor(dir); var geo = RoomMapGeo.Build(g, dir);
                        RoomLog($"ruta preparada en {sw.ElapsedMilliseconds} ms: {geo.Switches.Count} agujas, {geo.Heads.Count} cabezas de señal, {geo.Signals.Count} señales principales · "
                                + (geo.CanControl ? "numeración de Open Rails" : "sin numeración (" + geo.SignalNote + ")"));
                        BeginInvoke((Action)(() => { if (_roomGeoFor == dir) _roomGeo = geo; }));
                    }
                    catch (Exception ex) { RoomLog("no se ha podido preparar la ruta: " + ex.Message); }
                    finally { _roomGeoBuilding = false; }
                });
            }
        }

        void RoomStatesCheck()
        {
            var p = _pilot;
            if (p.StatesAt != _roomStatesSeen && p.States != null)
            {
                // (Open Rails manda dos bytes de más al final)
                if (_roomStatesSeen == DateTime.MinValue) RoomLog($"foto de agujas del despachador: {Math.Max(0, p.States.Length - 2)} agujas" + (_roomGeo != null ? $" (la ruta tiene {_roomGeo.Switches.Count})" : ""));
                _roomStatesSeen = p.StatesAt;
                foreach (var kv in _roomMine.ToList())
                {
                    if (p.StatesAt <= kv.Value.at.AddSeconds(0.5)) continue;   // foto de antes del cambio
                    if (DateTime.UtcNow - kv.Value.at > TimeSpan.FromSeconds(40) || kv.Key >= p.States.Length) { _roomMine.Remove(kv.Key); continue; }
                    if (p.States[kv.Key] != kv.Value.pos)
                    {
                        _roomMine.Remove(kv.Key);
                        RoomLog($"la aguja {kv.Key} ha vuelto a {p.States[kv.Key]}: Open Rails la ha puesto para la ruta de un tren");
                        try { _serviceHud?.MapNotice(Tr("Open Rails ha vuelto a poner la aguja: está en la ruta que tiene preparada para un tren.")); } catch { }
                    }
                }
            }
            if (p.SigStatesAt != _roomSigsSeen && p.SigStates != null)
            {
                bool ok = _roomGeo != null && p.SigStates.Length == 2 * _roomGeo.Heads.Count;
                if (_roomSigsSeen == DateTime.MinValue || !ok) RoomLog($"foto de señales del despachador: {p.SigStates.Length / 2} cabezas" + (_roomGeo != null ? $" · la ruta tiene {_roomGeo.Heads.Count}: {(ok ? "cuadra" : "NO CUADRA (no se pintan)")}" : ""));
                _roomSigsSeen = p.SigStatesAt;
            }
        }

        void RoomSwitchSet(int tx, int tz, int id, int pos)
        {
            if (_roomGeo != null && _roomGeo.ById.TryGetValue((tx, tz, id), out var s))
            {
                _roomSwSet[s.Order] = (pos, DateTime.UtcNow); _roomSwVer++;
                if (_roomAsked.TryGetValue(s.Order, out var asked) && asked.pos == pos && DateTime.UtcNow - asked.at < TimeSpan.FromSeconds(15)) { _roomAsked.Remove(s.Order); _roomMine[s.Order] = (pos, DateTime.UtcNow); }
            }
            RoomLog($"el despachador ha cambiado la aguja {tx} {tz} {id} → {pos}");
            try { _serviceHud?.BigMapInvalidate(); } catch { }
        }

        // Lo que pinta el mapa grande ahora (null fuera de una sala o sin la ruta preparada).
        RoomMapView RoomMapNow()
        {
            if (LocalMapOn) return LocalMapNow();   // conducción libre (MainMenuForm.MapaLocal.cs)
            if (!RoomMapOn || _roomGeo == null || _roomGeoFor != _curRoute?.Path) return null;
            var p = _pilot; var geo = _roomGeo;
            var st = p.States; var statesAt = p.StatesAt; var sig = p.SigStates;
            var bl = p.Blocked; if (bl != null && bl.Length != geo.Switches.Count) bl = null;   // (otra ruta u otra numeración: no se usa)
            var v = new RoomMapView
            {
                Geo = geo,
                SignalsOk = sig != null && sig.Length == 2 * geo.Heads.Count && geo.Heads.Count > 0,
                SwitchPos = s =>
                {
                    if (_roomSwSet.TryGetValue(s.Order, out var o) && o.at > statesAt) return o.pos;
                    return st != null && s.Order < st.Length ? st[s.Order] : -1;
                },
                HeadState = k => sig != null && 2 * k < sig.Length ? sig[2 * k] - 1 : -1,
                SwitchLock = s => bl != null && s.Order >= 0 && s.Order < bl.Length ? bl[s.Order] : '.',
                LocksKnown = bl != null
            };
            int PosOf(int o) => _roomSwSet.TryGetValue(o, out var ch) && ch.at > statesAt ? ch.pos : st != null && o < st.Length ? st[o] : -1;
            v.Paths = TrainPaths(geo, p.Trains, p.TrainTrack, PosOf, st, _roomSwVer, (_txtMPUser?.Text ?? "").Trim());
            foreach (var t in p.Trains)
                if (!t.player && OrGeo.TryLatLon(t.tx * 2048.0 + t.x, t.tz * 2048.0 + t.z, out double la, out double lo)) v.AiTrains.Add((t.name, la, lo, t.kmh));
            return v;
        }

        // Los demás jugadores de la sala, como marcadores del mapa (null fuera de una sala).
        List<LiveMarker> RoomPlayers()
        {
            if (!RoomMapOn) return null;
            string me = (_txtMPUser?.Text ?? "").Trim();
            var l = new List<LiveMarker>();
            foreach (var t in _pilot.Trains)
            {
                if (!t.player || string.Equals(t.name, me, StringComparison.Ordinal)) continue;
                if (!OrGeo.TryLatLon(t.tx * 2048.0 + t.x, t.tz * 2048.0 + t.z, out double la, out double lo)) continue;
                var c = RoomColor(t.name);
                // en la cabeza (Open Rails manda la cola) y con su rumbo: los primeros metros por la vía en el sentido de la marcha
                bool hasHdg = false; double hdg = 0;
                try
                {
                    if (_roomGeo?.Graph != null && _pilot.TrainTrack.TryGetValue(t.name, out var k))
                    {
                        var geo = _roomGeo; var st = _pilot.States; var stAt = _pilot.StatesAt;
                        int Pos(int n) => !geo.OrderOf.TryGetValue(n, out int o) ? -1
                                          : _roomSwSet.TryGetValue(o, out var ch) && ch.at > stAt ? ch.pos : st != null && o < st.Length ? st[o] : -1;
                        var pts = geo.Graph.PathAhead(k.node, la, lo, k.dir, k.mu, k.len, 25, Pos, out _);
                        if (pts != null && pts.Count >= 2) { (la, lo) = pts[0]; hdg = LiveMates.Bearing(pts[0].lat, pts[0].lon, pts[^1].lat, pts[^1].lon); hasHdg = true; }
                    }
                }
                catch { }
                bool disp = t.name == _pilot.DispatcherName, helper = _pilot.Helpers.Contains(t.name);
                string role = disp ? " · " + Tr("despachador") : helper ? " · " + Tr("ayudante") : "";
                l.Add(new LiveMarker { Id = "@mp:" + t.name, Name = t.name, Short = t.name, Train = "", Company = "", Room = true, HasHeading = hasHdg, Heading = hdg,
                                       Role = disp ? Tr("Despachador de la sala") : helper ? Tr("Ayudante del despachador") : Tr("Jugador de la sala"),
                                       Status = (t.kmh >= 1 ? $"{t.kmh:0} km/h" : Tr("parado")) + role, Lat = la, Lon = lo, SpeedKmh = t.kmh, Color = c });
            }
            return l;
        }

        Color RoomColor(string name)
        {
            if (!_roomColors.TryGetValue(name, out var c)) { c = LiveMates.Palette[_roomColors.Count % LiveMates.Palette.Length]; _roomColors[name] = c; }
            return c;
        }

        // «Tú» en la leyenda de una sala: tu papel (null fuera de una sala).
        string RoomMeStatus() => !RoomMapOn ? null : _pilot.IsDispatcher ? Tr("despachador") : _pilot.Helper ? Tr("ayudante") : Tr("jugador");

        // El camino por delante de cada tren, como el mapa de Open Rails (Ctrl+9, «Draw next path section»): hasta 5 km
        // por la vía según cómo están las agujas, del color del tren (tú, en verde; los de la IA, en gris). Se rehace solo
        // si cambian los trenes o las agujas (statesKey / ver).
        object _pathsTrains, _pathsStates; int _pathsVer; string _pathsFor;
        List<(Color col, List<(double lat, double lon)> pts, (double lat, double lon)? against)> _pathsCache;
        List<(Color col, List<(double lat, double lon)> pts, (double lat, double lon)? against)> TrainPaths(RoomMapGeo geo,
            List<(string name, int tx, int tz, double x, double z, double kmh, bool player)> trains,
            Dictionary<string, (int node, int mu, int dir, double len)> track, Func<int, int> posOfOrder, object statesKey, int ver, string me)
        {
            if (_pathsCache != null && ReferenceEquals(trains, _pathsTrains) && ReferenceEquals(statesKey, _pathsStates) && ver == _pathsVer && _pathsFor == geo?.Graph?.GetHashCode() + me)
                return _pathsCache;
            var l = new List<(Color, List<(double, double)>, (double, double)?)>();
            if (geo?.Graph != null && trains != null && track != null)
                foreach (var t in trains)
                {
                    try
                    {
                        if (!track.TryGetValue(t.name, out var k) || !OrGeo.TryLatLon(t.tx * 2048.0 + t.x, t.tz * 2048.0 + t.z, out double la, out double lo)) continue;
                        var pts = geo.Graph.PathAhead(k.node, la, lo, k.dir, k.mu, k.len, 5000, n => geo.OrderOf.TryGetValue(n, out int o) ? posOfOrder(o) : -1, out var ag);
                        if (pts == null) continue;
                        bool mine = t.name == "@yo" || (!string.IsNullOrEmpty(me) && t.name == me);
                        l.Add((mine ? Color.FromArgb(120, 210, 150) : t.player ? RoomColor(t.name) : Color.FromArgb(170, 176, 181), pts, ag));
                    }
                    catch { }
                }
            _pathsCache = l; _pathsTrains = trains; _pathsStates = statesKey; _pathsVer = ver; _pathsFor = geo?.Graph?.GetHashCode() + me;
            return l;
        }

        // Un clic en el mapa grande sobre una aguja o una señal. Devuelve el aviso que se enseña (o null).
        string RoomMapClick(object hit, Point screen)
        {
            if (hit is List<RouteGraph.SwitchGeo> many) { SwitchChooser(many, screen); return null; }
            if (LocalMapOn) return LocalMapClick(hit, screen);
            if (!RoomMapOn) return null;
            if (hit is RouteGraph.SwitchGeo s)
            {
                // las de una travesía o un escape (comparten identificador): por su nodo, solo con el despachador fijo
                if (s.Hidden && !RoomFixedDispatcher) return Tr("Esta aguja va unida a otra (un escape): se cambia con la otra.");
                // (1.2.69) Si está o no en tu camino lo decide el despachador, como con Ctrl+9: el cálculo de aquí no coincidía
                // con el suyo y rechazaba agujas que Ctrl+9 sí dejaba. Si no la deja, el aviso sale en Open Rails.
                if (!RoomOnMyPath(s.Lat, s.Lon, null)) RoomLog($"clic en la aguja {s.Tx} {s.Tz} {s.Id}: el mapa no la ve en mi camino, decide el despachador");
                var v = RoomMapNow();
                int pos = v?.SwitchPos?.Invoke(s) ?? -1;
                // enclavada en Open Rails (lo dice el agente del despachador): no se pide
                char lk = v?.SwitchLock?.Invoke(s) ?? '.';
                if (RoomMapDraw.IsLocked(v, s))
                {
                    RoomLog($"clic en la aguja {s.Tx} {s.Tz} {s.Id}: enclavada ({lk}), no se pide");
                    return lk == 'S' ? Tr("Aguja enclavada: la reserva una señal abierta. Se libera cuando la señal vuelva a parada o pase el tren.")
                         : lk == 'O' ? Tr("Aguja enclavada: hay un tren encima.")
                         : Tr("Aguja enclavada: la tiene reservada un tren para su ruta.");
                }
                if (s.Hidden) _pilot.ThrowNode(s.Node, pos == 1 ? 0 : 1);
                else _pilot.Throw(s.Tx, s.Tz, s.Id, pos == 1 ? 0 : 1);
                _roomAsked[s.Order] = (pos == 1 ? 0 : 1, DateTime.UtcNow);
                RoomLog($"clic en la aguja {s.Tx} {s.Tz} {s.Id}{(s.Hidden ? $" (nodo {s.Node}, de una travesía)" : "")} (está en {pos}): se pide {(pos == 1 ? 0 : 1)}" + (_pilot.IsDispatcher ? " · soy el despachador" : ""));
                return Tr("Aguja pedida al despachador…");
            }
            if (hit is RoomMapGeo.Sig g)
            {
                RoomLog($"clic en la señal {g.Obj} · ayudante {_pilot.Helper} · despachador {_pilot.IsDispatcher} · numeración {_roomGeo.CanControl}");
                if (!_roomGeo.CanControl || g.Obj < 0) return Tr("Esta señal no se puede pedir desde aquí (versión de Open Rails distinta). Usa el mapa de Open Rails (Ctrl+9).");
                // (servidor de antes: el despachador, solo con Ctrl+9)
                if (_pilot.IsDispatcher && !_pilot.AgentOk) return Tr("Eres el despachador: las señales se cambian desde el mapa de Open Rails (Ctrl+9).");
                if (!_pilot.IsDispatcher && !_pilot.Helper) return Tr("Pídele al despachador que te nombre ayudante: lo hace con un clic en tu nombre en la leyenda de su mapa grande.");
                // (1.2.71) cualquier señal de la ruta, con todas las órdenes (y, con el despachador fijo, las de tu tren)
                if (!RoomOnMyPath(g.Lat, g.Lon, g.Hdg)) RoomLog($"clic en la señal {g.Obj}: fuera de mi camino (se deja igual)");
                RoomSignalMenu(g, screen);
                return null;
            }
            return null;
        }

        // Varias agujas bajo el ratón (los cuatro desvíos de una travesía están a 2 o 3 m): un menú para elegir cuál,
        // de izquierda a derecha en la pantalla, con cómo está cada una; al elegir, como un clic en ella.
        void SwitchChooser(List<RouteGraph.SwitchGeo> list, Point screen)
        {
            var v = RoomMapNow();
            var items = new List<(string, Color, Action)>();
            for (int i = 0; i < list.Count; i++)
            {
                var s = list[i];
                int pos = v?.SwitchPos?.Invoke(s) ?? -1;
                string how = pos == 0 ? Tr("vía directa") : pos == 1 ? Tr("vía desviada") : Tr("sin dato");
                if (v != null && RoomMapDraw.IsLocked(v, s)) how += " · " + Tr("enclavada");
                items.Add((string.Format(Tr("Aguja {0}: {1}"), i + 1, how), Color.Empty, () =>
                {
                    string msg = RoomMapClick(s, screen);
                    if (msg != null) try { _serviceHud?.MapNotice(msg); } catch { }
                }));
            }
            _serviceHud?.BigMapMenu(screen, string.Format(Tr("{0} agujas aquí (de izquierda a derecha)"), list.Count), items);
            RoomLog($"varias agujas bajo el ratón: {string.Join(", ", list.Select(s => s.Node))}");
        }

        // Sala compartida (despachador fijo): cada jugador solo cambia lo que está en el camino de su tren, la línea
        // discontinua (el despachador lo comprueba igual: SelectOR Despachador\Limites.cs). Aquí, para no pedirlo y avisar
        // al momento: a menos de 15 m de mi camino y, una señal, mirando en el sentido de la marcha. Sin datos de mi
        // tren, se deja pedir (decide el despachador).
        bool RoomOnMyPath(double lat, double lon, double? hdg)
        {
            if (_roomJoined == null || _roomGeo?.Graph == null || _pilot == null) return true;
            string me = (_txtMPUser?.Text ?? "").Trim();
            try
            {
                var t = _pilot.Trains.FirstOrDefault(x => x.name == me);
                if (t.name == null || !_pilot.TrainTrack.TryGetValue(me, out var k)) return true;
                if (!OrGeo.TryLatLon(t.tx * 2048.0 + t.x, t.tz * 2048.0 + t.z, out double la, out double lo)) return true;
                var geo = _roomGeo; var st = _pilot.States; var stAt = _pilot.StatesAt;
                int Pos(int n) => !geo.OrderOf.TryGetValue(n, out int o) ? -1
                                  : _roomSwSet.TryGetValue(o, out var ch) && ch.at > stAt ? ch.pos : st != null && o < st.Length ? st[o] : -1;
                var pts = geo.Graph.PathAhead(k.node, la, lo, k.dir, k.mu, k.len, 5000, Pos, out _);
                if (pts == null || pts.Count < 2) return true;
                // el camino empieza en la cabeza: el tren entero va de la cola (pts[0]) a su largo; lo de debajo no cuenta
                int best = -1; double bd = double.MaxValue;
                for (int i = 1; i < pts.Count; i++)
                {
                    double d = DistToLineM(lat, lon, new List<(double lat, double lon)> { pts[i - 1], pts[i] });
                    if (d < bd) { bd = d; best = i; }
                }
                if (bd > 15) return false;
                if (hdg == null) return true;
                double b = LiveMates.Bearing(pts[best - 1].lat, pts[best - 1].lon, pts[best].lat, pts[best].lon);
                double diff = Math.Abs(((SigBearing(geo.Graph, lat, lon, hdg.Value) - b) % 360 + 540) % 360 - 180);
                return diff < 90;
            }
            catch { return true; }
        }

        // El sentido de una señal (RoomMapGeo.Sig.Hdg: ángulo en la proyección del grafo, en radianes) como rumbo de
        // brújula en grados, el de LiveMates.Bearing (antes se comparaban sin convertir y salía «fuera de mi camino» al azar).
        internal static double SigBearing(RouteGraph g, double lat, double lon, double hdg)
        {
            var (x, y) = g.Proj(lat, lon);
            var (la2, lo2) = g.Unproj(x + Math.Cos(hdg) * 10, y + Math.Sin(hdg) * 10);
            return LiveMates.Bearing(lat, lon, la2, lo2);
        }

        // Clic en un jugador de la leyenda «EN LA SALA»: el despachador lo nombra o lo quita de ayudante (como Ctrl+9).
        // false = lo de siempre (centrar el mapa en él).
        bool RoomPlayerMenu(LiveMarker m, Point screen, Action center)
        {
            if (!RoomMapOn || !_pilot.AgentOk || !_pilot.IsDispatcher || m?.Id == null || !m.Id.StartsWith("@mp:")) return false;
            string name = m.Id.Substring(4);
            bool helper = _pilot.Helpers.Contains(name);
            var items = new List<(string, Color, Action)>
            {
                (helper ? Tr("Quitar de ayudante") : Tr("Hacer ayudante (agujas y señales)"), Color.Empty, () =>
                {
                    _pilot?.SetHelper(name, !helper);
                    RoomLog($"{(helper ? "quito de ayudante" : "nombro ayudante")} a {name}");
                    try { _serviceHud?.MapNotice(string.Format(helper ? Tr("{0} ya no es ayudante.") : Tr("{0} es ayudante: puede cambiar agujas y señales desde su mapa."), name)); } catch { }
                }),
                (null, Color.Empty, null),
                (Tr("Centrar en el mapa"), Color.Empty, () => center?.Invoke())
            };
            _serviceHud?.BigMapMenu(screen, name + (helper ? " · " + Tr("ayudante") : ""), items);
            return true;
        }

        // Menú de una señal (dentro del mapa grande): arriba, cómo está ahora; cada orden, del color de lo que pide.
        void RoomSignalMenu(RoomMapGeo.Sig g, Point screen)
        {
            int now = RoomMapNow() is RoomMapView v ? RoomMapDraw.SignalState(v, g) : -1;
            string state = now < 0 ? Tr("sin dato") : now == 0 ? Tr("Parada") : now <= 2 ? Tr("Rebase autorizado") : now <= 5 ? Tr("Anuncio de parada") : Tr("Vía libre");
            (string, Color, Action) Item(string text, int pick, Color col) => (text, col, () =>
            {
                // conducción libre: directamente a Open Rails, como Ctrl+9
                if (LocalMapOn) { _localMap.Signal(g.Obj, pick); RoomLog($"conducción libre: señal {g.Obj} → {pick}"); return; }
                _pilot?.Signal(g.Obj, pick);
                RoomLog($"señal {g.Obj}: se pide {pick}");
                if (pick == 1) _roomSigCheck = (g.Obj, DateTime.UtcNow, false);
                try { _serviceHud?.MapNotice(Tr("Señal pedida al despachador…")); } catch { }
            });
            var items = new List<(string, Color, Action)>
            {
                Item(Tr("Parada"), 1, RoomMapDraw.AspectColor(0)),
                Item(Tr("Anuncio de parada"), 2, RoomMapDraw.AspectColor(3)),
                Item(Tr("Vía libre"), 3, RoomMapDraw.AspectColor(6)),
                Item(Tr("Rebase autorizado"), 4, RoomMapDraw.CallOnWhite),
                (null, Color.Empty, null),
                Item(Tr("Automática (como la pone Open Rails)"), 0, Color.Empty)
            };
            if (!LocalMapOn && RoomFixedDispatcher) { items.Add((null, Color.Empty, null)); items.Add(RoomPassItem()); items.Add(RoomReleaseItem()); }   // (y las de tu tren: Tab y Ctrl+Tab)
            _serviceHud?.BigMapMenu(screen, string.Format(Tr("Señal · ahora: {0}"), state), items);
            RoomLog($"menú de la señal {g.Obj} abierto");
        }

        // ¿La sala la lleva el despachador fijo de SelectOR, con el servidor que sabe pedir paso, liberar y cambiar un desvío
        // por su nodo?
        bool RoomFixedDispatcher => _pilot != null && _pilot.DriverOk && _pilot.DispatcherName == "Despachador_SelectOR";

        (string, Color, Action) RoomPassItem() => (Tr("Pedir paso a la señal de delante (Tab)"), RoomMapDraw.AspectColor(6), () =>
        {
            _pilot?.Pass();
            RoomLog("pedir paso (como Tab)");
            try { _serviceHud?.MapNotice(Tr("Paso pedido para la señal de delante de tu tren…")); } catch { }
        });

        (string, Color, Action) RoomReleaseItem() => (Tr("Liberar la señal de mi tren (Ctrl+Tab)"), RoomMapDraw.AspectColor(0), () =>
        {
            _pilot?.Release();
            RoomLog("liberar la señal (como Ctrl+Tab)");
            try { _serviceHud?.MapNotice(Tr("Pedido: liberar la señal de tu tren…")); } catch { }
        });

        // Tras pedir «Parada»: en 15 s la foto del despachador tiene que traerla en parada.
        void RoomSignalCheck()
        {
            if (_roomSigCheck is not { } c || _roomGeo == null) return;
            var p = _pilot;
            if (p.SigStatesAt > c.at.AddSeconds(1.5) && p.SigStates is byte[] st && st.Length == 2 * _roomGeo.Heads.Count)
            {
                var heads = Enumerable.Range(0, _roomGeo.Heads.Count).Where(k => _roomGeo.Heads[k].Obj == c.obj && _roomGeo.Heads[k].Normal).ToList();
                if (heads.Count > 0 && heads.All(k => st[2 * k] - 1 == 0)) { _roomSigCheck = null; RoomLog($"señal {c.obj}: llega en parada (la numeración vale)"); return; }   // en parada: la numeración es buena
            }
            if (DateTime.UtcNow - c.at < TimeSpan.FromSeconds(15)) return;
            _roomSigCheck = null;
            if (p.SigStatesAt <= c.at) return;   // no ha llegado ninguna foto: no se sabe
            // Solo esa señal: la numeración ya se comprueba al entrar (las cabezas de la foto del despachador); que una no
            // cambie (Open Rails no lo deja, o el despachador no la da por de tu camino) no apaga las demás.
            RoomLog($"señal {c.obj}: NO llega en parada en 15 s (se siguen pidiendo señales)");
            try { _serviceHud?.MapNotice(Tr("La señal no se ha puesto en parada: Open Rails no lo ha permitido.")); } catch { }
        }

        void RoomMapError(string e)
        {
            RoomLog("el servidor dice: " + e);
            string msg = e switch
            {
                "busy" => Tr("Open Rails no deja cambiarla: hay un tren encima o en la ruta que tiene reservada."),
                "locked" => Tr("Esa aguja está reservada para otro tren."),
                "far" => Tr("Solo puedes cambiar agujas cerca de tu tren."),
                "nohelper" => Tr("Pídele al despachador que te nombre ayudante: lo hace con un clic en tu nombre en la leyenda de su mapa grande."),
                "notdisp" => Tr("Solo el despachador puede nombrar ayudantes."),
                "nodisp" => Tr("La sala no tiene despachador ahora mismo."),
                "noadmin" => Tr("Las órdenes a las señales son del administrador. Para tu tren: pedir paso (Tab) o liberar su señal (Ctrl+Tab)."),
                "nobot" => Tr("Liberar la señal solo se puede con el despachador fijo de la sala."),
                "nobotsw" => Tr("Esta aguja va unida a otra (un escape): se cambia con la otra."),
                "notpath" => Tr("El despachador no la deja: no está en tu camino (la línea discontinua de tu tren)."),
                _ => Tr("No estás dentro de la sala.")
            };
            try { _serviceHud?.MapNotice(msg); } catch { }
        }
    }
}
