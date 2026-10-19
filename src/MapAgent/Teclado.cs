// Escribir en el chat del HUD sin que Open Rails reaccione a las teclas (1.2.69).
// La cajita del chat de SelectOR toma el teclado, pero a veces Windows no le deja quitárselo a Open Rails (que es la
// ventana activa) y las letras le llegaban también al simulador (regulador, frenos, cámaras…). Mientras SelectOR
// avisa de que se está escribiendo («TYPING segundos» por la conexión local, LocalLink.cs; caduca solo si deja de
// avisar), aquí el teclado de Open Rails se ve vacío en cada vuelta (UserInput.Update): no le llega ninguna tecla y no se
// queda ninguna pegada. Los mandos (palancas y botones) siguen yendo.
// Con Harmony, en memoria (no cambia ningún archivo de Open Rails).

using System;
using System.Reflection;
using HarmonyLib;

namespace SelectOR.MapAgent
{
    public static class Teclado
    {
        public static DateTime TypingUntil = DateTime.MinValue;
        static FieldInfo _kb;
        static object _empty;

        // (desde Agent.Install) 1 = puesto.
        public static int Install(Harmony h)
        {
            try
            {
                var t = AccessTools.TypeByName("Orts.Viewer3D.UserInput");
                var m = t == null ? null : AccessTools.Method(t, "Update");
                _kb = t == null ? null : AccessTools.Field(t, "KeyboardState");
                if (m == null || _kb == null) return 0;
                _empty = Activator.CreateInstance(_kb.FieldType);
                h.Patch(m, postfix: new HarmonyMethod(typeof(Teclado), nameof(After)));
                return 1;
            }
            catch (Exception ex) { Agent.Log("teclado: " + ex.GetType().Name + " " + ex.Message); return 0; }
        }

        // «TYPING s»: s segundos escribiendo (0 = ya no)
        public static void Typing(int seconds)
        {
            TypingUntil = seconds > 0 ? DateTime.UtcNow.AddSeconds(Math.Min(seconds, 30)) : DateTime.MinValue;
        }

        static void After()
        {
            if (DateTime.UtcNow >= TypingUntil) return;
            try { _kb.SetValue(null, _empty); } catch { }
        }
    }
}
