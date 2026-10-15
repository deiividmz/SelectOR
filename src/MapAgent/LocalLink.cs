// Conducción libre (sin multijugador): el mapa grande de SelectOR hace lo del mapa de Open Rails (Ctrl+9) — agujas,
// señales y trenes, y cambiarlas —, por una conexión con SelectOR dentro del mismo ordenador (127.0.0.1). Solo si
// SelectOR la ha pedido al lanzar la partida (SELECTOR_MAP_PORT y SELECTOR_MAP_TOKEN); en multijugador no hace nada.
//  · Lee, en cada vuelta del simulador (Simulator.Update), cada 0,25 s: la posición de las agujas (en el orden de
//    SWITCHSTATES), el aspecto de las cabezas de señal (en el orden de SIGNALSTATES: TDBIndex·100000 + trItemIndex) y
//    las agujas enclavadas; y cada segundo, los trenes de la IA. Manda solo lo que cambia (y todo cada 10 s).
//  · Las órdenes de SelectOR se hacen en esa misma vuelta, con las funciones que usa Ctrl+9 sin multijugador:
//    Signals.RequestSetSwitch(nodo, posición) y, en la señal, ClearHoldSignalDispatcher / RequestHoldSignalDispatcher /
//    RequestApproachAspect / RequestLeastRestrictiveAspect / SetManualCallOn. Open Rails dice que no a lo mismo que a
//    Ctrl+9 (aguja reservada por una señal o con un tren encima).
// Protocolo (líneas de texto):
//   OR→S  HELLO token · SW base64 · SIG base64 · BLK códigos · TRAINS n tx tz x z kmh p nodo mu sentido largo;… (la cola,
//         como el MOVE de la red; «@yo» = tu tren) · RES THROW i pos ok|S|T|O|no ·
//         RES SIGNAL índice orden ok|no
//   S→OR  THROW i pos · SIGNAL índice orden (0 automática · 1 parada · 2 anuncio · 3 vía libre · 4 rebase autorizado)
//         CAB tipo valor · BTN orden 1|0   (mandos de SelectOR, en cualquier modo: Controls.cs)
// Si algo falla, se apaga solo y Open Rails sigue como siempre.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using HarmonyLib;
using Orts.Formats.Msts;
using Orts.MultiPlayer;
using Orts.Simulation;
using Orts.Simulation.Physics;
using Orts.Simulation.Signalling;

namespace SelectOR.MapAgent
{
    public static class LocalLink
    {
        static int _port; static string _token;
        static readonly ConcurrentQueue<string> _in = new ConcurrentQueue<string>();
        static readonly ConcurrentQueue<string> _out = new ConcurrentQueue<string>();
        static readonly AutoResetEvent _outReady = new AutoResetEvent(false);
        static volatile bool _connected, _fresh;
        static readonly Stopwatch _clock = Stopwatch.StartNew();
        static long _nextState, _nextTrains, _resendAll;
        static string _lastSw, _lastSig, _lastBlk;
        static TrackNode[] _swOrder; static object _swOf;
        static SignalHead[] _heads; static object _headsOf;
        static int _errors;
        static bool _mapOn = true, _wasConnected;   // _mapOn: lo del mapa (solo Conducción libre; SELECTOR_MAP_NOMAP=1: solo mandos)

        // (desde Agent.Install) 1 = puesto.
        public static int Install(Harmony h)
        {
            if (!int.TryParse(Environment.GetEnvironmentVariable("SELECTOR_MAP_PORT"), out _port) || _port <= 0) return 0;
            _token = Environment.GetEnvironmentVariable("SELECTOR_MAP_TOKEN");
            _mapOn = Environment.GetEnvironmentVariable("SELECTOR_MAP_NOMAP") != "1";
            if (string.IsNullOrEmpty(_token)) return 0;
            var upd = AccessTools.Method(typeof(Simulator), "Update", new[] { typeof(float) });
            if (upd == null) return 0;
            h.Patch(upd, postfix: new HarmonyMethod(typeof(LocalLink), nameof(AfterSimUpdate)));
            new Thread(Net) { IsBackground = true, Name = "SelectOR mapa (conducción libre)" }.Start();
            Agent.Log($"conexión con SelectOR por 127.0.0.1:{_port}: " + (_mapOn ? "mapa grande (conducción libre) y mandos" : "mandos"));
            return 1;
        }

        // ---- conexión con SelectOR (hilo aparte) ----

