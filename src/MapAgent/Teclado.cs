// Escribir en el chat del HUD sin que Open Rails reaccione a las teclas (1.2.69).
// La cajita del chat de SelectOR toma el teclado, pero a veces Windows no le deja quitárselo a Open Rails (que es la
// ventana activa) y las letras le llegaban también al simulador (regulador, frenos, cámaras…). Mientras SelectOR
// avisa de que se está escribiendo («TYPING segundos» por la conexión local, LocalLink.cs; caduca solo si deja de
// avisar), aquí el teclado de Open Rails se ve vacío en cada vuelta (UserInput.Update): no le llega ninguna tecla y no se
// queda ninguna pegada. Los mandos (palancas y botones) siguen yendo.
// Al terminar (Intro para enviar, Esc para cerrar) la cajita se cierra con la tecla aún pulsada: si se soltara el teclado
// en ese momento, Open Rails la vería como una pulsación nueva (Esc le abría el menú de pausa). Por eso sigue vacío hasta
// que no quede ninguna tecla pulsada (como mucho 3 s).
// (1.2.72) Y, aunque no llegue el aviso, mientras la ventana de Open Rails no esté en primer plano (la cajita del chat,
// otro programa) su teclado también se ve vacío: algunos Open Rails seguían leyendo Esc, Intro, Retroceso… con la
// cajita del chat delante. Al volver, igual: vacío hasta que se suelten todas las teclas.
// Con Harmony, en memoria (no cambia ningún archivo de Open Rails).

using System;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;

namespace SelectOR.MapAgent
{
    public static class Teclado
    {
        public static DateTime TypingUntil = DateTime.MinValue;
        static DateTime _drainUntil = DateTime.MinValue;   // después de escribir, hasta que se suelten las teclas
        static FieldInfo _kb;
        static MethodInfo _pressed;
        static object _empty;
        static bool _saidTyping, _saidAway;

        // ¿La ventana de primer plano es de Open Rails (este proceso)? (las pruebas lo cambian)
        public static Func<bool> Foreground = OrInFront;
        static readonly uint _pid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        static bool OrInFront()
        {
            IntPtr w = GetForegroundWindow();
            if (w == IntPtr.Zero) return true;   // (no se sabe: como siempre)
            GetWindowThreadProcessId(w, out uint pid);
            return pid == _pid;
        }

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
                _pressed = AccessTools.Method(_kb.FieldType, "GetPressedKeys", Type.EmptyTypes);
                h.Patch(m, postfix: new HarmonyMethod(typeof(Teclado), nameof(After)));
                int r = Raton.Install(h);   // y el ratón: los clics en las ventanas de SelectOR no le llegan (Raton.cs)
                Agent.Log("ratón:" + ((r & 1) != 0 ? " clics de SelectOR fuera de Open Rails" : " (sin filtro)") + ((r & 2) != 0 ? " · mandos de la cabina protegidos" : ""));
                return 1;
            }
            catch (Exception ex) { Agent.Log("teclado: " + ex.GetType().Name + " " + ex.Message); return 0; }
        }

        // «TYPING s»: s segundos escribiendo (0 = ya no)
        public static void Typing(int seconds)
        {
            TypingUntil = seconds > 0 ? DateTime.UtcNow.AddSeconds(Math.Min(seconds, 30)) : DateTime.MinValue;
            if (seconds > 0 && !_saidTyping) { _saidTyping = true; Agent.Log("chat: escribiendo, Open Rails no recibe el teclado"); }
        }

        static void After()
        {
            var now = DateTime.UtcNow;
            bool typing = now < TypingUntil, away = false;
            if (!typing) try { away = !Foreground(); } catch { }
            if (typing || away)
            {
                if (away && !_saidAway && AnyKeyDown()) { _saidAway = true; Agent.Log("teclado: Open Rails leía teclas sin estar en primer plano; no le llegan"); }
                _drainUntil = now.AddSeconds(3); Clear(); return;
            }
            if (now >= _drainUntil) return;
            if (AnyKeyDown()) { Clear(); return; }   // (lo que Open Rails acaba de leer del teclado de verdad)
            _drainUntil = DateTime.MinValue;
        }

        static void Clear()
        {
            try { _kb.SetValue(null, _empty); } catch { }
        }

        static bool AnyKeyDown()
        {
            try { return _pressed != null && _pressed.Invoke(_kb.GetValue(null), null) is Array a && a.Length > 0; }
            catch { return false; }
        }
    }
}
