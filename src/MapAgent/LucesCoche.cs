// CYZYRails: el cierre de «La secuencia no contiene elementos» al dibujar las luces de un coche (1.2.81). CYZY, en
// LightViewer.PrepareFrame, hace TrainCarShape.Hierarchy.Max(); si la forma 3D (.s) del coche no se puede cargar (contenido
// que le falta a ese jugador: «DirectoryNotFoundException …\Renfe Pack UT440R»), Open Rails le pone una forma vacía, sin
// jerarquía, y Max() revienta: Open Rails se cerraba (logs de Iván y Delfi, 26 y 27/10, con trenes de otros y de la IA).
// Aquí: un coche sin jerarquía no prepara sus luces (las demás sí), y si aun así salta ese error, se queda en el registro
// sin cerrar Open Rails.

using System;
using System.Collections;
using HarmonyLib;

namespace SelectOR.MapAgent
{
    public static class LucesCoche
    {
        static int _said;

        public static int Install(Harmony h)
        {
            try
            {
                var t = AccessTools.TypeByName("Orts.Viewer3D.LightViewer");
                var m = t == null ? null : AccessTools.Method(t, "PrepareFrame");
                if (m == null) return 0;
                h.Patch(m, prefix: new HarmonyMethod(typeof(LucesCoche), nameof(Before)), finalizer: new HarmonyMethod(typeof(LucesCoche), nameof(After)));
                return 1;
            }
            catch (Exception ex) { Agent.Log("luces de los coches: " + ex.GetType().Name + " " + ex.Message); return 0; }
        }

        // ¿la forma del coche tiene jerarquía? (si no se sabe, se deja hacer)
        static bool Before(object __instance)
        {
            try
            {
                var shape = Traverse.Create(__instance).Field("CarViewer").Field("TrainCarShape").GetValue();
                if (shape == null) return true;
                var hier = Traverse.Create(shape).Field("Hierarchy").GetValue() ?? Traverse.Create(shape).Property("Hierarchy").GetValue();
                if (hier is ICollection c && c.Count == 0)
                {
                    if (_said++ < 5) Agent.Log("luces de un coche sin forma 3D (contenido que falta): no se dibujan");
                    return false;
                }
            }
            catch { }
            return true;
        }

        static Exception After(Exception __exception)
        {
            if (__exception is InvalidOperationException)
            {
                if (_said++ < 10) Agent.Log("luces de un coche: " + __exception.Message + " (se salta ese coche)");
                return null;
            }
            return __exception;
        }
    }
}
