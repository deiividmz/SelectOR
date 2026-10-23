// Open Rails que se cerraba de golpe al cortarse la sala (1.2.69). Se pone en todas las versiones (lo carga el agente en
// cualquier Open Rails); el fallo solo lo tenían las de .NET 6 (Testing…), porque en .NET Framework Thread.Abort sí existe.
// Al perder la conexión con el servidor (o si este le rechaza), Open Rails termina su hilo de red con Thread.Abort(), que
// en .NET 6 no existe: lanza PlatformNotSupportedException y, al no recogerla nadie, se cierra el programa entero (en el
// registro de Windows: «Thread abort is not supported on this platform» en ClientComm.Receive). Pasaba con cada reinicio
// del servidor o cada vez que la sala cortaba a alguien. Aquí, ese error se recoge: el hilo de red termina sin más y Open
// Rails sigue, como en .NET Framework (conduces solo). Lo mismo al alojar tú (OnlinePlayer.Receive, ServerComm.Stop).
// Con Harmony, en memoria (no cambia ningún archivo de Open Rails).

using System;
using System.Reflection;
using HarmonyLib;
using Orts.MultiPlayer;

namespace SelectOR.MapAgent
{
    public static class Hilos
    {
        // (desde Agent.Install) cuántos parches ha puesto.
        public static int Install(Harmony h)
        {
            int n = 0;
            foreach (var (t, name) in new[] { (typeof(ClientComm), "Receive"), (typeof(OnlinePlayer), "Receive"), (typeof(ServerComm), "Stop") })
            {
                try
                {
                    var m = AccessTools.Method(t, name);
                    if (m == null) continue;
                    h.Patch(m, finalizer: new HarmonyMethod(typeof(Hilos), nameof(Fin)));
                    n++;
                }
                catch (Exception ex) { Agent.Log($"hilos de red ({t.Name}.{name}): {ex.GetType().Name} {ex.Message}"); }
            }
            // (1.2.75) en el registro: cuándo se corta la conexión con la sala y si el servidor dice que el nombre está repetido
            try
            {
                var msg = AccessTools.Method(typeof(MSGMessage), "HandleMsg");
                _level = AccessTools.Field(typeof(MSGMessage), "level"); _text = AccessTools.Field(typeof(MSGMessage), "msgx") ?? AccessTools.Field(typeof(MSGMessage), "msg"); _user = AccessTools.Field(typeof(MSGMessage), "user");
                if (msg != null && _level != null) h.Patch(msg, prefix: new HarmonyMethod(typeof(Hilos), nameof(BeforeMessage)));
            }
            catch (Exception ex) { Agent.Log("hilos de red (MSGMessage): " + ex.GetType().Name + " " + ex.Message); }
            return n;
        }

        static FieldInfo _level, _text, _user;
        static void BeforeMessage(object __instance)
        {
            try
            {
                string level = _level.GetValue(__instance) as string;
                string to = _user?.GetValue(__instance) as string;
                if ((level == "SameNameError" || level == "Error") && (to == null || to == MPManager.GetUserName()))
                    Agent.Log($"sala: el servidor dice {level}: {_text?.GetValue(__instance) as string}");
            }
            catch { }
        }

        static Exception Fin(Exception __exception, MethodBase __originalMethod)
        {
            try
            {
                if (__originalMethod?.DeclaringType == typeof(ClientComm) && __originalMethod.Name == "Receive")
                    Agent.Log("sala: se ha cortado la conexión de Open Rails con el servidor" + (__exception != null ? $" ({__exception.GetType().Name})" : "") + "; sigue conduciendo solo");
            }
            catch { }
            if (__exception is PlatformNotSupportedException)
            {
                Agent.Log("conexión con la sala cortada: Open Rails se habría cerrado (Thread.Abort en .NET 6); sigue en marcha");
                return null;
            }
            return __exception;
        }
    }
}
