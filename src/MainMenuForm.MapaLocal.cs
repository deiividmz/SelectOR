// Mapa grande en Conducción libre (sin multijugador), como el mapa de Open Rails (Ctrl+9): agujas, señales y trenes
// de la IA, y cambiarlas desde él. Lo hace el agente del mapa dentro de Open Rails (MapAgent\LocalLink.cs), por una
// conexión con SelectOR en este mismo ordenador (LocalMapLink.cs); lo de la ruta y el dibujo son los de las salas
// (MainMenuForm.MapaSala.cs, RoomMap.cs). Solo en Conducción libre: en Actividad y Horarios, no.
//  · Aguja: un clic la cambia, si Open Rails la deja (como Ctrl+9). Las enclavadas (reservadas por una señal abierta,
//    por otro tren o con un tren encima), en azul con candado: no se piden.
//  · Señal: su menú (Parada · Anuncio · Vía libre · Rebase · Automática), sin despachador ni ayudantes.

using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Linq;

namespace SelectOR
{
    public partial class MainMenuForm
    {
        LocalMapLink _localMap;
        bool _localSeen;
        DateTime _localInvAt = DateTime.MinValue;

        bool LocalMapOn => _localMap != null && _localMap.Connected && _localMapIsMap;
        bool _localMapIsMap;   // la conexión es también para el mapa (Conducción libre); si no, solo para los mandos

        // Al lanzar: el mapa, solo con «-start -explorer / -exploreactivity» sin red; los mandos (MainMenuForm.Mandos.cs), en
        // cualquier modo si hay alguno asignado. Sin el agente, nada (Ctrl+9 y el teclado como siempre).
        void LocalMapPrepare(ProcessStartInfo psi, string args)
        {
            LocalMapStop();
            try
            {
                bool wantMap = args.StartsWith("-start", StringComparison.OrdinalIgnoreCase) && args.IndexOf("-explore", StringComparison.OrdinalIgnoreCase) >= 0
                               && args.IndexOf("-multiplayer", StringComparison.OrdinalIgnoreCase) < 0;
                bool wantJoy = JoyWanted();
                if (!wantMap && !wantJoy) return;
                var link = LocalMapLink.Start();
                if (link == null) { RoomLog("conducción libre: no se puede escuchar en 127.0.0.1 (sin mapa de agujas)"); return; }
                if (!MapAgentFiles.Apply(psi)) { link.Dispose(); RoomLog("conducción libre: no se ha podido poner el agente (sin mapa de agujas)"); return; }
                psi.Environment["SELECTOR_MAP_PORT"] = link.Port.ToString(CultureInfo.InvariantCulture);
                psi.Environment["SELECTOR_MAP_TOKEN"] = link.Token;
                if (!wantMap) psi.Environment["SELECTOR_MAP_NOMAP"] = "1";
                _localMapIsMap = wantMap;
                link.Result += (k, id, v, res) => { try { BeginInvoke((Action)(() => LocalMapResult(k, id, v, res))); } catch { } };
                link.Changed += () =>
                {
                    // (como mucho 4 veces por segundo)
                    if (DateTime.UtcNow - _localInvAt < TimeSpan.FromMilliseconds(240)) return;
                    _localInvAt = DateTime.UtcNow;
                    try { BeginInvoke((Action)(() => { try { _serviceHud?.BigMapInvalidate(); } catch { } })); } catch { }
                };
                _localMap = link; _roomSigsSeen = DateTime.MinValue;
                if (wantJoy) JoyStart();
                RoomLog($"── conducción libre · {_curRoute?.Name}: mapa grande por 127.0.0.1:{link.Port}");
            }
            catch { LocalMapStop(); }
        }

        void LocalMapStop()
        {
            JoyStop();
            if (_localMap == null) return;
            try { _localMap.Dispose(); } catch { }
            _localMap = null; _localSeen = false;
        }

        // Cada segundo (RoadTick): la ruta preparada y el registro de la conexión.
        void LocalMapTick()
        {
            if (_localMap == null) return;
            if (_localMap.Connected != _localSeen)
            {
                _localSeen = _localMap.Connected;
                RoomLog(_localSeen ? "conducción libre: Open Rails conectado" : "conducción libre: Open Rails desconectado");
            }
            if (!_localSeen) return;
            MapGeoEnsure();
            var l = _localMap;
            if (_roomGeo != null && l.SigStates != null && _roomSigsSeen == DateTime.MinValue)
            {
                _roomSigsSeen = l.SigStatesAt;
                bool ok = l.SigStates.Length == 2 * _roomGeo.Heads.Count, okSw = l.States != null && l.States.Length == _roomGeo.Switches.Count;
                RoomLog($"conducción libre: {l.SigStates.Length / 2} cabezas de señal ({(ok ? "cuadra" : "NO CUADRA: no se pintan")}) · "
                        + $"{l.States?.Length ?? 0} agujas ({(okSw ? "cuadra" : "NO CUADRA")}) · la ruta tiene {_roomGeo.Heads.Count} y {_roomGeo.Switches.Count}");
            }
        }

