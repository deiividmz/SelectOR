// (1.2.91, petición 2026-11-07, con su sí) SIN LA VENTANA DEL Ctrl+9 EN LA SALA COMPARTIDA: en la sala todo lo que hace
// (agujas, señales, itinerarios, ayudantes) está en el mapa grande del HUD de SelectOR, y la sala aplica las mismas reglas.
// Open Rails la abre poniendo Viewer.MapViewerEnabled a true (Orts.Viewer3D.Debugging.MapViewer; igual en NY 174/175 y
// CYZY): en la sala (SELECTOR_SPAWN_ONLY=1, como AgujasSala/Spawn) ese «true» se cambia por «false» y sale un aviso.
// Fuera de la sala, Ctrl+9 sigue como siempre. SELECTOR_CTRL9=1 la deja también en la sala (pruebas).

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
        public static bool Wanted => Environment.GetEnvironmentVariable("SELECTOR_SPAWN_ONLY") == "1" && Environment.GetEnvironmentVariable("SELECTOR_CTRL9") != "1";

        public static int Install(Harmony h)
        {
            if (!Wanted) return 0;
            try
            {
                var viewer = AccessTools.TypeByName("Orts.Viewer3D.Viewer");
                var set = viewer == null ? null : AccessTools.PropertySetter(viewer, "MapViewerEnabled");
                if (viewer == null) return 0;   // (aún sin cargar: Agent.Later)
                if (set == null) { Agent.Log("sin Ctrl+9: este Open Rails no tiene Viewer.MapViewerEnabled (se deja)"); return 0; }
                h.Patch(set, prefix: new HarmonyMethod(typeof(SinCtrl9), nameof(BeforeSet)));
                return 1;
            }
            catch (Exception ex) { Agent.Log("sin Ctrl+9: " + ex.GetType().Name + " " + ex.Message); return 0; }
        }

        static void BeforeSet(ref bool value)
        {
            if (!value) return;
            value = false;
            if ((DateTime.UtcNow - _said).TotalSeconds < 3) return;
            _said = DateTime.UtcNow;
            Agent.Log("Ctrl+9 en la sala: no se abre (todo está en el mapa grande del HUD)");
            try { MPManager.Simulator?.Confirmer?.Message(ConfirmLevel.Information, "En la sala, agujas, señales e itinerarios van en el mapa grande del HUD de SelectOR. · In the room, switches, signals and routes are on SelectOR's big HUD map."); } catch { }
        }
    }
}
