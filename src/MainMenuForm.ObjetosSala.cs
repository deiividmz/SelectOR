// OBJETOS DE LA SALA (petición 2026-11-04). El administrador los pone desde el Puesto de Mando (ObjetosSalaForm.cs). Aquí:
//  · abrir esa ventana (solo el administrador; botón «🚧 Objetos» del puesto);
//  · mientras se conduce en la sala, leer la lista (Supabase: room_objects_get) cada 15 s y, si ha cambiado (o Open Rails
//    se acaba de conectar), mandársela al agente por el enlace local: «OBJ BEGIN», una línea por objeto, «OBJ END»
//    (MapAgent\Viajeros3D.Objetos.cs los dibuja).
//  · (2026-11-10) la indicación de cada responsable de circulación (la reparte el servidor de la sala: SwitchPilot.BtStates),
//    también al agente: «OBJSTATE id estado nueva» (nueva = 1: se acaba de dar; con «marche», suena el silbato si está cerca).

using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace SelectOR
{
    public partial class MainMenuForm
    {
        const string SalaObjRoute = "CGL_V22N_RE";
        ObjetosSalaForm _objetos;
        long _objSentVersion = -1; DateTime _objNextPoll = DateTime.MinValue; bool _objPolling;
        string _btSent; readonly System.Collections.Concurrent.ConcurrentQueue<(long id, string st)> _btFresh = new();

        void BtHook(SwitchPilot p)
        {
            _btSent = null;
            p.BtChanged += (id, st) => _btFresh.Enqueue((id, st));   // (en el hilo de la conexión: se manda en el siguiente segundo)
        }

        // (ObjectsTick) las indicaciones, al agente: las recién dadas y, si ha cambiado algo o se acaba de conectar, todas
        void BtTick(bool justConnected)
        {
            var link = _localMap; var p = _pilot;
            if (link == null || !link.Connected || p == null) return;
            if (justConnected) _btSent = null;
            var ci = CultureInfo.InvariantCulture;
            while (_btFresh.TryDequeue(out var f)) link.Send(string.Format(ci, "OBJSTATE {0} {1} 1", f.id, f.st));
            var now = p.BtStates;
            string sig = string.Join(",", now.OrderBy(kv => kv.Key).Select(kv => kv.Key + ":" + kv.Value));
            if (sig == _btSent) return;
            foreach (var kv in now) link.Send(string.Format(ci, "OBJSTATE {0} {1} 0", kv.Key, kv.Value));
            _btSent = sig;
        }

        void OpenObjetosSala()
        {
            if (!(Supa.IsSuperadmin || Supa.IsObjectEditor) || !Supa.IsLoggedIn) return;   // (1.2.91: también los autorizados)
            if (_objetos != null && !_objetos.IsDisposed) { _objetos.WindowState = System.Windows.Forms.FormWindowState.Normal; _objetos.Activate(); return; }
            if (!SalaRouteHere) return;
            string dir = _curRoute.Path;
            _objetos = new ObjetosSalaForm(dir, SalaObjRoute, () => HorarioMapData(dir)) { Changed = () => _objNextPoll = DateTime.MinValue };
            _objetos.Show(_dispatch != null && !_dispatch.IsDisposed ? (System.Windows.Forms.IWin32Window)_dispatch : this);
        }

        // (LocalMapTick, cada segundo) conduciendo en la sala con el agente conectado: la lista, si ha cambiado
        void ObjectsTick(bool justConnected)
        {
            if (justConnected) { _objSentVersion = -1; _objNextPoll = DateTime.MinValue; }
            if (_roomJoined == null && _roomPending == null) return;
            try { BtTick(justConnected); } catch { }
            var l = _localMap; if (l == null || !l.Connected) return;
            if (_objPolling || DateTime.UtcNow < _objNextPoll) return;
            _objPolling = true; _objNextPoll = DateTime.UtcNow.AddSeconds(15);
            _ = Poll();
            async Task Poll()
            {
                try
                {
                    var (json, err) = await Supa.RpcAsync("room_objects_get", new { p_route = SalaObjRoute });
                    if (err != null || json == null) return;
                    var list = ObjetosSalaForm.Parse(json, out long version);
                    if (version == _objSentVersion) return;
                    var link = _localMap; if (link == null || !link.Connected) return;
                    var ci = CultureInfo.InvariantCulture;
                    link.Send("OBJ BEGIN");
                    foreach (var o in list)
                        link.Send(string.Format(ci, "OBJ {0} {1} {2} {3} {4} {5:0.###} {6:0.###} {7:0.#}", o.Id, o.Kind, string.IsNullOrWhiteSpace(o.Figure) ? "*" : o.Figure, o.TileX, o.TileZ, o.X, o.Z, o.Bearing));
                    link.Send("OBJ END");
                    _objSentVersion = version;
                    RoomLog($"objetos de la sala: {list.Count} mandados a Open Rails");
                }
                catch { }
                finally { _objPolling = false; }
            }
        }
    }
}
