// Map Window de Open Rails (Ctrl+9) en multijugador: lo que el propio Open Rails hace mal (T1.6.1).
//  · El texto del papel dice siempre «You are the dispatcher in this multiplayer session», también a los demás: ahora
//    dice si eres el despachador, ayudante (puedes cambiar desvíos y señales desde el mapa) o un jugador.
//  · Las casillas del despachador (penalización por pasar en rojo o correr, «Prefer green», dejar entrar a más
//    jugadores) se podían cambiar en cualquier jugador: no hacían nada en él, salvo la penalización, que avisaba a todos
//    como si la hubiera cambiado el despachador. Ahora solo las cambia el despachador.
//  · En la lista de jugadores, «Make this player an assistant / Demote this player» y «Kick» desde un jugador que no es
//    el despachador: hacer o quitar ayudante a otro sí llegaba a ese jugador (le quitaba los desvíos). Solo el despachador.
//  · «Allow call on» (rebase autorizado) salía siempre apagado en los jugadores: el jugador no sabe para qué tren está
//    abierta la señal (eso lo sabe el despachador, que es quien lo comprueba). Ahora un ayudante lo puede pedir.
// Se busca la Map Window por su nombre: si esta versión de Open Rails no la tiene así, no se toca nada.

using System;
using System.Globalization;
using System.Reflection;
using System.Windows.Forms;
using HarmonyLib;
using Orts.MultiPlayer;

namespace SelectOR.SignalGuard
{
    static class MapWindow
    {
        const string TypeName = "Orts.Viewer3D.Debugging.MapViewer";
        static bool _done;
        static readonly bool Es = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "es";

        public static void Install(Harmony h)
        {
            if (TryPatch(h)) return;
            // (si RunActivity aún no está cargado, en cuanto lo esté)
            // (mientras Open Rails arranca, su ventana aún no se puede cargar: se reintenta en silencio con lo siguiente)
            AppDomain.CurrentDomain.AssemblyLoad += (s, e) => { if (!_done) try { TryPatch(h); } catch { } };
        }

        static bool TryPatch(Harmony h)
        {
            if (_done) return true;
            Type t = null;
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                if (a.GetName().Name == "RunActivity") { t = a.GetType(TypeName); break; }
            if (t == null) return false;
            _done = true;
            int n = 0;
            n += Patch(h, t, "CheckPlayers", nameof(AfterCheckPlayers));
            n += Patch(h, t, "HandlePickedSignal", nameof(AfterPickedSignal));
            n += Patch(h, t, "playersView_MouseClick", nameof(AfterPlayersClick));
            int st = 0;
            try { st = MapStyle.Install(h, t); } catch (Exception ex) { Guard.Log("Map Window, aspecto: " + ex.Message); }
            Guard.Log($"Map Window: {n} de 3 arreglos puestos · aspecto SelectOR: {st} de 5");
            return true;
        }

        static int Patch(Harmony h, Type t, string method, string postfix)
        {
            var m = AccessTools.Method(t, method);
            if (m == null) { Guard.Log($"Map Window: no tiene {method}"); return 0; }
            h.Patch(m, postfix: new HarmonyMethod(typeof(MapWindow), postfix));
            return 1;
        }

        static T Ctl<T>(object w, string field) where T : class => Traverse.Create(w).Field(field).GetValue() as T;

        // Cada vez que repasa la lista de jugadores: el papel y las casillas del despachador.
        static void AfterCheckPlayers(object __instance)
        {
            try
            {
                if (!MPManager.IsMultiPlayer()) return;
                bool server = MPManager.IsServer(), aider = !server && MPManager.Instance().AmAider;
                var role = Ctl<Label>(__instance, "playerRoleExplanation");
                if (role != null)
                {
                    string t = server ? (Es ? "Eres el despachador de esta partida multijugador" : "You are the dispatcher in this multiplayer session")
                             : aider ? (Es ? "Eres ayudante: puedes cambiar desvíos y señales desde el mapa" : "You are a helper: you can throw switches and set signals from the map")
                             : (Es ? "Eres un jugador de esta partida multijugador" : "You are a player in this multiplayer session");
                    if (role.Text != t) role.Text = t;
                }
                foreach (var name in new[] { "penaltyCheckbox", "preferGreenCheckbox", "allowJoiningCheckbox" })
                {
                    var c = Ctl<Control>(__instance, name);
                    if (c != null && c.Enabled != server) c.Enabled = server;
                }
            }
            catch { }
        }

        // Menú de una señal: el ayudante puede pedir el rebase autorizado (el despachador decide si vale).
        static void AfterPickedSignal(object __instance)
        {
            try
            {
                if (!MPManager.IsClient() || !MPManager.Instance().AmAider) return;
                var item = Ctl<ToolStripItem>(__instance, "allowCallOnToolStripMenuItem");
                if (item != null) item.Enabled = true;
            }
            catch { }
        }

        // Menú de un jugador: hacer o quitar ayudante y echar, solo el despachador (y el texto, en español).
        static void AfterPlayersClick(object __instance)
        {
            try
            {
                string who = (Ctl<ToolStripItem>(__instance, "playerToolStripMenuItem"))?.Text ?? "";
                MapStyle.PlayerMenuText(__instance, MPManager.Instance()?.aiderList?.Contains(who) == true);
                if (MPManager.IsServer()) return;
                foreach (var name in new[] { "makeThisPlayerAnAssistantToolStripMenuItem", "kickFromMultiplayerSessionToolStripMenuItem" })
                {
                    var item = Ctl<ToolStripItem>(__instance, name);
                    if (item != null) item.Enabled = false;
                }
            }
            catch { }
        }
    }
}
