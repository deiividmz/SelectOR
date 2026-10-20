// Bocina, campana, puertas, luces, pantógrafos… entre versiones de Open Rails (1.2.73).
// Cada vez que un jugador toca uno de estos mandos, su Open Rails manda a los demás un «EVENT». Las New Year 174 y 175
// lo escriben con 4 datos (jugador, mando, coche, estado) y las demás (CYZYRails, Testing, la oficial, MG…) con 3
// (jugador, mando, estado): cada una solo entendía la suya y tiraba la otra («Parsing error»), así que en la sala no se
// oían ni se veían los de la otra familia. Aquí se entienden las dos: si llega la que no es la suya, se lee igual (sin
// coche: −1, que en las New Year solo usa el arranque del motor diésel de un coche concreto, y así no se toca ninguno).
// Este archivo lo comparten el agente de SelectOR (en el Open Rails de cada jugador) y SelectOR Despachador (el servidor).

using System;
using System.Reflection;
using HarmonyLib;
using Orts.MultiPlayer;

namespace SelectOR.Comun
{
    public static class Eventos
    {
        static FieldInfo _user, _name, _state, _car;
        static Action<string> _log;
        static int _seen;

        // 1 = puesto.
        public static int Install(Harmony h, Action<string> log)
        {
            _log = log;
            try
            {
                var t = typeof(MSGEvent);
                var ctor = AccessTools.Constructor(t, new[] { typeof(string) });
                _user = AccessTools.Field(t, "user"); _name = AccessTools.Field(t, "EventName"); _state = AccessTools.Field(t, "EventState");
                _car = AccessTools.Field(t, "CarIdx");   // (solo en las New Year 174 y 175)
                if (ctor == null || _user == null || _name == null || _state == null) return 0;
                h.Patch(ctor, prefix: new HarmonyMethod(typeof(Eventos), nameof(Before)));
                return 1;
            }
            catch (Exception ex) { log?.Invoke("eventos de otras versiones: " + ex.GetType().Name + " " + ex.Message); return 0; }
        }

        // true = lo lee Open Rails como siempre (es su formato, o no es ninguno de los dos)
        static bool Before(object __instance, string m)
        {
            try
            {
                if (m == null) return true;
                var a = m.Split(' ');
                int mine = _car != null ? 4 : 3;
                if (a.Length == mine || (a.Length != 3 && a.Length != 4)) return true;
                if (!int.TryParse(a[a.Length - 1].Trim(), out int state)) return true;
                int car = -1;
                if (a.Length == 4 && !int.TryParse(a[2].Trim(), out car)) return true;
                _user.SetValue(__instance, a[0].Trim());
                _name.SetValue(__instance, a[1].Trim());
                _state.SetValue(__instance, state);
                _car?.SetValue(__instance, car);
                if (_seen++ == 0) _log?.Invoke($"eventos de otra versión de Open Rails: entendidos ({a.Length} datos, p. ej. «{a[1].Trim()}» de «{a[0].Trim()}»)");
                return false;
            }
            catch { return true; }
        }
    }
}
