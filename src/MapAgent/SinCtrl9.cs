// (1.2.91, petición 2026-11-07, con su sí) SIN LA VENTANA DEL Ctrl+9 EN LA SALA COMPARTIDA: en la sala todo lo que hace
// (agujas, señales, itinerarios, ayudantes) está en el mapa grande del HUD de SelectOR, y la sala aplica las mismas reglas.
// Open Rails la abre poniendo Viewer.MapViewerEnabled a true (Orts.Viewer3D.Debugging.MapViewer; igual en NY 174/175 y
// CYZY): en la sala ese «true» se cambia por «false» y sale un aviso. Fuera de la sala, Ctrl+9 sigue como siempre.
// SELECTOR_CTRL9=1 la deja también en la sala (pruebas).
// (2026-11-08, «a algunos aún les sale») Tres cosas más:
//  · «en la sala» ya no es solo entrar por el botón de la sala de SelectOR (SELECTOR_SPAWN_ONLY=1): también cualquier
//    conexión de Open Rails a la dirección de la sala compartida (SELECTOR_SALA_ADDR, que SelectOR pone siempre en
//    multijugador; la de Open Rails, de sus ajustes Multiplayer_Host/Port), entres desde Actividad, Horarios o poniendo
//    la dirección a mano;
//  · la tecla misma: UserInput.IsPressed(GameMultiPlayerDispatcher) da «no» (se busca por nombre: el mismo en todas las
//    versiones), por si en alguna la ventana se abre sin pasar por MapViewerEnabled;
//  · y en cada vuelta, si la ventana está abierta, se cierra.

using System;
using System.Reflection;
using HarmonyLib;
using ORTS.Common;
using Orts.MultiPlayer;
using Orts.Simulation;

namespace SelectOR.MapAgent
{
    public static class SinCtrl9
    {
        static DateTime _said;
        static object _cmd;                 // UserCommand.GameMultiPlayerDispatcher (de esta versión)
        static PropertyInfo _mapProp;       // Viewer.MapViewerEnabled
        static bool? _inRoom;               // (se mira una vez conectado)
        static bool Off => Environment.GetEnvironmentVariable("SELECTOR_CTRL9") == "1";
        // ¿puede que estemos en la sala? (para poner los parches; si de verdad lo estamos, InRoom)
        public static bool Wanted => !Off && (Environment.GetEnvironmentVariable("SELECTOR_SPAWN_ONLY") == "1"
                                               || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SELECTOR_SALA_ADDR")));

        // en la sala compartida: entrando por su botón, o conectados a su dirección
        static bool InRoom()
        {
            if (Off) return false;
            if (Environment.GetEnvironmentVariable("SELECTOR_SPAWN_ONLY") == "1") return true;
            if (_inRoom.HasValue) return _inRoom.Value;
            try
            {
                if (!MPManager.IsMultiPlayer()) return false;   // (aún sin conectar: se vuelve a mirar)
                string want = (Environment.GetEnvironmentVariable("SELECTOR_SALA_ADDR") ?? "").Trim();
                var sim = MPManager.Simulator;
                var set = sim == null ? null : Member(sim, "Settings");   // (por nombre: el tipo de los ajustes cambia entre versiones)
                if (want.Length == 0 || set == null) { _inRoom = false; return false; }
                string host = (Member(set, "Multiplayer_Host") ?? "").ToString().Trim();
                string port = (Member(set, "Multiplayer_Port") ?? "").ToString().Trim();
                _inRoom = Array.Exists(want.Split(','), w => string.Equals(host + ":" + port, w.Trim(), StringComparison.OrdinalIgnoreCase));   // (la IP o el nombre)
                Agent.Log($"sin Ctrl+9: conectado a {host}:{port} → {(_inRoom.Value ? "la sala compartida: sin Ctrl+9" : "otro servidor: Ctrl+9 como siempre")}");
                return _inRoom.Value;
            }
            catch { return false; }
        }

        static object Member(object o, string n)
        {
            var t = o.GetType();
            var f = t.GetField(n, BindingFlags.Public | BindingFlags.Instance); if (f != null) return f.GetValue(o);
            var p = t.GetProperty(n, BindingFlags.Public | BindingFlags.Instance); return p?.GetValue(o);
        }

        public static int Install(Harmony h)
        {
            if (!Wanted) return 0;
            try
            {
                var viewer = AccessTools.TypeByName("Orts.Viewer3D.Viewer");
                if (viewer == null) return 0;   // (aún sin cargar: Agent.Later)
                int n = 0;
                _mapProp = AccessTools.Property(viewer, "MapViewerEnabled");
                var set = _mapProp?.GetSetMethod(true);
                if (set != null) { h.Patch(set, prefix: new HarmonyMethod(typeof(SinCtrl9), nameof(BeforeSet))); n++; }
                else Agent.Log("sin Ctrl+9: este Open Rails no tiene Viewer.MapViewerEnabled con «set»");
                // la tecla
                var input = AccessTools.TypeByName("Orts.Viewer3D.UserInput");
                var isPressed = input == null ? null : Array.Find(input.GetMethods(BindingFlags.Public | BindingFlags.Static),
                                    m => m.Name == "IsPressed" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.IsEnum);
                var cmdT = isPressed?.GetParameters()[0].ParameterType;
                if (cmdT != null && Enum.IsDefined(cmdT, "GameMultiPlayerDispatcher"))
                {
                    _cmd = Enum.Parse(cmdT, "GameMultiPlayerDispatcher");
                    h.Patch(isPressed, postfix: new HarmonyMethod(typeof(SinCtrl9), nameof(AfterIsPressed)));
                    n++;
                }
                else Agent.Log("sin Ctrl+9: no se encuentra la tecla GameMultiPlayerDispatcher");
                // y, en cada vuelta, la ventana cerrada
                var handle = AccessTools.Method(viewer, "HandleUserInput");
                if (handle != null && _mapProp?.GetGetMethod(true) != null) { h.Patch(handle, postfix: new HarmonyMethod(typeof(SinCtrl9), nameof(AfterInput))); n++; }
                return n > 0 ? 1 : 0;
            }
            catch (Exception ex) { Agent.Log("sin Ctrl+9: " + ex.GetType().Name + " " + ex.Message); return 0; }
        }

        static void BeforeSet(ref bool value)
        {
            if (!value || !InRoom()) return;
            value = false;
            Say();
        }

        // (UserInput.IsPressed, muchas veces por vuelta) la tecla del Ctrl+9, en la sala: «no»
        static void AfterIsPressed(object __0, ref bool __result)
        {
            if (!__result || _cmd == null || !_cmd.Equals(__0)) return;
            try { if (!InRoom()) return; } catch { return; }
            __result = false;
            Say();
        }

        static void AfterInput(object __instance)
        {
            try
            {
                if (!(bool)_mapProp.GetValue(__instance) || !InRoom()) return;
                _mapProp.SetValue(__instance, false);
                Say();
            }
            catch { }
        }

        static void Say()
        {
            if ((DateTime.UtcNow - _said).TotalSeconds < 3) return;
            _said = DateTime.UtcNow;
            Agent.Log("Ctrl+9 en la sala: no se abre (todo está en el mapa grande del HUD)");
            try { MPManager.Simulator?.Confirmer?.Message(ConfirmLevel.Information, "En la sala, agujas, señales e itinerarios van en el mapa grande del HUD de SelectOR. · In the room, switches, signals and routes are on SelectOR's big HUD map."); } catch { }
        }
    }
}
