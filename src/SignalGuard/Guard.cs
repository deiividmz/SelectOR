// Guardián de señales del multijugador de Open Rails (lo carga SelectOR al entrar en una partida en red).
//
// En multijugador solo el Open Rails del despachador calcula las señales, y lo hace mal en una cosa clave: una señal
// que ya se abrió para un tren no vuelve a mirar su cantón (SignalObject.Update solo llama a checkRouteState si NO
// está «Reserved»). Si después entra otro tren en el cantón —en multijugador las reservas tampoco lo impiden—, el
// script de la señal sigue viendo el cantón libre: la señal sigue abierta y la de detrás no anuncia la parada.
// Además el estado de las señales solo se manda a los demás jugadores cada 10 s (MPManager.MPUpdateInterval).
//
// Lo que hace, solo en el despachador:
//  1. CANTÓN DE VERDAD: justo antes de que el script de una señal principal calcule su aspecto (StateUpdate), si Open
//     Rails le va a decir que su cantón está libre, se comprueba de verdad:
//       · señal abierta para un tren: la ruta que Open Rails le reservó (signalRoute, tramo a tramo hasta la siguiente
//         señal): si hay otro tren en ella, ocupado. (Sin mirar la posición de los desvíos: los de su ruta los pone Open
//         Rails a medida que llega; mirarlos dejaba en rojo salidas bien abiertas, como las de Aeropuerto en CGL.)
//       · señal sin tren (Open Rails la ha soltado —en multijugador las reservas se roban y se sueltan solas— y se quedaba
//         con un «libre» viejo: con «Prefer green» su script la veía preparada y libre y daba verde con un tren en el
//         desvío): como hace Open Rails con las señales sin ruta, por los desvíos como están: tren → ocupado; desvío en
//         contra → obstruido. Así es el SCRIPT DE LA RUTA el que pone la parada (con su aspecto, su texto y su
//     dibujo, también los scripts en C# como los de CGL), y la señal de detrás, al leer la siguiente, pone el anuncio
//     de parada por su cuenta. (Antes se cambiaba el aspecto después del script, y los scripts en C#, que leen el
//     aspecto en texto de la siguiente, no se enteraban.)
//     Y para los scripts clásicos que no miran el estado del cantón, de respaldo, la parada después del script.
//  1b. ITINERARIOS INCOMPATIBLES (enclavamiento): una señal abierta para un tren solo se queda abierta si ninguna otra
//     señal pedida antes por OTRO tren tiene un itinerario que comparta algún tramo con el suyo (el desvío de
//     convergencia y la vía única de una salida, un cruce, la vía única en sentido contrario…). Por orden de llegada: la
//     primera que se pidió manda; las demás, en parada (cantón «reservado por otro», que su script ve obstruido) hasta que
//     ese itinerario se libera. Las vías paralelas que no comparten nada, a la vez. (En multijugador Open Rails no lo
//     impide: las reservas se roban, y en Aeropuerto, con tres vías hacia la vía única, se abrían varias salidas.)
//     Sin interbloqueos: una señal en parada no le quita el turno al tren por el que espera (directa o indirectamente):
//     si la salida de Aeropuerto espera a que pase el tren que entra por la vía única, la entrada de ese tren no espera
//     a la salida. Y las que Open Rails ya tiene en parada por su cuenta guardan su turno, pero no estorban a nadie.
//  2. AL MOMENTO: el despachador manda el estado de señales y desvíos cada 2 s en vez de cada 10.
//  3. MAP WINDOW (Ctrl+9): todos los jugadores son ayudantes del despachador (su aiderList), para que valgan sus
//     órdenes de señal desde el mapa (si el despachador se lo quita a alguien, no se le vuelve a dar). Los demás
//     arreglos del mapa, en MapWindow.cs.
// Las señales con una orden manual (desde el mapa, o retenidas por una parada) no se tocan.
// Registro en %AppData%\Open Rails\SelectOR\senales.log. Ante errores repetidos se apaga solo (Open Rails sigue).

using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using Orts.Formats.Msts;
using Orts.MultiPlayer;
using Orts.Simulation.Physics;
using Orts.Simulation.Signalling;

