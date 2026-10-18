// Ctrl+Tab (y Ctrl+Shift+Tab) en multijugador: «liberar la señal» de tu tren (1.2.68).
// Open Rails lo tiene (Train.RequestResetSignal: deshace el itinerario que tu tren tiene pedido más allá de la señal de
// delante y la devuelve a parada) pero en multijugador no hace nada. Aquí:
//  · jugador: se le pide al despachador fijo («SELECTORRESET»: el servidor de la sala se lo pasa; SelectOR Despachador
//    hace lo mismo que Open Rails con tu tren);
//  · si eres tú el despachador: lo mismo que Open Rails, con tu tren.
// Tab (pedir paso) ya funciona en multijugador sin nada: lo atiende el despachador.
// Con Harmony, en memoria (no cambia ningún archivo de Open Rails).

using System;
using HarmonyLib;
using Orts.Formats.Msts;
using Orts.MultiPlayer;
using Orts.Simulation;
using Orts.Simulation.Physics;
using Orts.Simulation.Signalling;
using ORTS.Common;

namespace SelectOR.MapAgent
{
    public static class Liberar
    {
        // (desde Agent.Install) cuántos parches ha puesto.
        public static int Install(Harmony h)
        {
            try
            {
                var m = AccessTools.Method(typeof(Train), "RequestResetSignal");
                if (m == null) return 0;
                h.Patch(m, prefix: new HarmonyMethod(typeof(Liberar), nameof(Before)));
                return 1;
            }
            catch (Exception ex) { Agent.Log("liberar la señal: " + ex.GetType().Name + " " + ex.Message); return 0; }
        }

        static bool Before(Train __instance, Direction direction)
        {
            try
            {
                if (!MPManager.IsMultiPlayer()) return true;   // fuera de multijugador, como siempre
                if (MPManager.IsClient())
                {
                    const string body = "SELECTORRESET";
                    MPManager.Notify(" " + body.Length + ": " + body);
                    Say("Pedido al despachador: liberar la señal de tu tren. · Asked the dispatcher to release your train's signal.");
                    Agent.Log("Ctrl+Tab: liberar la señal, pedido al despachador");
                    return false;
                }
                // eres el despachador: lo de Open Rails, con tu tren
                int num = direction == Direction.Forward ? 0 : 1;
                var ns = __instance.NextSignalObject?[num];
                var route = __instance.ValidRoute?[num];
                if (ns != null && route != null && ns.this_sig_lr(MstsSignalFunction.NORMAL) != MstsSignalAspect.STOP)
                {
                    int ri = route.GetRouteIndex(ns.TCNextTC, __instance.PresentPosition[num].RouteListIndex);
                    if (ri >= 0)
                    {
                        __instance.signalRef.BreakDownRouteList(route, ri, __instance.routedForward);
                        route.RemoveRange(ri, route.Count - ri);
                        ns.ResetSignal(true);
                        Agent.Log($"Ctrl+Tab: señal {ns.thisRef} liberada (eres el despachador)");
                    }
                }
                return false;
            }
            catch (Exception ex) { Agent.Log("Ctrl+Tab: " + ex.GetType().Name + " " + ex.Message); return false; }
        }

        static void Say(string text)
        {
            try { MPManager.Simulator?.Confirmer?.Message(ConfirmLevel.Information, text); } catch { }
        }
    }
}
