// (1.2.84, petición) Conducción libre: el itinerario marcado (la hoja de ruta) pone también las agujas.
//  · Solo en Conducción libre sin red: ni en la sala compartida ni en servidores, ni en servicios de empresa, Horarios o
//    Actividades (_launchFreeDrive, y el agente del mapa conectado: LocalMapOn, que ya exige -explore sin multijugador).
//  · Cada segundo (RoadTick): las agujas del itinerario (RoadBook.SwitchPlan, por su nodo, de punta y de talón) que el tren
//    tiene en los próximos 2,5 km y no están como pide el itinerario se le piden al agente de Open Rails (THROW, lo mismo
//    que Ctrl+9: Signals.RequestSetSwitch). Open Rails dice que no con un tren encima o con una señal abierta por ella
//    (por eso se piden con antelación, antes de que se abra la señal de delante); se reintenta cada 4 s mientras se pueda.
//  · Cada aguja, una vez por pasada: si ya está como debe o se ha puesto, no se vuelve a tocar (si el maquinista la
//    cambia luego a mano, se respeta). Con un itinerario nuevo (rehecho al salirse), se empieza de nuevo.

using System;
using System.Collections.Generic;
using System.Linq;

namespace SelectOR
{
    public partial class MainMenuForm
    {
        bool _launchFreeDrive;
        int _itinSwVersion = -1;
        readonly HashSet<(int node, double d)> _itinSwDone = new();
        readonly Dictionary<(int node, double d), DateTime> _itinSwAsked = new();
        bool _itinSwSaid;
        const double ItinSwAheadM = 2500;

        void ItinSwitchReset() { _itinSwVersion = -1; _itinSwDone.Clear(); _itinSwAsked.Clear(); _itinSwSaid = false; }

        void ItinSwitchTick()
        {
            try
            {
                if (!_launchFreeDrive || _mpClientRun || !LocalMapOn || _prefs?.ItinAutoSwitches == false) return;   // (el interruptor: «Tu viaje» → Itinerario)
                var geo = _roomGeo; var l = _localMap;
                if (geo == null || _roomGeoFor != _curRoute?.Path || !_road.HasPlan || _road.SwitchPlan.Count == 0) return;
                var st = l.States; var bl = l.Blocked;
                if (st == null || st.Length != geo.Switches.Count) return;
                if (bl != null && bl.Length != st.Length) bl = null;
                if (_road.Version != _itinSwVersion) { _itinSwVersion = _road.Version; _itinSwDone.Clear(); _itinSwAsked.Clear(); }
                if (_road.OffRoute) return;   // (fuera del itinerario: hasta que se rehaga)
                double at = _road.Progress; var utc = DateTime.UtcNow;
                int asked = 0;
                foreach (var (d, node, sel) in _road.SwitchPlan)
                {
                    if (d < at - 5) continue;                 // ya pasada
                    if (d > at + ItinSwAheadM) break;         // (en orden de marcha)
                    var key = (node, d);
                    if (_itinSwDone.Contains(key)) continue;
                    if (!geo.OrderOf.TryGetValue(node, out int order) || order < 0 || order >= st.Length) { _itinSwDone.Add(key); continue; }
                    if (st[order] == sel) { _itinSwDone.Add(key); continue; }
                    char lk = bl != null ? bl[order] : '.';
                    if (lk == 'O') continue;                  // un tren encima: Open Rails no la deja
                    if (_itinSwAsked.TryGetValue(key, out var last) && (utc - last).TotalSeconds < 4) continue;
                    _itinSwAsked[key] = utc;
                    l.Throw(order, sel);
                    asked++;
                    RoomLog($"conducción libre: aguja {order} (nodo {node}) a {sel} según el itinerario, a {d - at:0} m{(lk != '.' ? $" (enclavada: {lk})" : "")}");
                    if (asked >= 4) break;                    // (unas pocas por vuelta)
                }
                if (asked > 0 && !_itinSwSaid)
                {
                    _itinSwSaid = true;
                    try { _serviceHud?.MapNotice(Tr("Las agujas de tu itinerario se ponen solas al acercarte (Conducción libre).")); } catch { }
                }
            }
            catch { }
        }
    }
}
