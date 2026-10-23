// El ratón de Open Rails y las ventanas de SelectOR (1.2.75).
// Open Rails lee el ratón de todo el escritorio mientras su ventana está activa (UserInput.Update → Mouse.GetState): un
// clic en el pupitre, el HUD o el mapa grande de SelectOR, que están encima de su ventana, le llegaba también a él en ese
// mismo punto, y accionaba el mando de su cabina que quedara debajo (y la rueda movía su cámara). En CYZYRails, algunos
// de esos mandos fallan al pulsarlos así (NullReferenceException en CabViewDiscreteRenderer.HandleUserInput) y Open
// Rails se cerraba: «al darle a los botones del pupitre» (pantógrafo, arenero, luces: según lo que hubiera debajo).
//  · Mientras el puntero está sobre una ventana que no es de Open Rails, su ratón se ve sin botones pulsados y sin
//    rueda (la posición, la de siempre).
//  · Y si aun así un mando de la cabina falla al accionarlo con el ratón, se apunta y Open Rails sigue (antes se cerraba).
// Con Harmony, en memoria (no cambia ningún archivo de Open Rails).

using System;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using Microsoft.Xna.Framework.Input;

namespace SelectOR.MapAgent
{
    public static class Raton
    {
        static FieldInfo _ms, _last;
        static int _said, _fails;

        // ¿El puntero está sobre una ventana de otro programa? (las pruebas lo cambian)
        public static Func<bool> OverOther = PointerOverOther;
        static readonly uint _pid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        static bool PointerOverOther()
        {
            if (!GetCursorPos(out var p)) return false;
            IntPtr w = WindowFromPoint(p);
            if (w == IntPtr.Zero) return false;
            GetWindowThreadProcessId(w, out uint pid);
            return pid != 0 && pid != _pid;
        }

        // (desde Teclado.Install: los mismos tipos del 3D) 1 = puesto
        public static int Install(Harmony h)
        {
            int n = 0;
            try
            {
                var t = AccessTools.TypeByName("Orts.Viewer3D.UserInput");
                var upd = t == null ? null : AccessTools.Method(t, "Update");
                _ms = t == null ? null : AccessTools.Field(t, "MouseState");
                _last = t == null ? null : AccessTools.Field(t, "LastMouseState");
                if (upd != null && _ms != null && _last != null && _ms.FieldType == typeof(MouseState))
                {
                    h.Patch(upd, postfix: new HarmonyMethod(typeof(Raton), nameof(After)));
                    n = 1;
                }
            }
            catch (Exception ex) { Agent.Log("ratón: " + ex.GetType().Name + " " + ex.Message); }
            // los mandos de la cabina con el ratón: que un fallo de uno no cierre Open Rails
            try
            {
                var iface = AccessTools.TypeByName("Orts.Viewer3D.RollingStock.ICabViewMouseControlRenderer");
                if (iface != null)
                {
                    int g = 0;
                    foreach (var ty in AccessTools.GetTypesFromAssembly(iface.Assembly))
                    {
                        if (ty.IsInterface || ty.IsAbstract || !iface.IsAssignableFrom(ty)) continue;
                        var m = ty.GetMethod("HandleUserInput", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
                        if (m == null || m.IsAbstract) continue;
                        h.Patch(m, finalizer: new HarmonyMethod(typeof(Raton), nameof(Guard)));
                        g++;
                    }
                    if (g > 0) n |= 2;
                }
            }
            catch (Exception ex) { Agent.Log("ratón (mandos de la cabina): " + ex.GetType().Name + " " + ex.Message); }
            return n;
        }

        // (cada vuelta, tras leer Open Rails el ratón)
        static void After()
        {
            try
            {
                if (!OverOther()) return;
                var ms = (MouseState)_ms.GetValue(null);
                var last = (MouseState)_last.GetValue(null);
                if (ms.LeftButton == ButtonState.Released && ms.RightButton == ButtonState.Released && ms.MiddleButton == ButtonState.Released
                    && ms.XButton1 == ButtonState.Released && ms.XButton2 == ButtonState.Released && ms.ScrollWheelValue == last.ScrollWheelValue) return;
                _ms.SetValue(null, new MouseState(ms.X, ms.Y, last.ScrollWheelValue, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released));
                if (_said++ == 0) Agent.Log("ratón: un clic en una ventana de SelectOR ya no le llega a Open Rails");
            }
            catch { }
        }

        // un mando de la cabina que falla al accionarlo con el ratón: se apunta y Open Rails sigue
        static Exception Guard(Exception __exception, object __instance)
        {
            if (__exception == null) return null;
            if (!(__exception is NullReferenceException || __exception is IndexOutOfRangeException || __exception is ArgumentOutOfRangeException || __exception is InvalidCastException)) return __exception;
            if (_fails++ < 5)
            {
                string what = "?";
                try
                {
                    var ctl = AccessTools.Field(__instance.GetType(), "Control")?.GetValue(__instance);
                    var ct = ctl == null ? null : AccessTools.Field(ctl.GetType(), "ControlType")?.GetValue(ctl);
                    what = ct?.ToString() ?? __instance.GetType().Name;
                }
                catch { }
                Agent.Log($"cabina: el mando {what} ha fallado al accionarlo con el ratón (Open Rails sigue): {__exception.GetType().Name} {__exception.Message}\n   {(__exception.StackTrace ?? "").Replace("\r\n", "\n").Replace("\n", "\n   ")}");
            }
            return null;
        }
    }
}