        RoomMapView LocalMapNow()
        {
            var l = _localMap; var geo = _roomGeo;
            if (l == null || geo == null || _roomGeoFor != _curRoute?.Path) return null;
            var st = l.States; var sig = l.SigStates;
            if (st != null && st.Length != geo.Switches.Count) st = null;   // (otra ruta u otra numeración: no se usa)
            var bl = l.Blocked; if (bl != null && bl.Length != geo.Switches.Count) bl = null;
            var v = new RoomMapView
            {
                Geo = geo,
                SignalsOk = sig != null && sig.Length == 2 * geo.Heads.Count && geo.Heads.Count > 0,
                SwitchPos = s => st != null && s.Order >= 0 && s.Order < st.Length ? st[s.Order] : -1,
                HeadState = k => sig != null && 2 * k < sig.Length ? sig[2 * k] - 1 : -1,
                SwitchLock = s => bl != null && s.Order >= 0 && s.Order < bl.Length ? bl[s.Order] : '.',
                LocksKnown = bl != null
            };
            v.Paths = TrainPaths(geo, l.Trains, l.TrainTrack, o => st != null && o < st.Length ? st[o] : -1, st, 0, null);
            foreach (var t in l.Trains)
                if (!t.player && OrGeo.TryLatLon(t.tx * 2048.0 + t.x, t.tz * 2048.0 + t.z, out double la, out double lo)) v.AiTrains.Add((t.name, la, lo, t.kmh));
            return v;
        }

        string LocalMapClick(object hit, Point screen)
        {
            if (hit is RouteGraph.SwitchGeo s)
            {
                var v = LocalMapNow();   // (también las de una travesía: aquí se piden por su número)
                int pos = v?.SwitchPos?.Invoke(s) ?? -1;
                if (pos < 0) return Tr("Todavía no se sabe cómo está esta aguja.");
                char lk = v.SwitchLock?.Invoke(s) ?? '.';
                if (RoomMapDraw.IsLocked(v, s))
                {
                    RoomLog($"conducción libre: clic en la aguja {s.Order}: enclavada ({lk}), no se pide");
                    return LockedText(lk);
                }
                _localMap.Throw(s.Order, pos == 1 ? 0 : 1);
                RoomLog($"conducción libre: clic en la aguja {s.Order} (está en {pos}): se pide {(pos == 1 ? 0 : 1)}");
                return null;
            }
            if (hit is RoomMapGeo.Sig g)
            {
                RoomLog($"conducción libre: clic en la señal {g.Obj} · numeración {_roomGeo?.CanControl}");
                if (_roomGeo == null || !_roomGeo.CanControl || g.Obj < 0) return Tr("Esta señal no se puede pedir desde aquí (versión de Open Rails distinta). Usa el mapa de Open Rails (Ctrl+9).");
                RoomSignalMenu(g, screen);
                return null;
            }
            return null;
        }

        string LockedText(char lk) => lk == 'S' ? Tr("Aguja enclavada: la reserva una señal abierta. Se libera cuando la señal vuelva a parada o pase el tren.")
                                    : lk == 'O' ? Tr("Aguja enclavada: hay un tren encima.")
                                    : Tr("Aguja enclavada: la tiene reservada un tren para su ruta.");

        // Lo que contesta Open Rails a una orden del mapa: si no la deja, el aviso.
        void LocalMapResult(string kind, int id, int v, string res)
        {
            RoomLog($"conducción libre: {kind} {id} {v} → {res}");
            if (res == "ok") { try { _serviceHud?.BigMapInvalidate(); } catch { } return; }
            string msg = kind == "THROW"
                ? (res == "S" || res == "T" || res == "O" ? LockedText(res[0]) : Tr("Open Rails no deja cambiarla: hay un tren encima o en la ruta que tiene reservada."))
                : Tr("Open Rails no deja cambiar esa señal.");
            try { _serviceHud?.MapNotice(msg); } catch { }
        }
    }
}