namespace SelectOR.SignalGuard
{
    public static class Guard
    {
        const int UpdateIntervalS = 2;
        static bool _off, _intervalSet;
        static int _errors;
        static readonly HashSet<int> _forced = new HashSet<int>();
        static readonly HashSet<long> _seenPairs = new HashSet<long>();
        static DateTime _aidersAt = DateTime.MinValue;
        // 1b. Señales que quieren estar abiertas para un tren: su itinerario (tramos), desde cuándo y si se les tiene en parada.
        // (WaitsFor/WaitsForItin: el tren o el itinerario por el que está en parada; Passive: en parada por Open Rails)
        sealed class Itin { public int Sig; public Train Train; public HashSet<int> Sections; public DateTime Since, Seen; public bool Held, Passive; public Train WaitsFor; public Itin WaitsForItin; }
        static readonly Dictionary<int, Itin> _itins = new Dictionary<int, Itin>();
        static readonly TimeSpan ItinTtl = TimeSpan.FromSeconds(5);
        static readonly Dictionary<string, DateTime> _logged = new Dictionary<string, DateTime>();
        // (una línea por señal y motivo como mucho cada minuto: Open Rails la recalcula sin parar)
        static void LogOnce(string key, string line)
        {
            if (_logged.TryGetValue(key, out var at) && (DateTime.UtcNow - at).TotalSeconds < 60) return;
            if (_logged.Count > 2000) _logged.Clear();
            _logged[key] = DateTime.UtcNow;
            Log(line);
        }
        static readonly HashSet<string> _aidersAdded = new HashSet<string>();
        static readonly AccessTools.FieldRef<SignalObject, SignalObject.InternalBlockstate> BlockStateOf =
            AccessTools.FieldRefAccess<SignalObject, SignalObject.InternalBlockstate>("internalBlockState");

        public static void Install()
        {
            var target = AccessTools.Method(typeof(SignalObject), nameof(SignalObject.StateUpdate));
            if (target == null) { Log("Open Rails no tiene SignalObject.StateUpdate: no se instala"); return; }
            var h = new Harmony("selector.signalguard");
            h.Patch(target, prefix: new HarmonyMethod(typeof(Guard), nameof(BeforeStateUpdate)), postfix: new HarmonyMethod(typeof(Guard), nameof(AfterStateUpdate)));
            Log($"instalado en {ProcessPath()} · Open Rails {typeof(SignalObject).Assembly.GetName().Version} · {(Type.GetType("System.Runtime.Loader.AssemblyLoadContext") != null ? ".NET" : ".NET Framework")}");
            try { MapWindow.Install(h); } catch (Exception ex) { Log("Map Window: " + ex.Message); }
        }

        static string ProcessPath()
        {
            try { return System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "?"; } catch { return "?"; }
        }

        // Nunca deja escapar una excepción (iría al bucle de Open Rails).
        static void Safe(Action a)
        {
            if (_off) return;
            try { a(); }
            catch (Exception ex)
            {
                _errors++;
                if (_errors <= 3) Log("error: " + ex.GetType().Name + " " + ex.Message);
                if (_errors >= 25) { _off = true; Log("demasiados errores: el guardián se apaga (Open Rails sigue como siempre)"); }
            }
        }

        static bool Dispatcher() => MPManager.IsMultiPlayer() && MPManager.IsServer();   // solo él calcula señales

        static void BeforeStateUpdate(SignalObject __instance) => Safe(() => Before(__instance));
        static void AfterStateUpdate(SignalObject __instance) => Safe(() => After(__instance));

