// Las agujas que cambia la sala, también para tu tren (1.2.83).
//  · En multijugador, cuando la sala cambia una aguja, a tu Open Rails le llega la foto de agujas (MSGSwitchStatus) y la
//    gira (MSGSwitchStatus.SetSwitch). NY 174 y 175, antes de girarla, la pasan por Signals.RequestSetSwitch: la aguja
//    queda «puesta a mano», se actualizan sus señales y todos los trenes de tu partida (el tuyo también) recalculan el
//    camino que tienen por delante.
//  · CYZY 0.8 y las versiones oficiales (Estable 1.6, Testing, Inestable) solo la giran: tu tren seguía con el camino de
//    antes, y las señales, el monitor de vía o los límites que ves no cuadraban con la aguja que tienes delante.
//  · Aquí se añade esa misma llamada en el mismo sitio (dentro de la foto de agujas, justo antes de girar cada una), solo
//    en las versiones que no la traen: si HandleMsg ya llama a RequestSetSwitch, no se pone nada.
// Con Harmony, en memoria (no cambia ningún archivo de Open Rails). Se busca por nombre (las versiones difieren).

using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Orts.Formats.Msts;
using Orts.MultiPlayer;
using Orts.Simulation.Signalling;

namespace SelectOR.MapAgent
{
    public static class AgujasRecibidas
    {
        [ThreadStatic] static int _inPhoto;   // dentro de MSGSwitchStatus.HandleMsg (la foto de agujas de la sala)
        static int _logged;

        // (desde Agent.Install) cuántos parches ha puesto.
        public static int Install(Harmony h)
        {
            try
            {
                var photo = AccessTools.Method(typeof(MSGSwitchStatus), "HandleMsg");
                var turn = AccessTools.Method(typeof(MSGSwitchStatus), "SetSwitch", new[] { typeof(TrackNode), typeof(int) });
                var ask = AccessTools.Method(typeof(Signals), "RequestSetSwitch", new[] { typeof(TrackNode), typeof(int) });
                if (photo == null || turn == null || ask == null)
                {
                    Agent.Log("agujas recibidas: este Open Rails no tiene lo necesario; se quedan como están");
                    return 0;
                }
                if (Calls(photo, "RequestSetSwitch"))
                {
                    Agent.Log("agujas recibidas: este Open Rails ya avisa a tu tren (como NY 174); nada que añadir");
                    return 0;
                }
                h.Patch(photo, prefix: new HarmonyMethod(typeof(AgujasRecibidas), nameof(Enter)), finalizer: new HarmonyMethod(typeof(AgujasRecibidas), nameof(Leave)));
                h.Patch(turn, prefix: new HarmonyMethod(typeof(AgujasRecibidas), nameof(BeforeTurn)));
                Agent.Log("agujas recibidas: las que cambia la sala recalculan también el camino de tu tren (como NY 174)");
                return 2;
            }
            catch (Exception ex) { Agent.Log("agujas recibidas: " + ex.GetType().Name + " " + ex.Message); return 0; }
        }

        // ¿El método llama a otro con ese nombre? (su IL original, sin parches)
        static bool Calls(MethodBase m, string name)
        {
            try { return PatchProcessor.ReadMethodBody(m).Any(kv => kv.Value is MethodBase mb && mb.Name == name); }
            catch { return false; }
        }

        static void Enter() => _inPhoto++;
        static Exception Leave(Exception __exception) { _inPhoto = Math.Max(0, _inPhoto - 1); return __exception; }

        // Lo que hace NY 174 justo antes de girar la aguja: Signals.RequestSetSwitch(aguja, posición)
        static void BeforeTurn(TrackNode __0, int __1)
        {
            if (_inPhoto <= 0) return;
            try
            {
                if (!MPManager.IsMultiPlayer() || MPManager.IsServer()) return;
                bool ok = MPManager.Simulator.Signals.RequestSetSwitch(__0, __1);
                if (_logged < 5) { _logged++; Agent.Log($"agujas recibidas: aguja {__0.Index} → {__1} de la sala{(ok ? ", camino de los trenes al día" : " (la tiene reservada tu Open Rails: solo se gira)")}"); }
            }
            catch (Exception ex) { if (_logged < 5) { _logged++; Agent.Log("agujas recibidas: " + ex.GetType().Name + " " + ex.Message); } }
        }
    }
}