        static void Net()
        {
            int fails = 0;
            while (true)
            {
                try
                {
                    using (var tcp = new TcpClient())
                    {
                        tcp.NoDelay = true;
                        tcp.Connect("127.0.0.1", _port);
                        var ns = tcp.GetStream();
                        var w = new StreamWriter(ns, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                        w.WriteLine("HELLO " + _token);
                        while (_out.TryDequeue(out _)) { }
                        _fresh = true; _connected = true;
                        Agent.Log("conducción libre: conectado con SelectOR");
                        fails = 0;
                        var reader = new Thread(() =>
                        {
                            try
                            {
                                var r = new StreamReader(ns, new UTF8Encoding(false));
                                string line;
                                while ((line = r.ReadLine()) != null) if (line.Length > 0 && line.Length < 200) _in.Enqueue(line);
                            }
                            catch { }
                            _connected = false; _outReady.Set();
                        }) { IsBackground = true };
                        reader.Start();
                        while (_connected)
                        {
                            _outReady.WaitOne(2000);
                            while (_connected && _out.TryDequeue(out var line)) w.WriteLine(line);
                        }
                    }
                }
                catch { fails++; }
                _connected = false;
                Thread.Sleep(fails > 10 ? 5000 : 1500);   // SelectOR cerrado o aún sin escuchar: se reintenta
            }
        }

        static void Send(string line) { if (!_connected) return; if (_out.Count < 500) _out.Enqueue(line); _outReady.Set(); }

        // ---- en cada vuelta del simulador ----

        static void AfterSimUpdate(Simulator __instance)
        {
            if (_errors > 20) return;
            try
            {
                var sim = __instance;
                if (sim == null) return;
                bool mp = MPManager.IsMultiPlayer();
                long now = _clock.ElapsedMilliseconds;
                // los mandos (palancas y botones), en cualquier modo y también en red; lo del mapa, solo en Conducción libre
                while (_in.TryDequeue(out var cmd)) { if (Controls.Do(cmd)) continue; if (!mp && _mapOn && sim.Signals != null) Command(sim, cmd); }
                if (_wasConnected && !_connected) Controls.ReleaseAll();   // (sin SelectOR, ningún botón se queda pulsado)
                _wasConnected = _connected;
                if (mp || !_mapOn || sim.Signals == null || !_connected) return;   // (en red manda el despachador: Agent.cs)
                if (_fresh) { _fresh = false; _lastSw = _lastSig = _lastBlk = null; _nextState = _nextTrains = 0; }
                if (now >= _nextState)
                {
                    _nextState = now + 250;
                    bool all = now >= _resendAll; if (all) _resendAll = now + 10000;
                    SendState(sim, all);
                }
                if (now >= _nextTrains) { _nextTrains = now + 1000; SendTrains(sim); }
            }
            catch (Exception ex) { Fail(ex); }
        }

        static TrackNode[] Switches(Simulator sim)
        {
            var nodes = sim.TDB?.TrackDB?.TrackNodes;
            if (nodes == null) return null;
            if (_swOf != nodes) { _swOrder = nodes.Where(t => t != null && t.TrJunctionNode != null).OrderBy(t => t.Index).ToArray(); _swOf = nodes; }
            return _swOrder;
        }

        static SignalHead[] Heads(Simulator sim)
        {
            var objs = sim.Signals.SignalObjects;
            if (objs == null) return null;
            if (_headsOf != objs)
            {
                var l = new SortedList<long, SignalHead>();
                foreach (var s in objs)
                    if (s != null && (s.Type == SignalObjectType.Signal || s.Type == SignalObjectType.SpeedSignal) && s.SignalHeads != null)
                        foreach (var h in s.SignalHeads) { long k = h.TDBIndex * 100000L + h.trItemIndex; if (!l.ContainsKey(k)) l.Add(k, h); }
                _heads = l.Values.ToArray(); _headsOf = objs;
            }
            return _heads;
        }

        static void SendState(Simulator sim, bool all)
        {
            var sw = Switches(sim);
            if (sw != null)
            {
                var b = new byte[sw.Length];
                for (int i = 0; i < sw.Length; i++) b[i] = (byte)sw[i].TrJunctionNode.SelectedRoute;
                string s = Convert.ToBase64String(b);
                if (all || s != _lastSw) { _lastSw = s; Send("SW " + s); }
                string blk = Blocked(sim, sw);
                if (blk != null && (all || blk != _lastBlk)) { _lastBlk = blk; Send("BLK " + blk); }
            }
            var hs = Heads(sim);
            if (hs != null)
            {
                var b = new byte[hs.Length * 2];
                for (int i = 0; i < hs.Length; i++) { b[2 * i] = (byte)((int)hs[i].state + 1); b[2 * i + 1] = (byte)(hs[i].draw_state + 1); }
                string s = Convert.ToBase64String(b);
                if (all || s != _lastSig) { _lastSig = s; Send("SIG " + s); }
            }
        }

        // Una letra por aguja: O tren encima · S reservada por una señal abierta · T reservada o pedida por otro tren (no
        // el tuyo: las de tu propio camino se pueden cambiar, como con Ctrl+9 o la G) · «.» libre.
        static string Blocked(Simulator sim, TrackNode[] sw)
        {
            var tcl = sim.Signals.TrackCircuitList;
            if (tcl == null) return null;
            Train mine = sim.PlayerLocomotive?.Train;
            var sb = new StringBuilder(sw.Length);
            foreach (var t in sw)
            {
                char c = '.';
                try
                {
                    var x = t.TCCrossReference;
                    if (x != null && x.Count > 0)
                    {
                        int i = x[0].Index;
                        var st = i >= 0 && i < tcl.Count ? tcl[i].CircuitState : null;
                        if (st != null)
                        {
                            if (st.HasTrainsOccupying()) c = 'O';
                            else if (st.SignalReserved >= 0) c = 'S';
                            else if (st.TrainClaimed != null && st.TrainClaimed.Count > 0) c = 'T';
                            else if (st.TrainReserved != null && st.TrainReserved.Train != null && st.TrainReserved.Train != mine) c = 'T';
                        }
                    }
                }
                catch { }
                sb.Append(c);
            }
            return sb.ToString();
        }

        static void SendTrains(Simulator sim)
        {
            var sb = new StringBuilder("TRAINS ");
            Train mine = sim.PlayerLocomotive?.Train;
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            foreach (var t in sim.Trains.ToArray())
            {
                try
                {
                    if (t == null || t.TrainType == Train.TRAINTYPE.STATIC) continue;
                    // como el MOVE de la red: la cola, su tramo y sentido, MUDirection y la longitud (para el camino por delante)
                    var r = t.RearTDBTraveller; if (r == null) continue;
                    sb.Append(t == mine ? "@yo" : "AI" + t.Number).Append(' ').Append(r.TileX).Append(' ').Append(r.TileZ).Append(' ')
                      .Append(r.X.ToString("0.0", ci)).Append(' ').Append(r.Z.ToString("0.0", ci)).Append(' ')
                      .Append((Math.Abs(t.SpeedMpS) * 3.6).ToString("0", ci)).Append(t == mine ? " 1 " : " 0 ")
                      .Append(r.TrackNodeIndex).Append(' ').Append((int)t.MUDirection).Append(' ').Append((int)r.Direction).Append(' ')
                      .Append(t.Length.ToString("0", ci)).Append(';');
                }
                catch { }
            }
            Send(sb.ToString());
        }

        // ---- órdenes de SelectOR (en la vuelta del simulador, como Ctrl+9) ----

        static void Command(Simulator sim, string cmd)
        {
            var t = cmd.Split(' ');
            if (t.Length >= 3 && t[0] == "THROW" && int.TryParse(t[1], out int i) && int.TryParse(t[2], out int pos) && (pos == 0 || pos == 1))
            {
                var sw = Switches(sim);
                if (sw == null || i < 0 || i >= sw.Length) { Send($"RES THROW {i} {pos} no"); return; }
                string blk = Blocked(sim, new[] { sw[i] });
                bool ok = sim.Signals.RequestSetSwitch(sw[i], pos);
                Send($"RES THROW {i} {pos} " + (ok ? "ok" : blk != "." ? blk : "no"));
                _nextState = 0;
                Agent.Log($"conducción libre: aguja {i} → {pos}: {(ok ? "cambiada" : "Open Rails no la deja (" + blk + ")")}");
                return;
            }
            if (t.Length >= 3 && t[0] == "SIGNAL" && int.TryParse(t[1], out int obj) && int.TryParse(t[2], out int pick) && pick >= 0 && pick <= 4)
            {
                var objs = sim.Signals.SignalObjects;
                var s = objs != null && obj >= 0 && obj < objs.Length ? objs[obj] : null;
                if (s == null || s.Type != SignalObjectType.Signal) { Send($"RES SIGNAL {obj} {pick} no"); return; }
                switch (pick)
                {
                    case 0: s.ClearHoldSignalDispatcher(); break;
                    case 1: s.RequestHoldSignalDispatcher(true); break;
                    case 2: s.RequestApproachAspect(); break;
                    case 3: s.RequestLeastRestrictiveAspect(); break;
                    case 4: s.SetManualCallOn(true); break;
                }
                Send($"RES SIGNAL {obj} {pick} ok");
                _nextState = 0;
                Agent.Log($"conducción libre: señal {obj} → {pick}");
            }
        }

        static void Fail(Exception ex)
        {
            _errors++;
            if (_errors <= 3) Agent.Log("conducción libre: error " + ex.GetType().Name + " " + ex.Message);
            if (_errors == 21) Agent.Log("conducción libre: demasiados errores, el mapa de SelectOR se apaga (Open Rails sigue como siempre)");
        }
    }
}
