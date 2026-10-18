// Sala compartida de CGL (SelectOR la lanza con SELECTOR_SPAWN_ONLY=1): el .pat solo para la salida.
// Open Rails, al empezar una Conducción libre, coloca TODAS las agujas del recorrido del .pat (Train.PresetExplorerPath).
// En la sala no se quiere: el recorrido es solo el sitio de salida. Así que, justo después, se devuelven como estaban las
// que quedan más allá del tren (su largo + 50 m desde la salida); las de debajo del tren se dejan, porque son las que lo
// ponen en la vía como dice el .pat. Se devuelven del mismo modo que las colocó Open Rails (alineando el cruce con la rama
// de antes). En la sala, además, manda el despachador: esto evita que tu Open Rails las tenga distintas mientras carga.
// Si algo falla, se deja como lo haya hecho Open Rails.

using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Orts.Simulation.Physics;
using Orts.Simulation.Signalling;

namespace SelectOR.MapAgent
{
    public static class Spawn
    {
        // (desde Agent.Install) 1 = puesto.
        public static int Install(Harmony h)
        {
            if (Environment.GetEnvironmentVariable("SELECTOR_SPAWN_ONLY") != "1") return 0;
            var m = AccessTools.Method(typeof(Train), "PresetExplorerPath");
            if (m == null) return 0;
            h.Patch(m, prefix: new HarmonyMethod(typeof(Spawn), nameof(Before)), postfix: new HarmonyMethod(typeof(Spawn), nameof(After)));
            return 1;
        }

        // Cómo estaban las agujas antes (por tramo de cruce).
        static void Before(Signals orgSignals, out Dictionary<int, int> __state)
        {
            __state = null;
            try
            {
                __state = new Dictionary<int, int>();
                foreach (var s in orgSignals.TrackCircuitList)
                    if (s != null && s.CircuitType == TrackCircuitSection.TrackCircuitType.Junction) __state[s.Index] = s.JunctionLastRoute;
            }
            catch { __state = null; }
        }

        static void After(Train __instance, Signals orgSignals, Dictionary<int, int> __state)
        {
            if (__state == null) return;
            try
            {
                var route = __instance.TCRoute?.TCRouteSubpaths?[0];
                if (route == null) return;
                float cap = __instance.Cars.Sum(c => c.CarLengthM) + 50f, acc = 0;
                int kept = 0, back = 0;
                for (int i = 0; i < route.Count; i++)
                {
                    var s = orgSignals.TrackCircuitList[route[i].TCSectionIndex];
                    if (s.CircuitType == TrackCircuitSection.TrackCircuitType.Junction && __state.TryGetValue(s.Index, out int was) && s.JunctionLastRoute != was)
                    {
                        if (acc <= cap) kept++;
                        else if (was >= 0 && was <= 1) { s.alignSwitchPins(s.Pins[1, was].Link); back++; }
                    }
                    acc += s.Length;
                }
                int all = 0;
                foreach (var kv in __state) { var s = orgSignals.TrackCircuitList[kv.Key]; if (s.JunctionLastRoute != kv.Value) all++; }
                Agent.Log($"sala compartida: el recorrido solo para la salida · agujas del recorrido devueltas {back} · bajo el tren {kept} · distintas ahora en toda la ruta {all}");
            }
            catch (Exception ex) { Agent.Log("sala compartida: salida: " + ex.GetType().Name + " " + ex.Message); }
        }
    }
}
