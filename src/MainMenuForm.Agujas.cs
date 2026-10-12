// Salas de «Servidores SelectOR»: los desvíos se ponen solos según el itinerario de la hoja de ruta (SwitchPilot).
//  · Al entrar en una sala con Open Rails, el piloto se conecta al servidor de la sala (solo cuando el escenario está
//    abierto: Open Rails ya está dentro y el piloto nunca puede quedarse de despachador).
//  · Cada vuelta de la hoja de ruta (1 s) le pasa los desvíos que hay por delante (hasta 90 s de marcha, entre 0,8 y
//    3 km) o aún bajo el tren, con su posición, la distancia y el tiempo hasta llegar; y los que ya ha dejado atrás.
//  · Prioridad: 3 servicio programado (turno), 2 tren de viajeros, 1 mercancías (o sin saber).
//  · Avisos: si un desvío lo tiene otro tren con más prioridad, una notificación y la línea de estado de la hoja de
//    ruta («espera»); cuando vuelve a ser suyo, otra.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace SelectOR
{
    public partial class MainMenuForm
    {
        SwitchPilot _pilot;
        bool _schedDriving;   // conduciendo un servicio programado (turno): prioridad máxima en los desvíos

        void PilotCreate() => PilotCreateFor(_roomHost, _roomPort);

        // También con «Unirse a un servidor» (host y puerto escritos a mano): si el servidor es el de SelectOR contesta
        // «READY» y el piloto trabaja; si no (el de Open Rails u otro), no contesta y el piloto se retira solo.
        void PilotCreateFor(string host, int port)
        {
            PilotStop();
            if (string.IsNullOrEmpty(host) || port <= 0) return;
            string name = (_txtMPUser?.Text ?? "").Trim();
            if (name.Length == 0) return;
            _pilot = new SwitchPilot(host, port, name, PilotPrio());
            _pilot.Changed += (key, before, now) => { try { BeginInvoke((Action)(() => PilotChanged(key, before, now))); } catch { } };
            _pilot.GateOwner += (tok, pw) => { try { BeginInvoke((Action)(() => RoomGateInherited(tok, pw))); } catch { } };
            _pilotToldReady = false;
        }

        bool _pilotToldReady;
        DateTime _pilotReadyAt, _pilotStatesSeen = DateTime.MinValue; bool _pilotNoStatesLogged; int _pilotRoadVer = -1;

        void PilotStop()
        {
            try { _pilot?.Dispose(); } catch { }
            _pilot = null;
        }

        int PilotPrio()
        {
            if (_schedDriving) return 3;
            try { return TrainIsPassenger(_drivenConsist ?? CurrentDrivenConsist()) == true ? 2 : 1; } catch { return 1; }
        }

        double PilotTrainLenM()
        {
            try { var sp = KnownSpec(_drivenConsist ?? CurrentDrivenConsist()); if (sp != null && sp.LengthM > 0) return sp.LengthM; } catch { }
            return 300;
        }

        double PilotLookM => Math.Max(800, Math.Min(3000, Math.Max(0, _orKmhLast) / 3.6 * 90));

        // Desde RoadTick, tras seguir al tren por el itinerario.
        void PilotTick()
        {
            if (_pilot == null || !_scenarioReady) return;
            _pilot.EnsureStarted();
            if (!_pilot.Ready) return;
            if (!_pilotToldReady)
            {
                _pilotToldReady = true;   // conectado con el servidor de la sala: que se sepa
                _pilotReadyAt = DateTime.UtcNow; _pilotStatesSeen = DateTime.MinValue; _pilotNoStatesLogged = false;
                PilotLog($"── piloto conectado · prioridad {PilotPrioName(PilotPrio())} · itinerario: {(_road.HasPlan ? $"{_road.Switches.Count} desvíos en {_road.TotalLen / 1000:0.0} km" : "sin marcar")}"
                       + (_road.SwitchesHidden > 0 ? $" · {_road.SwitchesHidden} sin poder pedir (comparten número)" : ""));
                try { _driveBar?.ShowMessage("🔀 " + Tr("Desvíos según tu itinerario: los pide SelectOR al servidor de la sala."), false, 8); } catch { }
            }
            _pilot.Prio = PilotPrio();
            var ahead = new List<(int, int, int, int, double, double)>();
            var passed = new List<(int, int, int)>();
            if (_road.HasPlan && !_road.OffRoute)
            {
                double v = Math.Max(3, Math.Max(0, _orKmhLast) / 3.6), tail = PilotTrainLenM() + 30, look = PilotLookM;
                var keys = new HashSet<string>();
                foreach (var s in _road.Switches)
                {
                    double d = s.d - _road.Progress;
                    if (d < -tail) passed.Add((s.tx, s.tz, s.id));
                    // un desvío por el que el itinerario vuelve a pasar (p. ej., tras invertir la marcha): solo la pasada
                    // más cercana; la siguiente se pide cuando se haya dejado atrás esta
                    else if (d <= look && ahead.Count < 6 && keys.Add(SwitchPilot.Key(s.tx, s.tz, s.id))) ahead.Add((s.tx, s.tz, s.id, s.sel, d, Math.Max(0, d) / v));
                }
            }
            _pilot.Tick(ahead, passed);
            if (_pilot.StatesAt != _pilotStatesSeen)
            {
                if (_pilotStatesSeen == DateTime.MinValue) PilotLog($"posiciones del despachador recibidas ({_pilot.States?.Length ?? 0} desvíos; la ruta tiene {_road.Graph?.JunctionOrder.Length ?? 0})");
                _pilotStatesSeen = _pilot.StatesAt;
            }
            else if (_pilotStatesSeen == DateTime.MinValue && !_pilotNoStatesLogged && (DateTime.UtcNow - _pilotReadyAt).TotalSeconds > 40)
            {
                _pilotNoStatesLogged = true;
                PilotLog("sin posiciones del despachador en 40 s: ¿el servidor es la versión de antes? (no se puede comprobar ni corregir nada)");
            }
            if (_road.Version != _pilotRoadVer) { _pilotRoadVer = _road.Version; if (_road.HasPlan) PilotLog($"itinerario: {_road.Switches.Count} desvíos en {_road.TotalLen / 1000:0.0} km" + (_road.SwitchesHidden > 0 ? $" · {_road.SwitchesHidden} sin poder pedir" : "")); }
            PilotCheckStates();
        }

        // Con la posición real que manda el despachador (cada 10 s, cuando algo cambia): un desvío del itinerario a menos
        // de 600 m que el servidor dio por puesto y que, en una foto POSTERIOR a eso, no está como se pidió, se vuelve a
        // pedir (FIX: el servidor se lo reenvía al despachador, salvo que este lo haya movido a mano). Si no llega otra foto
        // en 12 s (no ha cambiado nada), se insiste; como mucho 3 veces por desvío. Todo queda en
        // %AppData%\Open Rails\SelectOR\desvios.log.
        readonly Dictionary<string, (int n, DateTime at)> _pilotFix = new();
        void PilotCheckStates()
        {
            try
            {
                var st = _pilot?.States; var g = _road.Graph;
                if (st == null || g == null || g.JunctionOrder.Length == 0) return;
                var statesAt = _pilot.StatesAt;
                var now = DateTime.UtcNow;
                var pos = new Dictionary<int, int>();
                for (int i = 0; i < g.JunctionOrder.Length && i < st.Length; i++) pos[g.JunctionOrder[i]] = st[i];
                for (int i = 0; i < _road.Switches.Count && i < _road.SwitchNodes.Count; i++)
                {
                    var s = _road.Switches[i]; double d = s.d - _road.Progress;
                    if (d < 0 || d > 600) continue;
                    var ps = _pilot.StatusOf(s.tx, s.tz, s.id);
                    if (ps?.State != SwitchPilot.State.Granted) continue;
                    if (statesAt < ps.Since.AddSeconds(1)) continue;   // la foto es de antes de dárselo: no dice nada
                    if (!pos.TryGetValue(_road.SwitchNodes[i], out int real)) continue;
                    string key = SwitchPilot.Key(s.tx, s.tz, s.id);
                    _pilotFix.TryGetValue(key, out var fx);
                    if (real == s.sel)
                    {
                        if (fx.n > 0) { PilotLog($"desvío {key} (nodo {_road.SwitchNodes[i]}) a {d:0} m: ya está bien ({real}) tras {fx.n} reenvío(s)"); _pilotFix.Remove(key); }
                        continue;
                    }
                    // mal: otra foto posterior al último reenvío, o 12 s sin fotos nuevas (no ha cambiado nada)
                    if (fx.n > 0 && statesAt < fx.at && (now - fx.at).TotalSeconds < 12) continue;
                    if (fx.n >= 3) continue;
                    _pilotFix[key] = (fx.n + 1, now);
                    _pilot.Fix(s.tx, s.tz, s.id);
                    PilotLog($"desvío {key} (nodo {_road.SwitchNodes[i]}) a {d:0} m: quiere {s.sel}, está {real} · foto de hace {(now - statesAt).TotalSeconds:0} s,"
                           + $" dado hace {(now - ps.Since).TotalSeconds:0} s · reenvío {fx.n + 1} · tren {_orKmhLast:0} km/h"
                           + (_road.SwitchesHidden > 0 ? $" · {_road.SwitchesHidden} del itinerario sin poder pedir" : ""));
                }
            }
            catch { }
        }

        void PilotLog(string line)
        {
            try
            {
                string dir = Environment.GetEnvironmentVariable("SELECTOR_CACHE_DIR") is { Length: > 0 } t ? t   // (las pruebas, en su carpeta)
                           : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Open Rails", "SelectOR");
                string f = System.IO.Path.Combine(dir, "desvios.log");
                System.IO.File.AppendAllText(f, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {_curRoute?.Name} · {line}{Environment.NewLine}");
            }
            catch { }
        }

        // La línea de estado de la hoja de ruta mientras el piloto trabaja (null = la de siempre).
        (string text, Color color)? PilotNote()
        {
            if (_pilot == null || !_pilot.Ready || !_road.HasPlan || _road.OffRoute) return null;
            double tail = PilotTrainLenM() + 30, look = PilotLookM;
            var near = _road.Switches.Where(s => s.d - _road.Progress >= -tail && s.d - _road.Progress <= look).ToList();
            if (near.Count == 0) return null;
            foreach (var s in near)
            {
                var st = _pilot.StatusOf(s.tx, s.tz, s.id);
                if (st?.State == SwitchPilot.State.Waiting)
                    return ("🔀 " + string.Format(Tr("Desvío a {0}: lo tiene el tren de {1} ({2}) · espera"),
                                PilotDist(s.d - _road.Progress), st.Holder, PilotPrioName(st.HolderPrio)), Color.FromArgb(240, 196, 90));
            }
            bool all = near.All(s => _pilot.StatusOf(s.tx, s.tz, s.id)?.State == SwitchPilot.State.Granted);
            return all ? ("🔀 " + Tr("Desvíos del itinerario preparados"), Theme.AccentHi) : null;
        }

        string PilotPrioName(int p) => p >= 3 ? Tr("servicio programado") : p == 2 ? Tr("viajeros") : Tr("mercancías");

        static string PilotDist(double m) => m < 1000 ? Math.Max(0, Math.Round(m / 10) * 10) + " m" : (m / 1000).ToString("0.0", System.Globalization.CultureInfo.CurrentCulture) + " km";

        void PilotChanged(string key, SwitchPilot.Status before, SwitchPilot.Status now)
        {
            try
            {
                if (RoadHudAlive) _roadHud.Invalidate();
                var sw = _road.Switches.FirstOrDefault(s => SwitchPilot.Key(s.tx, s.tz, s.id) == key);
                if (before == null || before.State != now.State || before.Holder != now.Holder)
                    PilotLog($"desvío {key} a {sw.d - _road.Progress:0} m: " + (now.State == SwitchPilot.State.Granted ? $"puesto para ti ({now.Sel})"
                           : now.State == SwitchPilot.State.Waiting ? $"en espera: lo tiene {now.Holder} ({PilotPrioName(now.HolderPrio)})" : "rechazado por el servidor"));
                string where = sw.tx == 0 && sw.tz == 0 && sw.id == 0 ? "" : " " + string.Format(Tr("a {0}"), PilotDist(sw.d - _road.Progress));
                if (now.State == SwitchPilot.State.Waiting && before?.State != SwitchPilot.State.Waiting)
                    EnqueueToast(new NotificationToast("🔀", Tr("Desvío ocupado"),
                        string.Format(Tr("El desvío{0} lo usa antes el tren de {1} ({2}). Espera a que pase: tu señal puede abrirse hacia la otra vía."),
                                      where, now.Holder, PilotPrioName(now.HolderPrio)),
                        "SelectOR · " + Tr("Desvíos"), Color.FromArgb(240, 196, 90)));
                else if (now.State == SwitchPilot.State.Granted && before?.State == SwitchPilot.State.Waiting)
                    EnqueueToast(new NotificationToast("🔀", Tr("Desvío para ti"),
                        string.Format(Tr("El desvío{0} ya está puesto para tu itinerario."), where),
                        "SelectOR · " + Tr("Desvíos"), Theme.AccentHi));
            }
            catch { }
        }
    }
}