        // 1. Antes del script: el estado del cantón, de verdad.
        static void Before(SignalObject sig)
        {
            if (!Dispatcher()) return;
            Housekeeping();
            if (sig == null) return;
            // ya no la pide ningún tren (lo ha rebasado, o se ha soltado): fuera de la cola de itinerarios
            if (sig.enabledTrain?.Train == null) _itins.Remove(sig.thisRef);
            if (sig.SignalHeads == null || sig.holdState != HoldState.None || !sig.isSignalNormal()) return;
            ref var bs = ref BlockStateOf(sig);
            if (bs != SignalObject.InternalBlockstate.Reserved && bs != SignalObject.InternalBlockstate.Reservable)
            {
                // en parada por Open Rails (ocupada, reservada por otro…): guarda su turno, sin estorbar a nadie
                if (sig.enabledTrain?.Train != null) { var w = Register(sig); w.Held = true; w.Passive = true; w.WaitsFor = null; w.WaitsForItin = null; }
                return;
            }
            bool routed = sig.enabledTrain?.Train != null;
            bool against = false;
            var me = routed ? Register(sig) : null;   // (en la cola desde que la pide: así guarda su turno)
            var other = routed ? TrainOnRoute(sig) : TrainInBlock(sig, out _, out against);
            string forWho = routed ? $"abierta para «{Name(sig.enabledTrain.Train)}»" : "sin tren que la pida";
            if (other != null)
            {
                bs = SignalObject.InternalBlockstate.OccupiedSameDirection;   // (Open Rails lo vuelve a calcular en su siguiente repaso)
                if (me != null) { me.Held = true; me.WaitsFor = other; me.WaitsForItin = null; }
                LogOnce(sig.thisRef + " o", $"señal {sig.thisRef} ({forWho}): su cantón lo ocupa «{Name(other)}»: se le dice a su script");
                return;
            }
            if (against)
            {
                bs = SignalObject.InternalBlockstate.Blocked;   // desvío en contra: obstruido (parada)
                LogOnce(sig.thisRef + " a", $"señal {sig.thisRef} ({forWho}): un desvío de su cantón está en contra: se le dice a su script");
                return;
            }
            if (me == null) return;
            var first = Conflict(me);
            if (first != null)
            {
                bs = SignalObject.InternalBlockstate.ReservedOther;   // itinerario incompatible: su script lo ve obstruido (parada)
                me.Held = true; me.WaitsFor = first.Train; me.WaitsForItin = first;
                LogOnce(sig.thisRef + " i", $"señal {sig.thisRef} ({forWho}): itinerario incompatible con el de la señal {first.Sig} (pedida antes, para «{Name(first.Train)}»): en parada hasta que se libere");
                return;
            }
            me.Held = false; me.WaitsFor = null; me.WaitsForItin = null;
        }

        // En la cola de itinerarios: sus tramos (la ruta que Open Rails le reservó) y desde cuándo la pide su tren.
        static Itin Register(SignalObject sig)
        {
            var now = DateTime.UtcNow;
            var train = sig.enabledTrain.Train;
            if (!_itins.TryGetValue(sig.thisRef, out var it) || it.Train != train || now - it.Seen > ItinTtl)
                _itins[sig.thisRef] = it = new Itin { Sig = sig.thisRef, Train = train, Since = now, Held = true };
            it.Seen = now; it.Passive = false;
            var secs = new HashSet<int>();
            if (sig.signalRoute != null) foreach (var el in sig.signalRoute) if (el != null && el.TCSectionIndex >= 0) secs.Add(el.TCSectionIndex);
            it.Sections = secs;
            return it;
        }

        // La primera (por orden de llegada) de las que comparten algún tramo con esta y son de otro tren.
        static Itin Conflict(Itin me)
        {
            if (me.Sections == null || me.Sections.Count == 0) return null;
            var now = DateTime.UtcNow;
            Itin best = null;
            foreach (var e in _itins.Values)
            {
                if (e == me || e.Train == me.Train || now - e.Seen > ItinTtl || e.Sections == null) continue;
                if (e.Held && (e.Passive || WaitsOn(e, me.Train))) continue;   // (no estorba: o no cuenta, o me espera a mí)
                bool before = e.Since < me.Since || (e.Since == me.Since && e.Sig < me.Sig);
                if (!before || !e.Sections.Overlaps(me.Sections)) continue;
                if (best == null || e.Since < best.Since) best = e;
            }
            return best;
        }

        // ¿Está en parada esperando (directa o indirectamente) a este tren?
        static bool WaitsOn(Itin e, Train t)
        {
            for (int n = 0; e != null && e.Held && n < 16; n++, e = e.WaitsForItin)
                if (e.WaitsFor == t) return true;
            return false;
        }

