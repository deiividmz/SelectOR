// Los trenes de los demás jugadores, en tu Open Rails (multijugador; 1.2.66):
//  · Vehículo que falta (el tren de otro lleva un .eng o .wag que tú no tienes): Open Rails busca uno de largo parecido
//    (MPManager.SubCar → SubMissingCar), pero el largo le llega del «PLAYER» EN CENTÍMETROS y lo trata como metros:
//    buscaba uno de 2 000 m, no encontraba ninguno, el tren se quedaba vacío y no lo veías. Aquí, el largo en metros para
//    buscarlo y el vehículo puesto con su largo exacto (lo mismo que el despachador fijo: SelectOR Despachador\Arreglos.cs).
//    Además, el vehículo puesto lleva el identificador del de verdad (1.2.67): Open Rails le dejaba el de serie, «AI», y con
//    varios sustituidos marcaba de cabeza todos los «AI», también un coche, y fallaba al ponerle las luces.
//  · (1.2.81) Los trenes de los demás (y los IA de la sala) no se comprueban «hacia atrás» en tu Open Rails
//    (Train.CheckBackwardClearance): su ruta aquí es solo la de cuando llegaron y, al salirse de ella, Open Rails los ponía
//    fuera de control y rehacía su ruta, que fallaba (NullReferenceException en GenerateValidRoute en cada fotograma desde
//    que hay trenes IA en la sala). Quien los mueve es su dueño (o el despachador): aquí solo se dibujan.
//  · Si aun así Open Rails no puede colocar el tren de otro (le falta todo, o su vía te sale ocupada), queda en el
//    registro del agente con el motivo (OnlineTrains.AddPlayers), para saber por qué no se ve.
// Con Harmony, en memoria (no cambia ningún archivo de Open Rails).

using System;
using HarmonyLib;
using Orts.MultiPlayer;
using Orts.Simulation.RollingStocks;

namespace SelectOR.MapAgent
{
    public static class Remotos
    {
        [ThreadStatic] static int _subs;
        [ThreadStatic] static MSGPlayer _adding;   // el «PLAYER» que se está poniendo

        // (desde Agent.Install) cuántos parches ha puesto.
        public static int Install(Harmony h)
        {
            int n = 0;
            try
            {
                var sub = AccessTools.Method(typeof(MPManager), "SubCar");
                if (sub != null) { h.Patch(sub, prefix: new HarmonyMethod(typeof(Remotos), nameof(BeforeSub)), postfix: new HarmonyMethod(typeof(Remotos), nameof(AfterSub))); n++; }
                var back = AccessTools.Method(typeof(Orts.Simulation.Physics.Train), "CheckBackwardClearance");
                if (back != null) { h.Patch(back, prefix: new HarmonyMethod(typeof(Remotos), nameof(BeforeBackward))); n++; }
                var add = AccessTools.Method(typeof(OnlineTrains), "AddPlayers");
                if (add != null)
                {
                    h.Patch(add, prefix: new HarmonyMethod(typeof(Remotos), nameof(BeforeAdd)), postfix: new HarmonyMethod(typeof(Remotos), nameof(AfterAdd)),
                            finalizer: new HarmonyMethod(typeof(Remotos), nameof(AddFailed)));
                    n++;
                }
            }
            catch (Exception ex) { Agent.Log("trenes de los demás: " + ex.GetType().Name + " " + ex.Message); }
            return n;
        }

        // __state: el largo que llegó (cm) y la posición del vehículo en el tren (los anteriores ya están puestos)
        static void BeforeSub(Orts.Simulation.Physics.Train train, ref int length, out (int len, int index) __state)
        {
            __state = (length, train?.Cars?.Count ?? -1);
            if (length > 100) length = (int)Math.Round(length / 100.0);   // centímetros → metros
        }

        static void AfterSub(TrainCar __result, string wagonFilePath, (int len, int index) __state)
        {
            try
            {
                if (__result == null) { Agent.Log($"vehículo que falta, sin sustituto: {System.IO.Path.GetFileName(wagonFilePath)}"); return; }
                if (__state.len > 100) __result.CarLengthM = __state.len / 100f;
                var p = _adding;
                if (p?.ids != null && __state.index >= 0 && __state.index < p.ids.Length) __result.CarID = p.ids[__state.index];
                _subs++;
                Agent.Log($"vehículo que falta: {System.IO.Path.GetFileName(wagonFilePath)} → {System.IO.Path.GetFileName(__result.WagFilePath)} ({__result.CarLengthM:F1} m)");
            }
            catch { }
        }

        static void BeforeAdd(MSGPlayer player) { _subs = 0; _adding = player; }

        // (1.2.81) en un jugador, los trenes de los demás no se comprueban hacia atrás (ver arriba)
        static bool BeforeBackward(Orts.Simulation.Physics.Train __instance, ref int __result)
        {
            try
            {
                if (__instance.TrainType != Orts.Simulation.Physics.Train.TRAINTYPE.REMOTE || !MPManager.IsClient()) return true;
                __result = 0;
                return false;
            }
            catch { return true; }
        }

        static void AfterAdd(MSGPlayer player)
        {
            try
            {
                if (player == null || MPManager.OnlineTrains?.Players == null || !MPManager.OnlineTrains.Players.TryGetValue(player.user, out var p) || p?.Train == null) return;
                Agent.Log($"tren de «{player.user}»: puesto ({p.Train.Cars.Count} vehículos{(_subs > 0 ? $", {_subs} sustituidos" : "")})");
            }
            catch { }
        }

        // (no se traga el error: Open Rails sigue igual; solo queda apuntado)
        static Exception AddFailed(Exception __exception, MSGPlayer player)
        {
            _adding = null;
            if (__exception != null)
                try { Agent.Log($"tren de «{player?.user}»: Open Rails no lo puede poner: {__exception.GetType().Name} {__exception.Message}"); } catch { }
            return __exception;
        }
    }
}
