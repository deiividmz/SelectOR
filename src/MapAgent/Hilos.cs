// Open Rails que se cerraba de golpe al cortarse la sala (1.2.69). Se pone en todas las versiones (lo carga el agente en
// cualquier Open Rails); el fallo solo lo tenían las de .NET 6 (Testing…), porque en .NET Framework Thread.Abort sí existe.
// Al perder la conexión con el servidor (o si este le rechaza), Open Rails termina su hilo de red con Thread.Abort(), que
// en .NET 6 no existe: lanza PlatformNotSupportedException y, al no recogerla nadie, se cierra el programa entero (en el
// registro de Windows: «Thread abort is not supported on this platform» en ClientComm.Receive). Pasaba con cada reinicio
// del servidor o cada vez que la sala cortaba a alguien. Aquí, ese error se recoge: el hilo de red termina sin más y Open
// Rails sigue, como en .NET Framework (conduces solo). Lo mismo al alojar tú (OnlinePlayer.Receive, ServerComm.Stop).
// Con Harmony, en memoria (no cambia ningún archivo de Open Rails).

using System;
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
            return n;
        }

        static Exception Fin(Exception __exception)
        {
            if (__exception is PlatformNotSupportedException)
            {
                Agent.Log("conexión con la sala cortada: Open Rails se habría cerrado (Thread.Abort en .NET 6); sigue en marcha");
                return null;
            }
            return __exception;
        }
    }
}
