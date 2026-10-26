// Agente del mapa (multijugador): una pieza mínima dentro del Open Rails de cada jugador de una sala (la carga SelectOR
// al lanzar una partida en red), que solo trabaja en el DESPACHADOR, para que el mapa grande de SelectOR haga lo mismo
// que su ventana Ctrl+9:
//  · las órdenes de señal de su propio SelectOR: Open Rails solo acepta órdenes de señal de sus ayudantes, y las del
//    despachador por red las tiraba; el despachador entra en su propia lista de ayudantes (aiderList);
//  · los nombramientos de ayudante desde el mapa grande: el servidor de la sala le pasa «AIDER nombre T/F» solo si lo
//    pide el SelectOR del despachador (los de los demás los tira); aquí se apunta en la lista y se dice a todos, como
//    hace Ctrl+9 («Make this player an assistant»). Sin el agente, Open Rails los ignoraba.
//  · la foto de las señales enseguida: Open Rails solo la manda cada 10 s (MPUpdateInterval) y el mapa grande tardaba
//    en ver un cambio de señal; tras una orden de señal que le llega por red, se adelanta ese mismo envío (a ~1 s y a
//    ~3 s, lo que tarda Open Rails en recalcular el aspecto). Es la misma foto de siempre, solo antes;
//  · las agujas enclavadas (Open Rails no las dice por la red): cada segundo mira, en el orden de SWITCHSTATES, qué
//    agujas tienen una señal abierta que las reserva (S), un tren que las reserva (T) o un tren encima (O), y si algo
//    cambia se lo manda al servidor de la sala («SELECTORBLOCKED códigos», que el servidor se queda para los SelectOR).
//    Solo después de que el servidor se lo pida («TEXT SelectOR … @selector:blocked»): un servidor que no lo conoce se
//    lo pasaría a los Open Rails de la sala.
// En Conducción libre (sin red) hace además de Ctrl+9 para el mapa grande de SelectOR: LocalLink.cs.
// En la sala compartida, el .pat solo para la salida: Spawn.cs.
// No cambia cómo calcula Open Rails las señales ni las agujas (solo lee). Si algo falla, Open Rails sigue como siempre.
// Registro: %AppData%\Open Rails\SelectOR\agente-mapa.log (o SELECTOR_CACHE_DIR).

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using HarmonyLib;
using Orts.Formats.Msts;
using Orts.MultiPlayer;

namespace SelectOR.MapAgent
{
    public static class Agent
    {
        static int _errors;
        static readonly Stopwatch _clock = Stopwatch.StartNew();
        static long _sigDue1 = -1, _sigDue2 = -1;   // (ms del reloj) cuándo adelantar la foto de señales; -1 = nada
        static volatile bool _blockedOn;           // el servidor ha pedido las agujas enclavadas
        static long _blockedNext, _blockedResend;
        static string _blockedLast;
        static TrackNode[] _swOrder, _swOf;        // agujas en el orden de SWITCHSTATES (y de qué TDB)

        public static void Install()
        {
            Errores.Install();   // por qué se cierra Open Rails, aunque tenga su registro apagado (Errores.cs)
            try { Tcs.Install(new Harmony("selector.mapagent.tcs")); } catch (Exception ex) { Log("scripts de seguridad: " + ex.Message); }   // ATO/LZB más nuevos que este Open Rails (Tcs.cs)
            var h = new Harmony("selector.mapagent");
            int n = 0;
            var upd = AccessTools.Method(typeof(MPManager), "Update");
            if (upd != null) { h.Patch(upd, postfix: new HarmonyMethod(typeof(Agent), nameof(AfterUpdate))); n++; }
            var aider = AccessTools.Method(typeof(MSGAider), "HandleMsg");
            if (aider != null) { h.Patch(aider, prefix: new HarmonyMethod(typeof(Agent), nameof(BeforeAider))); n++; }
            var sig = AccessTools.Method(typeof(MSGSignalChange), "HandleMsg");
            if (sig != null) { h.Patch(sig, postfix: new HarmonyMethod(typeof(Agent), nameof(AfterSignalChange))); n++; }
            var text = AccessTools.Method(typeof(MSGText), "HandleMsg");
            if (text != null) { h.Patch(text, prefix: new HarmonyMethod(typeof(Agent), nameof(BeforeText))); n++; }
            int local = LocalLink.Install(h);   // conducción libre: el mapa grande de SelectOR (LocalLink.cs)
            int spawn = Spawn.Install(h);       // sala compartida: el .pat solo para la salida (Spawn.cs)
            int roomSw = AgujasSala.Install(h); // sala compartida: las agujas, solo las de la sala; la tecla G, a la sala (AgujasSala.cs)
            int remote = Remotos.Install(h);    // los trenes de los demás: vehículos que faltan y por qué no se ve uno (Remotos.cs)
            int couples = Acoples.Install(h);   // acoples y desacoples de los demás: registro y trenes por sus coches (Acoples.cs)
            int release = Liberar.Install(h);   // Ctrl+Tab en multijugador: liberar la señal de tu tren (Liberar.cs)
            int sun = Sol.Install(h);            // el sol de las rutas CGL a la hora de España (Sol.cs)
            int threads = Hilos.Install(h);      // .NET 6: que un corte de la sala no cierre Open Rails (Hilos.cs)
            int keys = Teclado.Install(h);       // escribir en el chat del HUD sin que Open Rails lo reciba (Teclado.cs)
            int esc = Pausa.Install(h);          // Esc en multijugador: menú con «Salir» y «Continuar» (Pausa.cs)
            int events = SelectOR.Comun.Eventos.Install(h, Log);   // bocina, puertas, luces… de otras versiones de Open Rails (Eventos.cs)
            int clock = SelectOR.Comun.Hora.Install(h, Log);       // la hora de la sala, también a los que ya están dentro (Hora.cs)
            Log($"instalado ({n} de 4{(local > 0 ? " + conducción libre" : "")}{(spawn > 0 ? " + salida de la sala" : "")}{(roomSw > 0 ? " + agujas de la sala" : "")}{(remote > 0 ? " + trenes de los demás" : "")}{(couples > 0 ? " + acoples" : "")}{(release > 0 ? " + Ctrl+Tab" : "")}{(sun > 0 ? " + sol" : "")}{(threads > 0 ? " + hilos de red" : "")}{(keys > 0 ? " + teclado" : "")}{(esc > 0 ? " + Esc" : "")}{(events > 0 ? " + eventos de otras versiones" : "")}{(clock > 0 ? " + hora de la sala" : "")}) en {ProcessPath()} · Open Rails {typeof(MPManager).Assembly.GetName().Version}");
            if (sun == 0 || keys == 0 || esc == 0) Later(h, sun == 0, keys == 0, esc == 0);
        }

