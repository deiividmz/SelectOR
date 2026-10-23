// Esc en multijugador (1.2.75). Open Rails, en multijugador, no abre su menú de pausa: solo dice «In MP, use Alt-F4 to
// quit directly». Aquí, en multijugador, Esc abre ese mismo menú en su versión de multijugador (Open Rails ya la tiene:
// «Salir» y «Continuar», sin «Guardar») y sin pausar la simulación (en la sala no se puede):
//  · «Continuar» (o Esc otra vez): se cierra el menú;
//  · «Salir»: lo mismo que Alt+F4 (se le pide a la ventana de Open Rails que se cierre) y, si a los 10 s sigue abierto, se
//    cierra a la fuerza.
// El aviso de Alt+F4 ya no sale. Fuera del multijugador, todo como siempre.
// Con Harmony, en memoria (no cambia ningún archivo de Open Rails). Se busca todo por nombre (las versiones de Open Rails
// numeran distinto las teclas).

using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using HarmonyLib;
using Orts.MultiPlayer;

namespace SelectOR.MapAgent
{
    public static class Pausa
    {
        static MethodInfo _isPressed; static object _cmd;
        static PropertyInfo _quitWin, _visible;
        static bool _said;

        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        const uint WM_CLOSE = 0x0010;

        // (desde Agent.Install, o al cargar el 3D) 1 = puesto
        public static int Install(Harmony h)
        {
            try
            {
                var viewer = AccessTools.TypeByName("Orts.Viewer3D.Viewer");
                var input = AccessTools.TypeByName("Orts.Viewer3D.UserInput");
                var quitT = AccessTools.TypeByName("Orts.Viewer3D.Popups.QuitWindow");
                if (viewer == null || input == null || quitT == null) return 0;
                var handle = AccessTools.Method(viewer, "HandleUserInput");
                // IsPressed(UserCommand): el tipo de la tecla, del propio método (cada versión lo tiene en otro sitio)
                _isPressed = Array.Find(input.GetMethods(BindingFlags.Public | BindingFlags.Static), m => m.Name == "IsPressed" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.IsEnum);
                var cmdT = _isPressed?.GetParameters()[0].ParameterType;
                if (cmdT == null || !Enum.IsDefined(cmdT, "GamePauseMenu")) return 0;
                _cmd = Enum.Parse(cmdT, "GamePauseMenu");
                _quitWin = AccessTools.Property(viewer, "QuitWindow");
                _visible = AccessTools.Property(quitT, "Visible");
                var quit = AccessTools.Method(quitT, "buttonQuit_Click");
                if (handle == null || _isPressed == null || _quitWin == null || _visible == null || quit == null) return 0;
                h.Patch(handle, postfix: new HarmonyMethod(typeof(Pausa), nameof(AfterInput)));
                h.Patch(quit, prefix: new HarmonyMethod(typeof(Pausa), nameof(BeforeQuit)));
                // el aviso «In MP, use Alt-F4 to quit directly» (Confirmer.Information)
                var conf = AccessTools.TypeByName("Orts.Simulation.Confirmer") ?? AccessTools.TypeByName("Orts.Common.Confirmer");
                var info = conf == null ? null : AccessTools.Method(conf, "Information", new[] { typeof(string) });
                if (info != null) h.Patch(info, prefix: new HarmonyMethod(typeof(Pausa), nameof(BeforeInfo)));
                return 1;
            }
            catch (Exception ex) { Agent.Log("Esc en multijugador: " + ex.GetType().Name + " " + ex.Message); return 0; }
        }

        // (cada vuelta del 3D) Esc en multijugador: el menú, sin pausar
        static void AfterInput(object __instance)
        {
            try
            {
                if (!MPManager.IsMultiPlayer() || !(bool)_isPressed.Invoke(null, new[] { _cmd })) return;
                var w = _quitWin.GetValue(__instance);
                if (w == null) return;
                bool show = !(bool)_visible.GetValue(w);
                _visible.SetValue(w, show);
                if (!_said) { _said = true; Agent.Log("Esc en multijugador: menú con «Salir» y «Continuar»"); }
            }
            catch { }
        }

        // «Salir» en multijugador: como Alt+F4; si no se cierra en 10 s, a la fuerza
        static bool BeforeQuit()
        {
            try
            {
                if (!MPManager.IsMultiPlayer()) return true;
                Agent.Log("Esc en multijugador: «Salir» (como Alt+F4)");
                Close();
                return false;
            }
            catch { return true; }
        }

        // (las pruebas lo cambian)
        public static Action Close = CloseOr;
        static void CloseOr()
        {
                var me = Process.GetCurrentProcess();
                IntPtr hwnd = me.MainWindowHandle;
                if (hwnd != IntPtr.Zero) PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                new Thread(() =>
                {
                    Thread.Sleep(hwnd != IntPtr.Zero ? 10000 : 0);
                    try { Agent.Log("Esc en multijugador: Open Rails no se ha cerrado; se cierra a la fuerza"); } catch { }
                    try { me.Kill(); } catch { Environment.Exit(0); }
                }) { IsBackground = true }.Start();
        }

        static bool BeforeInfo(string __0)
        {
            try { return !(MPManager.IsMultiPlayer() && __0 != null && (__0.Contains("Alt-F4") || __0.Contains("Alt+F4"))); }
            catch { return true; }
        }
    }
}
