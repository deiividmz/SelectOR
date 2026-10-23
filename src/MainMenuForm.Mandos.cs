// Mandos al conducir (Ajustes de SelectOR → Mandos; Joysticks.cs): mientras Open Rails está abierto, cada 25 ms se leen
// los mandos y, si algo cambia, se le manda al agente del mapa dentro de Open Rails por la conexión local
// (LocalMapLink: «CAB tipo valor» para las palancas, «CRUISE fracción máx» para la del control de crucero, «BTN orden
// 1|0» para los botones; MapAgent\Controls.cs). En
// cualquier modo (Actividad, Horario, Conducción libre, multijugador). Al conectarse Open Rails se le manda la posición
// de todas las palancas (como un mando de verdad: lo que marca la palanca es lo que hay).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace SelectOR
{
    public partial class MainMenuForm
    {
        Timer _joyTimer;
        List<Joy.Device> _joyDevs = new();
        readonly Dictionary<string, Joy.State> _joySt = new();
        readonly Dictionary<string, float> _joyAxSent = new();
        readonly Dictionary<string, bool> _joyBtnSent = new();
        DateTime _joyEnumAt = DateTime.MinValue, _joyConnAt = DateTime.MinValue;

        bool JoyWanted() => _prefs?.JoyEnabled == true && _prefs.JoyBindings != null && _prefs.JoyBindings.Count > 0;

        // Clima activado: al abrir SelectOR (con un poco de margen) se descarga el publicado, si ha cambiado (ClimaFiles.cs).
        async void ClimaSyncSoon()
        {
            if (_prefs?.ClimaEnabled != true) return;
            await System.Threading.Tasks.Task.Delay(15000);
            try { await ClimaFiles.SyncAsync(); } catch { }
        }

        void ShowSelectorSettings()
        {
            using var dlg = new SelectorSettingsDialog(_prefs);
            dlg.ShowDialog(this);
        }

        void JoyStart()
        {
            JoyStop();
            _joyAxSent.Clear(); _joyBtnSent.Clear(); _joyEnumAt = DateTime.MinValue; _joyConnAt = DateTime.MinValue;
            _joyTimer = new Timer { Interval = 25 };
            _joyTimer.Tick += (s, e) => JoyTick();
            _joyTimer.Start();
            RoomLog($"mandos: {_prefs.JoyBindings.Count} asignaciones");
        }

        void JoyStop()
        {
            if (_joyTimer == null) return;
            try { _joyTimer.Stop(); _joyTimer.Dispose(); } catch { }
            _joyTimer = null;
        }

        void JoyTick()
        {
            var l = _localMap; var binds = _prefs?.JoyBindings;
            if (l == null || binds == null) return;
            if (!l.Connected) return;
            if (l.ConnectedAt != _joyConnAt) { _joyConnAt = l.ConnectedAt; _joyAxSent.Clear(); _joyBtnSent.Clear(); }   // (Open Rails nuevo: todo otra vez)
            // los mandos: al principio y, si falta alguno de los asignados, cada 3 s (se enchufa más tarde)
            bool missing = binds.Any(b => !_joyDevs.Any(d => d.Key == b.Device));
            if (_joyEnumAt == DateTime.MinValue || (missing && DateTime.UtcNow - _joyEnumAt > TimeSpan.FromSeconds(3)))
            {
                _joyDevs = Joy.Source.Enumerate(); _joyEnumAt = DateTime.UtcNow;
                foreach (var d in _joyDevs) if (!_joySt.ContainsKey(d.Key)) _joySt[d.Key] = new Joy.State();
            }
            var ok = new HashSet<string>();
            foreach (var d in _joyDevs) if (Joy.Source.Read(d, _joySt[d.Key])) ok.Add(d.Key);
            // palancas: el valor, si cambia
            foreach (var b in binds.Where(b => b.Axis))
            {
                if (!ok.Contains(b.Device)) continue;
                float v = Joy.Value(b, _joySt[b.Device].Axes[Math.Max(0, Math.Min(5, b.Index))]);
                if (_joyAxSent.TryGetValue(b.Action, out float last) && Math.Abs(v - last) < 0.004f) continue;
                _joyAxSent[b.Action] = v;
                if (b.Action == "THROTTLE_DYNAMIC") JoyCombined(l, v);
                else if (b.Action == "CRUISE_SPEED")
                    l.Send("CRUISE " + v.ToString("0.000", CultureInfo.InvariantCulture) + " " + Math.Round(Math.Max(0, _svcMaxKmh)).ToString(CultureInfo.InvariantCulture));
                else l.Send("CAB " + b.Action + " " + v.ToString("0.000", CultureInfo.InvariantCulture));
            }
            // botones: pulsado si lo está cualquiera de los suyos
            foreach (var g in binds.Where(b => !b.Axis).GroupBy(b => b.Action))
            {
                bool down = g.Any(b => ok.Contains(b.Device) && Joy.Down(_joySt[b.Device], b.Index));
                bool known = _joyBtnSent.TryGetValue(g.Key, out bool was);
                if (known && was == down) continue;
                if (!known && !down) { _joyBtnSent[g.Key] = false; continue; }   // (al empezar, los sueltos no se mandan)
                _joyBtnSent[g.Key] = down;
                OrControl.Calm(OrControl.CalmFor(g.Key, !down));   // (como el pupitre: sin consultas a OR mientras cambia)
                l.Send("BTN " + g.Key + " " + (down ? "1" : "0"));
            }
        }

        // Regulador + freno dinámico en una palanca (v: + regulador, − freno dinámico, 0 los dos sueltos). Primero se suelta
        // el otro, y el freno dinámico solo se manda al ir hacia él o al volver (en punto muerto Open Rails avisa de que no
        // se puede usar cada vez que le llega, aunque sea un 0).
        void JoyCombined(LocalMapLink l, float v)
        {
            float thr = Math.Max(0, v), dyn = Math.Max(0, -v);
            float lastThr = _joyAxSent.TryGetValue("THROTTLE", out var t0) ? t0 : -1, lastDyn = _joyAxSent.TryGetValue("DYNAMIC_BRAKE", out var d0) ? d0 : 0;
            string F(float x) => x.ToString("0.000", CultureInfo.InvariantCulture);
            if (thr == 0 && lastThr != 0) { l.Send("CAB THROTTLE 0.000"); _joyAxSent["THROTTLE"] = 0; }
            if (dyn == 0 && lastDyn > 0) { l.Send("CAB DYNAMIC_BRAKE 0.000"); _joyAxSent["DYNAMIC_BRAKE"] = 0; }
            if (thr > 0) { l.Send("CAB THROTTLE " + F(thr)); _joyAxSent["THROTTLE"] = thr; }
            if (dyn > 0) { l.Send("CAB DYNAMIC_BRAKE " + F(dyn)); _joyAxSent["DYNAMIC_BRAKE"] = dyn; }
        }
    }
}