        // En los Open Rails de .NET Framework el agente entra antes de que se cargue RunActivity.exe, que es donde están el
        // teclado (UserInput) y el sol (SunMoonPos): no se encontraban y no se ponían nunca (1.2.69). Se ponen en cuanto
        // Open Rails carga ese ensamblado.
        static void Later(Harmony h, bool sun, bool keys, bool esc)
        {
            AssemblyLoadEventHandler onLoad = null;
            bool busy = false;
            void Try(System.Reflection.Assembly a)
            {
                if (busy) return;
                busy = true;
                try
                {
                    string got = "";
                    bool Has(string type) => (a == null ? AccessTools.TypeByName(type) : a.GetType(type, false)) != null;
                    if (sun && Has("Orts.Viewer3D.Common.SunMoonPos")) { sun = false; if (Sol.Install(h) > 0) got += " + sol"; }
                    if (keys && Has("Orts.Viewer3D.UserInput")) { keys = false; if (Teclado.Install(h) > 0) got += " + teclado"; }
                    if (esc && Has("Orts.Viewer3D.Popups.QuitWindow")) { esc = false; if (Pausa.Install(h) > 0) got += " + Esc"; }
                    if (got != "") Log("instalado al cargar el 3D:" + got);
                    if (!sun && !keys && !esc) AppDomain.CurrentDomain.AssemblyLoad -= onLoad;
                }
                catch (Exception ex) { Log("al cargar el 3D: " + ex.GetType().Name + " " + ex.Message); }
                finally { busy = false; }
            }
            onLoad = (s, e) => Try(e.LoadedAssembly);
            AppDomain.CurrentDomain.AssemblyLoad += onLoad;
            Try(null);   // (por si ya estaba)
        }

        static string ProcessPath()
        {
            try { return System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "?"; } catch { return "?"; }
        }

        // Cada vuelta del multijugador: el despachador, en su propia lista de ayudantes (así acepta sus órdenes de señal).
        static void AfterUpdate()
        {
            if (_errors > 20) return;
            try
            {
                if (!MPManager.IsMultiPlayer() || !MPManager.IsServer()) return;
                var mp = MPManager.Instance(); string me = MPManager.GetUserName();
                if (mp?.aiderList != null && !string.IsNullOrEmpty(me) && !mp.aiderList.Contains(me))
                {
                    mp.aiderList.Add(me);
                    Log($"«{me}» es el despachador: acepta sus órdenes de señal desde el mapa grande de SelectOR");
                }
                long now = _clock.ElapsedMilliseconds;
                if (mp != null) SignalsSoon(mp, now);
                if (_blockedOn && now >= _blockedNext) { _blockedNext = now + 1000; SendBlocked(now); }
            }
            catch (Exception ex) { Fail(ex); }
        }

        // ---- foto de las señales enseguida ----

        // Orden de señal por red (hilo de la red): en el despachador, adelantar la foto de señales.
        static void AfterSignalChange()
        {
            try
            {
                if (_errors > 20 || !MPManager.IsServer()) return;
                long now = _clock.ElapsedMilliseconds;
                Interlocked.Exchange(ref _sigDue1, now + 800);
                Interlocked.Exchange(ref _sigDue2, now + 3000);
            }
            catch (Exception ex) { Fail(ex); }
        }

