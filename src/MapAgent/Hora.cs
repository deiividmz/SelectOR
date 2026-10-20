// La hora de la sala en el Open Rails de cada jugador (1.2.74).
// Open Rails pone la hora del servidor solo al entrar (en el «PLAYER» de vuelta). Después, cada minuto, el servidor manda su
// hora («TimeCheck»), pero Open Rails solo la usa para enseñarla en el HUD (serverTimeDifference): el sol, el cielo y las
// luces siguen con el reloj de cada uno. Aquí, si el servidor dice otra hora (más de 1 min de diferencia: el
// administrador ha adelantado o retrasado la de la sala), se pone esa en el reloj de este Open Rails.
// Este archivo lo comparten el agente de SelectOR y SelectOR Despachador (en modo jugador, para las pruebas).

using System;
using HarmonyLib;
using Orts.MultiPlayer;

namespace SelectOR.Comun
{
    public static class Hora
    {
        static Action<string> _log;

        // 1 = puesto.
        public static int Install(Harmony h, Action<string> log)
        {
            _log = log;
            try
            {
                var m = AccessTools.Method(typeof(MSGMessage), "HandleMsg", Type.EmptyTypes);
                var f = AccessTools.Field(typeof(MPManager), "serverTimeDifference");
                if (m == null || f == null) return 0;
                h.Patch(m, postfix: new HarmonyMethod(typeof(Hora), nameof(After)));
                return 1;
            }
            catch (Exception ex) { log?.Invoke("hora de la sala: " + ex.GetType().Name + " " + ex.Message); return 0; }
        }

        static void After()
        {
            try
            {
                var sim = MPManager.Simulator;
                if (sim == null || MPManager.IsServer() || !MPManager.IsMultiPlayer()) return;
                var mp = MPManager.Instance();
                double d = mp.serverTimeDifference;
                if (Math.Abs(d) <= 60) return;
                sim.ClockTime += d;
                mp.serverTimeDifference = 0;
                _log?.Invoke($"hora de la sala: el reloj se ajusta {(d > 0 ? "+" : "")}{d / 60:0} min");
            }
            catch { }
        }
    }
}
