// (1.2.90) La radio de la sala conduciendo: con la conexión de la sala (la del chat), y su mando en una ventanita encima de
// Open Rails (RadioHud), que sale cuando la conexión es con un servidor de SelectOR (READY con chat).
// (2026-11-10, petición) Canal de tu banda: cada 2 s, dónde va tu tren (la sala manda la posición de cada tren) y la dependencia
// por la que va (la del punto más cercano: la vía que dibuja el esquema de cada estación y la de cada tramo de línea; acierta en el
// 98 % de las señales de la ruta, y lo que falla está en el límite mismo entre dos bandas); su
// banda de regulación (Bandas.cs) es tu canal (oyes a quien hable en ella, y a sus jefes de circulación). Para cambiar de banda, dos veces seguidas la misma (en el límite entre dos no va saltando). Fuera de la ruta de
// la sala o sin saber aún dónde va, la radio de siempre: toda la sala.

using System;
using System.Collections.Generic;
using System.Linq;

namespace SelectOR
{
    public partial class MainMenuForm
    {
        RoomRadio _radio; RadioHud _radioHud;
        System.Windows.Forms.Timer _radioBandTimer;
        int _radioBand, _radioBandSeen;
        List<(double x, double y, int band)> _radioPts; RouteGraph _radioGraph; System.Threading.Tasks.Task _radioPtsTask;

        void RadioHook(SwitchPilot p)
        {
            RadioStop();
            string me = (_txtMPUser?.Text ?? "").Trim();
            _radio = new RoomRadio(p, this, me);
            _radio.Bands = () => _radioBand > 0 ? new[] { "b" + _radioBand } : null;
            p.ChatReset += () => { try { if (!IsDisposed) BeginInvoke(new System.Action(RadioShowHud)); } catch { } };
            _radioBand = _radioBandSeen = 0;
            _radioBandTimer = new System.Windows.Forms.Timer { Interval = 2000 };
            _radioBandTimer.Tick += (s, e) => RadioBandTick();
            _radioBandTimer.Start();
        }

        // la banda por la que va tu tren (0: no se sabe)
        void RadioBandTick()
        {
            try
            {
                int b = 0;
                var p = _pilot;
                if (p != null && p.Ready && p.MapOk && SalaRouteHere)
                {
                    if (_radioGraph == null)
                    {
                        var t = RouteGraphFor(_curRoute.Path);
                        if (!t.IsCompleted) return;
                        _radioGraph = t.Result;
                    }
                    // (una vez, en segundo plano, menos de un segundo) los puntos de cada dependencia con su banda: la vía de cada
                    // estación (cada 40 m de lo que dibuja su esquema) y la de los tramos de línea («Junto a…», «Línea…», que
                    // pueden ser de otra banda que la estación más cercana)
                    if (_radioGraph != null && _radioPts == null)
                    {
                        if (_radioPtsTask == null)
                        {
                            var g = _radioGraph;
                            _radioPtsTask = System.Threading.Tasks.Task.Run(() =>
                            {
                                var l = new List<(double x, double y, int band)>();
                                try
                                {
                                    foreach (var z in Schematic.Zones(g))
                                    {
                                        if (!Bandas.Of.TryGetValue(z.Name, out int zb)) continue;
                                        if (z.Line) { if (z.Anchors != null) foreach (var a in z.Anchors) l.Add((a.x, a.y, zb)); continue; }
                                        Schematic s0 = null; try { s0 = Schematic.Build(g, null, z); } catch { }
                                        if (s0 == null) continue;
                                        foreach (int e in s0.DrawnEdges)
                                            if (s0.DrawnRange(e, out double a0, out double b0))
                                                for (double o = a0; o <= b0; o += 40) { var (px, py) = g.PointAt(e, o); l.Add((px, py, zb)); }
                                    }
                                }
                                catch { }
                                _radioPts = l;
                            });
                        }
                        return;
                    }
                    string me = (_txtMPUser?.Text ?? "").Trim();
                    if (_radioGraph != null && _radioPts != null && _radioPts.Count > 0 && me.Length > 0)
                        foreach (var tr in p.Trains)
                        {
                            if (!tr.player || !string.Equals(tr.name, me, StringComparison.Ordinal)) continue;
                            if (!OrGeo.TryLatLon(tr.tx * 2048.0 + tr.x, tr.tz * 2048.0 + tr.z, out double la, out double lo)) break;
                            var (x, y) = _radioGraph.Proj(la, lo);
                            double best = double.MaxValue;
                            foreach (var q in _radioPts) { double d = (q.x - x) * (q.x - x) + (q.y - y) * (q.y - y); if (d < best) { best = d; b = q.band; } }
                            break;
                        }
                }
                if (b == _radioBand) { _radioBandSeen = b; return; }
                if (b != _radioBandSeen) { _radioBandSeen = b; return; }   // (la primera vez, se espera a la siguiente)
                _radioBand = b;
            }
            catch { }
        }

        void RadioShowHud()
        {
            if (_radio == null || _pilot == null || !_pilot.ChatOk) return;
            if (_radioHud == null || _radioHud.IsDisposed) _radioHud = new RadioHud(_prefs, _radio);
            if (!_radioHud.Visible) _radioHud.Show();
        }

        void RadioStop()
        {
            try { _radioBandTimer?.Stop(); _radioBandTimer?.Dispose(); } catch { }
            _radioBandTimer = null; _radioBand = _radioBandSeen = 0;
            try { _radioHud?.Close(); _radioHud?.Dispose(); } catch { }
            _radioHud = null;
            try { _radio?.Dispose(); } catch { }
            _radio = null;
        }
    }
}
