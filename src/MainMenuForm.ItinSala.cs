// Itinerarios en el mapa grande del HUD (1.2.75), en una sala con el despachador fijo. Como en el puesto de mando
// (DispatchForm.Itinerarios.cs), pero para el maquinista: clic en una señal → «Establecer itinerario desde aquí…»; se
// dibujan los destinos posibles (el que va por el itinerario de tu hoja de ruta, en verde y con ★) y un clic en uno lo
// pide a la sala. La sala solo lo deja desde las dos señales que tiene delante tu tren (o al jefe de circulación de esa
// estación, o al administrador desde su puesto). «Anular itinerario» si ya hay uno desde esa señal. Los itinerarios de
// todos se ven en verde (anulándose, en ámbar) y sus agujas, enclavadas.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace SelectOR
{
    public partial class MainMenuForm
    {
        RoomMapView.ItinPick _hudPick;
        Dictionary<int, List<(double off, int dir, int obj)>> _roomSbe; RoomMapGeo _roomSbeFor;

        Dictionary<int, List<(double off, int dir, int obj)>> RoomSbe()
        {
            if (_roomGeo?.Graph == null) return null;
            if (_roomSbeFor != _roomGeo) { _roomSbe = SchematicCircuits.SignalsByEdge(_roomGeo.Graph, _roomGeo); _roomSbeFor = _roomGeo; }
            return _roomSbe;
        }

        // (RoomMapHook) las respuestas de la sala y la lista de itinerarios
        void RoomItinHook(SwitchPilot p)
        {
            p.ItinsChanged += () => { try { _serviceHud?.BigMapInvalidate(); } catch { } };
            p.ItinReply += (who, ok, o, text) =>
            {
                string me = (_txtMPUser?.Text ?? "").Trim();
                if (!string.Equals(who, me, StringComparison.OrdinalIgnoreCase)) return;
                RoomLog($"itinerario desde la señal {o}: {(ok ? "sí" : "no")} · {text}");
                try { BeginInvoke((Action)(() => { try { _serviceHud?.MapNotice(DispatchFormText(text)); } catch { } })); } catch { }
            };
        }

        static string DispatchFormText(string t)
        {
            int k = t.IndexOf(" · ", StringComparison.Ordinal);
            return k < 0 ? t : I18n.English ? t.Substring(k + 3) : t.Substring(0, k);
        }

        // (RoomMapNow) los itinerarios de la sala, el destino que se está eligiendo y sus agujas enclavadas
        void RoomItinView(RoomMapView v, Func<int, int> posOfOrder, HashSet<int> itinNodes)
        {
            var geo = _roomGeo; var sbe = RoomSbe();
            v.Pick = _hudPick;
            if (geo?.Graph == null || sbe == null || _pilot == null) return;
            foreach (var it in _pilot.Itins)
            {
                if (it.State != "queue") foreach (var (n, _) in it.Switches) itinNodes.Add(n);   // (en cola: aún no enclava)
                var sg = geo.Signals.FirstOrDefault(s => s.Obj == it.Origin);
                if (sg == null || !SchematicCircuits.SignalPlace(sbe, sg, out int e, out double off, out int dir)) continue;
                var pos = it.Switches.ToDictionary(x => x.node, x => x.pos);
                int kind = it.State == "cancel" ? 1 : it.State == "queue" ? 2 : 0;
                SchematicCircuits.Walk(geo.Graph, sbe, e, off, dir,
                    n => pos.TryGetValue(n, out int pp) ? pp : geo.OrderOf.TryGetValue(n, out int o) ? posOfOrder(o) : -1,
                    (ed, a, b) => v.Itins.Add((SchematicCircuits.PieceLatLon(geo.Graph, ed, a, b), kind)));
            }
        }

        // (RoomSignalMenu) lo de los itinerarios, arriba del menú de una señal
        List<(string, Color, Action)> RoomItinItems(RoomMapGeo.Sig g)
        {
            var l = new List<(string, Color, Action)>();
            if (LocalMapOn || !RoomFixedDispatcher || _pilot == null || g.Obj < 0) return l;
            var it = _pilot.ItinFrom(g.Obj);
            if (it == null) l.Add((Tr("Establecer itinerario desde aquí…"), RoomMapDraw.ItinGreen, () => RoomStartPick(g)));
            else if (it.State == "queue")
            {
                l.Add((string.Format(Tr("Itinerario {0}: en cola ({1}.º)"), it.Origin + " → " + (it.Dest >= 0 ? it.Dest.ToString() : Tr("topera")), it.Queue), Theme.Subtle, null));
                l.Add((Tr("Quitarlo de la cola"), Color.FromArgb(255, 183, 77), () => { _pilot.ItinDel(it.Origin); RoomLog($"quitar de la cola el itinerario desde la señal {it.Origin}"); }));
            }
            else l.Add((string.Format(Tr("Anular itinerario {0}"), it.Origin + " → " + (it.Dest >= 0 ? it.Dest.ToString() : Tr("topera"))), Color.FromArgb(255, 183, 77), () =>
            {
                _pilot.ItinDel(it.Origin);
                RoomLog($"anular el itinerario desde la señal {it.Origin}");
                try { _serviceHud?.MapNotice(Tr("Anulando el itinerario…")); } catch { }
            }));
            l.Add((null, Color.Empty, null));
            return l;
        }

        void RoomStartPick(RoomMapGeo.Sig g)
        {
            var geo = _roomGeo; var sbe = RoomSbe();
            if (geo?.Graph == null || sbe == null || !SchematicCircuits.SignalPlace(sbe, g, out int e, out double off, out int dir))
            { try { _serviceHud?.MapNotice(Tr("No se sabe por dónde sigue la vía desde esa señal.")); } catch { } return; }
            var opts = SchematicCircuits.Options(geo.Graph, sbe, e, off, dir);
            if (opts.Count == 0) { try { _serviceHud?.MapNotice(Tr("Desde esa señal no hay ningún destino a menos de 3 km.")); } catch { } return; }
            // el que va por el itinerario de tu hoja de ruta
            var plan = new HashSet<int>();
            try { foreach (var p in _road.Points) if (p.Path != null) foreach (int x in p.Path) plan.Add(x); } catch { }
            if (plan.Count > 0)
                foreach (var o in opts)
                {
                    var es = o.Pieces.Select(x => x.e).Distinct().Skip(1).ToList();   // (sin el tramo de la propia señal)
                    o.Mine = es.Count > 0 && es.All(plan.Contains);
                }
            var pick = new RoomMapView.ItinPick { Origin = g.Obj };
            foreach (var o in opts)
            {
                var po = new RoomMapView.ItinPickOpt { Opt = o };
                foreach (var (pe, a, b) in o.Pieces) po.Parts.Add(SchematicCircuits.PieceLatLon(geo.Graph, pe, a, b));
                var (x, y) = geo.Graph.PointAt(o.EndEdge, o.EndOff); (po.Lat, po.Lon) = geo.Graph.Unproj(x, y);
                pick.Opts.Add(po);
            }
            _hudPick = pick;
            RoomLog($"itinerario desde la señal {g.Obj}: {opts.Count} destinos ({string.Join(", ", opts.Select(o => (o.Dest >= 0 ? o.Dest.ToString() : "topera") + (o.Mine ? "★" : "")))})");
            try { _serviceHud?.MapNotice(string.Format(Tr("Itinerario desde la señal {0}: clic en el destino (un círculo). Clic fuera: cancelar."), g.Obj)); _serviceHud?.BigMapInvalidate(); } catch { }
        }

        // (RoomMapClick) eligiendo el destino: un círculo lo pide; otro sitio, nada
        string RoomPickClick(object hit)
        {
            var pk = _hudPick; _hudPick = null;
            try { _serviceHud?.BigMapInvalidate(); } catch { }
            if (pk == null || hit is not RoomMapView.ItinPickOpt o) return null;
            _pilot?.ItinSet(pk.Origin, o.Opt.Dest, o.Opt.Switches);
            RoomLog($"itinerario {pk.Origin} → {(o.Opt.Dest >= 0 ? o.Opt.Dest.ToString() : "topera")} pedido: agujas {string.Join(" ", o.Opt.Switches.Select(x => x.node + ":" + x.pos))}");
            return string.Format(Tr("Estableciendo el itinerario {0} → {1}…"), pk.Origin, o.Opt.Dest >= 0 ? o.Opt.Dest.ToString() : Tr("topera"));
        }
    }
}