        // (hilo del juego) Llegada la hora, Open Rails manda en su siguiente vuelta la foto de agujas y señales que
        // mandaría a los 10 s (solo si ha cambiado algo: MSGSignalStatus.OKtoSend).
        static void SignalsSoon(MPManager mp, long now)
        {
            long d1 = Interlocked.Read(ref _sigDue1), d2 = Interlocked.Read(ref _sigDue2);
            bool due = false;
            if (d1 >= 0 && now >= d1) { Interlocked.CompareExchange(ref _sigDue1, -1, d1); due = true; }
            if (d2 >= 0 && now >= d2) { Interlocked.CompareExchange(ref _sigDue2, -1, d2); due = true; }
            if (due) mp.lastSwitchTime = -1e9;
        }

        // ---- agujas enclavadas ----

        // «TEXT SelectOR yo @selector:…» del servidor de la sala: es para el agente (no se enseña en Open Rails).
        static bool BeforeText(string ___sender, string ___msgx)
        {
            try
            {
                if (___sender != "SelectOR" || ___msgx == null || !___msgx.StartsWith("@selector:", StringComparison.Ordinal)) return true;
                if (___msgx.StartsWith("@selector:blocked", StringComparison.Ordinal) && !_blockedOn)
                {
                    _blockedLast = null; _blockedNext = 0; _blockedOn = true;
                    Log("el servidor de la sala pide las agujas enclavadas: se le mandan al cambiar");
                }
                return false;
            }
            catch (Exception ex) { Fail(ex); return true; }
        }

        static void SendBlocked(long now)
        {
            string codes = BlockedCodes();
            if (codes == null) return;
            if (codes == _blockedLast && now < _blockedResend) return;
            bool first = _blockedLast == null;
            _blockedLast = codes; _blockedResend = now + 30000;
            string tmp = "SELECTORBLOCKED " + codes;
            MPManager.BroadCast(" " + tmp.Length + ": " + tmp);
            if (first) Log($"agujas enclavadas: {codes.Count(c => c != '.')} de {codes.Length} (se mandan al cambiar)");
        }

        // Una letra por aguja, en el orden de SWITCHSTATES (nodos con TrJunctionNode, por Index): lo mismo que mira Open
        // Rails para no dejar cambiarla (Signals.RequestSetSwitch).
        static string BlockedCodes()
        {
            var sim = MPManager.Simulator;
            var nodes = sim?.TDB?.TrackDB?.TrackNodes;
            var tcl = sim?.Signals?.TrackCircuitList;
            if (nodes == null || tcl == null) return null;
            if (_swOf != nodes)
            {
                _swOrder = nodes.Where(t => t != null && t.TrJunctionNode != null).OrderBy(t => t.Index).ToArray();
                _swOf = nodes;
            }
            var sb = new StringBuilder(_swOrder.Length);
            foreach (var t in _swOrder)
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
                            else if (st.TrainReserved != null || (st.TrainClaimed != null && st.TrainClaimed.Count > 0)) c = 'T';
                        }
                    }
                }
                catch { }
                sb.Append(c);
            }
            return sb.ToString();
        }

        // «AIDER nombre T/F» en el despachador (Open Rails lo ignora ahí): nombrar o quitar un ayudante, como Ctrl+9.
        static bool BeforeAider(MSGAider __instance)
        {
            if (_errors > 20) return true;
            try
            {
                if (!MPManager.IsServer()) return true;   // en un jugador: lo de siempre («ahora eres ayudante»)
                string user = __instance.user; bool add = __instance.add;
                var mp = MPManager.Instance();
                if (string.IsNullOrEmpty(user) || mp?.aiderList == null || user == MPManager.GetUserName()) return false;
                if (add && !mp.aiderList.Contains(user)) mp.aiderList.Add(user);
                if (!add) mp.aiderList.Remove(user);
                try { MPManager.BroadCast(new MSGAider(user, add).ToString()); } catch (Exception ex) { Fail(ex); }   // a todos (el nombrado lo ve en su Open Rails)
                Log($"«{user}»: {(add ? "ayudante" : "ya no es ayudante")} (desde el mapa grande de SelectOR)");
                return false;
            }
            catch (Exception ex) { Fail(ex); return true; }
        }

        static void Fail(Exception ex)
        {
            _errors++;
            if (_errors <= 3) Log("error: " + ex.GetType().Name + " " + ex.Message);
            if (_errors == 21) Log("demasiados errores: el agente se apaga (Open Rails sigue como siempre)");
        }

        public static void Log(string line)
        {
            try
            {
                string dir = Environment.GetEnvironmentVariable("SELECTOR_CACHE_DIR");
                if (string.IsNullOrWhiteSpace(dir)) dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Open Rails", "SelectOR");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "agente-mapa.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
            }
            catch { }
        }
    }
}
