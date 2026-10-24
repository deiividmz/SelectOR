// Sala compartida (SelectOR la lanza con SELECTOR_SPAWN_ONLY=1): las agujas son las de la sala (1.2.76).
//  · En multijugador, el Open Rails de cada jugador colocaba por su cuenta las agujas de la ruta que reserva para su tren
//    (alignSwitchPins → Signals.setSwitch) y, mientras las tenía reservadas, no hacía caso de lo que mandaba la sala. Como
//    la sala solo manda una aguja cuando cambia, la diferencia se quedaba: el mapa grande (la sala) decía una cosa y tu 3D
//    otra. Open Rails ya trae la manera de evitarlo (MPManager.NoAutoSwitch, «sin cambio de agujas a mano»): aquí vale
//    «sí» en la sala, una vez puesto el tren (las de debajo del tren las pone el .pat al salir: Spawn.cs).
//  · La tecla G / Mayús+G (y la de detrás) en multijugador solo cambiaba la aguja en tu Open Rails y la sala no se
//    enteraba. Ahora se pide a la sala, como un clic en el mapa grande o Ctrl+9 (con sus enclavamientos e itinerarios).
// Con Harmony, en memoria (no cambia ningún archivo de Open Rails). Se busca por nombre (las versiones difieren).

using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Orts.MultiPlayer;
using Orts.Simulation.Physics;
using Orts.Simulation.Signalling;

namespace SelectOR.MapAgent
{
    public static class AgujasSala
    {
        static volatile bool _armed;        // ya está puesto el tren: desde aquí, las agujas solo las cambia la sala
        [ThreadStatic] static int _gKey;    // dentro de la tecla G (ProcessRequest…SetSwitch del tren)
        static long _lastAskTicks; static int _lastAskNode = -1, _lastAskPos = -1;

        // (desde Agent.Install) cuántos parches ha puesto.
        public static int Install(Harmony h)
        {
            if (Environment.GetEnvironmentVariable("SELECTOR_SPAWN_ONLY") != "1") return 0;
            int n = 0;
            try
            {
                var noAuto = AccessTools.Method(typeof(MPManager), "NoAutoSwitch");
                if (noAuto == null) { Agent.Log("agujas de la sala: no se encuentra NoAutoSwitch; tu Open Rails sigue colocándolas"); return 0; }
                h.Patch(noAuto, prefix: new HarmonyMethod(typeof(AgujasSala), nameof(BeforeNoAuto))); n++;
                var set = AccessTools.Method(typeof(Signals), "setSwitch");
                if (set != null) { h.Patch(set, prefix: new HarmonyMethod(typeof(AgujasSala), nameof(BeforeSetSwitch))); n++; }
                // tecla G: las que van por sentido (no la del número de aguja, que es la de Ctrl+9 y llega por la sala)
                foreach (var m in typeof(Train).GetMethods(BindingFlags.Public | BindingFlags.Instance)
                             .Where(m => (m.Name == "ProcessRequestExplorerSetSwitch" || m.Name == "ProcessRequestManualSetSwitch")
                                         && m.GetParameters().Length >= 1 && m.GetParameters()[0].ParameterType.Name == "Direction"))
                { h.Patch(m, prefix: new HarmonyMethod(typeof(AgujasSala), nameof(BeforeG)), finalizer: new HarmonyMethod(typeof(AgujasSala), nameof(AfterG))); n++; }
                // armar: al acabar de poner el tren por su .pat, o con la primera foto de agujas de la sala
                var preset = AccessTools.Method(typeof(Train), "PresetExplorerPath");
                if (preset != null) h.Patch(preset, finalizer: new HarmonyMethod(typeof(AgujasSala), nameof(Arm)));
                var states = AccessTools.Method(typeof(MSGSwitchStatus), "HandleMsg");
                if (states != null) h.Patch(states, prefix: new HarmonyMethod(typeof(AgujasSala), nameof(ArmQuiet)));
                Agent.Log("agujas de la sala: tu Open Rails no las coloca por su cuenta (manda la sala) · la tecla G se pide a la sala");
            }
            catch (Exception ex) { Agent.Log("agujas de la sala: " + ex.GetType().Name + " " + ex.Message); }
            return n;
        }

        static Exception Arm(Exception __exception)
        {
            if (!_armed) { _armed = true; Agent.Log("agujas de la sala: tren puesto; desde ahora las agujas son las de la sala"); }
            return __exception;
        }
        static void ArmQuiet() { if (!_armed) { _armed = true; Agent.Log("agujas de la sala: primera foto de agujas de la sala; desde ahora mandan ellas"); } }

        static bool Active => _armed && MPManager.IsMultiPlayer() && !MPManager.IsServer();

        static bool BeforeNoAuto(ref bool __result)
        {
            try { if (Active) { __result = true; return false; } } catch { }
            return true;
        }

        static void BeforeG() => _gKey++;
        static Exception AfterG(Exception __exception) { _gKey = Math.Max(0, _gKey - 1); return __exception; }

        // Una aguja que tu Open Rails quiere cambiar: con la tecla G, se pide a la sala; si no (su ruta), nada (NoAutoSwitch)
        static bool BeforeSetSwitch(Signals __instance, int nodeIndex, int switchPos)
        {
            try
            {
                if (_gKey <= 0 || !Active) return true;
                var tn = MPManager.Simulator.TDB.TrackDB.TrackNodes[nodeIndex];
                // (Open Rails llama más de una vez en la misma pulsación: una sola petición)
                long now = DateTime.UtcNow.Ticks;
                if (nodeIndex == _lastAskNode && switchPos == _lastAskPos && now - _lastAskTicks < TimeSpan.TicksPerSecond) return false;
                _lastAskNode = nodeIndex; _lastAskPos = switchPos; _lastAskTicks = now;
                string m = new MSGSwitch(MPManager.GetUserName(), tn.UiD.WorldTileX, tn.UiD.WorldTileZ, tn.UiD.WorldId, switchPos, true).ToString();
                if (m == null) { Agent.Log($"tecla G: aguja {nodeIndex} → {switchPos}: Open Rails no deja pedirla (sin permiso de ayudante)"); return false; }
                MPManager.Notify(m);
                Agent.Log($"tecla G: aguja {nodeIndex} → {switchPos}, pedida a la sala");
                return false;   // la pone la sala (te llega con su foto de agujas)
            }
            catch (Exception ex) { Agent.Log("tecla G: " + ex.GetType().Name + " " + ex.Message); return true; }
        }
    }
}