        // Después del script: respaldo para los scripts clásicos que no miran el cantón, y diagnóstico.
        static void After(SignalObject sig)
        {
            if (!Dispatcher() || sig == null || sig.SignalHeads == null || sig.holdState != HoldState.None || !sig.isSignalNormal()) return;
            bool open = false, cs = false;
            foreach (var h in sig.SignalHeads)
                if (h.Function == SignalFunction.NORMAL)
                {
                    if (h.usedCsSignalScript != null) cs = true;
                    if (h.state > MstsSignalAspect.RESTRICTING) open = true;
                }
            if (!open) { _forced.Remove(sig.thisRef); return; }
            var other = TrainInBlock(sig, out var next, out _);
            bool held = _itins.TryGetValue(sig.thisRef, out var mine) && mine.Held && DateTime.UtcNow - mine.Seen < ItinTtl;
            if (held && !cs)   // (respaldo para los scripts clásicos que no miran el cantón: itinerario incompatible u ocupado)
            {
                foreach (var h in sig.SignalHeads)
                    if (h.Function == SignalFunction.NORMAL && h.usedCsSignalScript == null)
                    {
                        h.state = MstsSignalAspect.STOP;
                        h.draw_state = h.def_draw_state(MstsSignalAspect.STOP);
                    }
                return;
            }
            if (other != null && !cs)
            {
                foreach (var h in sig.SignalHeads)
                    if (h.Function == SignalFunction.NORMAL && h.usedCsSignalScript == null)
                    {
                        h.state = MstsSignalAspect.STOP;
                        h.draw_state = h.def_draw_state(MstsSignalAspect.STOP);
                    }
                if (_forced.Add(sig.thisRef) && _forced.Count <= 400)
                    Log($"señal {sig.thisRef}: su script no mira el cantón; en parada, lo ocupa «{Name(other)}»");
                return;
            }
            // diagnóstico (sin tocar nada): abierta para un tren, con la siguiente en parada y sin anunciarlo
            if (other == null && next != null && sig.enabledTrain != null && sig.this_sig_lr(SignalFunction.NORMAL) >= MstsSignalAspect.CLEAR_1
                && next.isSignalNormal() && next.this_sig_lr(SignalFunction.NORMAL) <= MstsSignalAspect.RESTRICTING
                && _seenPairs.Add(((long)sig.thisRef << 32) | (uint)next.thisRef) && _seenPairs.Count <= 300)
                Log($"aviso: señal {sig.thisRef} abierta ({sig.this_sig_lr(SignalFunction.NORMAL)} «{Text(sig)}») para «{Name(sig.enabledTrain.Train)}» con la siguiente {next.thisRef} en {next.this_sig_lr(SignalFunction.NORMAL)} «{Text(next)}» (cantón {BlockStateOf(sig)})");
        }

        // Cada poco: el intervalo de envío y los ayudantes.
        static void Housekeeping()
        {
            if (!_intervalSet)
            {
                var mp = MPManager.Instance();
                if (mp != null)
                {
                    if (mp.MPUpdateInterval > UpdateIntervalS) { Log($"estado de señales y desvíos a los demás cada {UpdateIntervalS} s (antes {mp.MPUpdateInterval} s)"); mp.MPUpdateInterval = UpdateIntervalS; }
                    _intervalSet = true;
                }
            }
            if ((DateTime.UtcNow - _aidersAt).TotalSeconds >= 2)
            {
                _aidersAt = DateTime.UtcNow; SyncAiders();
                var stale = new List<int>(); foreach (var e in _itins.Values) if (DateTime.UtcNow - e.Seen > ItinTtl) stale.Add(e.Sig);
                foreach (var k in stale) _itins.Remove(k);
            }
        }

        // 3. Map Window: todos los jugadores, ayudantes del despachador (sus órdenes de señal valen). Una vez a cada uno:
        // si el despachador se lo quita desde el mapa, se respeta.
        static void SyncAiders()
        {
            var mp = MPManager.Instance(); var players = MPManager.OnlineTrains?.Players;
            if (mp?.aiderList == null || players == null) return;
            string me = MPManager.GetUserName();
            foreach (var name in new List<string>(players.Keys))
            {
                if (string.IsNullOrEmpty(name) || name == me || !_aidersAdded.Add(name)) continue;
                if (!mp.aiderList.Contains(name)) mp.aiderList.Add(name);
                Log($"«{name}»: ayudante del despachador (sus órdenes de señal desde el mapa valen)");
            }
        }

        // El tren (otro que aquel para el que se abrió la señal) que hay en su cantón, y la señal del final del cantón:
        // desde la señal hasta la siguiente, siguiendo los desvíos como están puestos (lo mismo que hace Open Rails con
        // las señales sin ruta).
        // Señal abierta para un tren: otro tren en la ruta que Open Rails le reservó (sus tramos, hasta la siguiente señal).
        // Sin ruta guardada, por los desvíos como están (sin dar nada por obstruido).
        static Train TrainOnRoute(SignalObject sig)
        {
            var list = MPManager.Simulator?.Signals?.TrackCircuitList;
            var route = sig.signalRoute;
            if (list == null) return null;
            if (route == null || route.Count == 0) return TrainInBlock(sig, out _, out _);
            Train mine = sig.enabledTrain?.Train;
            foreach (var el in route)
            {
                if (el == null || el.TCSectionIndex < 0 || el.TCSectionIndex >= list.Count) continue;
                var s = list[el.TCSectionIndex];
                if (s?.CircuitState == null || !s.CircuitState.HasTrainsOccupying()) continue;
                foreach (var tr in s.CircuitState.TrainsOccupying())
                    if (tr?.Train != null && tr.Train != mine) return tr.Train;
            }
            return null;
        }

        // against = el camino se corta antes de llegar a otra señal o al final de la vía (un desvío en contra).
        static Train TrainInBlock(SignalObject sig, out SignalObject next, out bool against)
        {
            next = null; against = false;
            var list = MPManager.Simulator?.Signals?.TrackCircuitList;
            if (list == null) return null;
            Train mine = sig.enabledTrain?.Train;
            int sect = sig.TCReference, dir = sig.TCDirection;
            bool own = true;   // la sección de la propia señal: solo lo que queda por delante de ella
            if (sig.TCNextTC > 0) { sect = sig.TCNextTC; dir = sig.TCNextDirection; own = false; }
            for (int n = 0; sect > 0 && sect < list.Count && n < 300; n++)
            {
                var s = list[sect];
                if (s.CircuitState != null && s.CircuitState.HasTrainsOccupying())
                {
                    if (own && s.Index == sig.TCReference)
                    {
                        foreach (var t in s.TestTrainAhead(null, sig.TCOffset, sig.TCDirection).Keys)
                            if (t != null && t != mine) return t;
                    }
                    else
                        foreach (var tr in s.CircuitState.TrainsOccupying())
                            if (tr?.Train != null && tr.Train != mine) return tr.Train;
                }
                own = false;
                if (s.EndSignals[dir] != null) { next = s.EndSignals[dir]; return null; }
                if (s.CircuitType == TrackCircuitSection.TrackCircuitType.EndOfTrack) return null;
                var pin = s.ActivePins[dir, 0];
                if (pin == null || pin.Link == -1) pin = s.ActivePins[dir, 1];
                if (pin == null || pin.Link < 0) { against = true; return null; }
                sect = pin.Link; dir = pin.Direction;
            }
            return null;
        }

        static string Text(SignalObject s)
        {
            foreach (var h in s.SignalHeads) if (h.Function == SignalFunction.NORMAL && !string.IsNullOrEmpty(h.TextSignalAspect)) return h.TextSignalAspect;
            return "";
        }

        static string Name(Train t)
        {
            try { return t == null ? "?" : string.IsNullOrEmpty(t.Name) ? "#" + t.Number : t.Name; } catch { return "?"; }
        }

        static readonly object _logLock = new object();
        public static void Log(string line)
        {
            try
            {
                string dir = Environment.GetEnvironmentVariable("SELECTOR_CACHE_DIR") is { Length: > 0 } t ? t   // (las pruebas, en su carpeta)
                           : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Open Rails", "SelectOR");
                Directory.CreateDirectory(dir);
                lock (_logLock) File.AppendAllText(Path.Combine(dir, "senales.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
            }
            catch { }
        }
    }
}
