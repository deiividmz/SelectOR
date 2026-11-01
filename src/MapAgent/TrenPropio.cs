// Tu tren en la sala (1.2.82). Varios jugadores: en la sala «no se oye el tren, las luces de fuera no están en su sitio y
// no deja moverlo» (en Conducción libre, todo bien). Eso es lo que pasa si Open Rails cambia los coches de TU tren en
// marcha (los coches nuevos no tienen su sonido ni sus luces puestas y la cabina se queda con la locomotora de antes), y
// en multijugador hay dos sitios donde lo puede hacer con el tren equivocado:
//  · UPDATETRAIN (la respuesta a un GETTRAIN que pidió tu Open Rails por un tren de otro que no le cuadraba): busca el tren
//    por su NÚMERO entre todos los tuyos y le pone los coches que llegan. Si tu tren tiene ese número, los pone en el tuyo.
//  · MOVE: la posición de cada tren IA («0xAI») la aplica al tren con ese número, también si es el tuyo; y si sus coches no
//    cuadran, pide el tren (GETTRAIN) y llega el UPDATETRAIN de arriba.
// Aquí: tu tren nunca se toca por un mensaje de otro tren (se apunta en el registro del agente), y cada 2 s se mira si
// algo de tu tren ha cambiado sin que tú lo hayas hecho (número, tipo, coches, locomotora de cabeza): queda apuntado con
// la hora, para saber qué ha pasado si vuelve a ocurrir.
// Con Harmony, en memoria (no cambia ningún archivo de Open Rails). Todo por nombre (las versiones de Open Rails cambian).

using System;
using System.Collections;
using System.Diagnostics;
using HarmonyLib;
using Orts.MultiPlayer;
using Orts.Simulation.Physics;
using Orts.Simulation.RollingStocks;

namespace SelectOR.MapAgent
{
    public static class TrenPropio
    {
        static readonly Stopwatch _clock = Stopwatch.StartNew();
        static long _next;
        static int _said, _changes;
        static readonly System.Collections.Generic.HashSet<string> _told = new System.Collections.Generic.HashSet<string>();   // (un aviso por tren)
        static readonly bool Off = Environment.GetEnvironmentVariable("SELECTOR_TREN_PROPIO") == "0";   // (sin las protecciones; la vigilancia sigue)

        // (desde Agent.Install) cuántos parches ha puesto.
        public static int Install(Harmony h)
        {
            if (Off) return 0;
            int n = 0;
            try
            {
                var upd = AccessTools.Method(typeof(MSGUpdateTrain), "HandleMsg");
                if (upd != null) { h.Patch(upd, prefix: new HarmonyMethod(typeof(TrenPropio), nameof(BeforeUpdate))); n++; }
                var mv = AccessTools.Method(typeof(MSGMove), "HandleMsg");
                if (mv != null) { h.Patch(mv, prefix: new HarmonyMethod(typeof(TrenPropio), nameof(BeforeMove))); n++; }
            }
            catch (Exception ex) { Agent.Log("tu tren: " + ex.GetType().Name + " " + ex.Message); }
            return n;
        }

        static Train Own()
        {
            try { return MPManager.IsClient() ? MPManager.Simulator?.PlayerLocomotive?.Train : null; } catch { return null; }
        }
        static bool Mine(Train t) => t != null && t.TrainType != Train.TRAINTYPE.REMOTE;

        // UPDATETRAIN con el número de tu tren: no se aplica
        static bool BeforeUpdate(object __instance)
        {
            try
            {
                var own = Own(); if (!Mine(own)) return true;
                var tr = Traverse.Create(__instance);
                int num = tr.Field("TrainNum").GetValue<int>();
                if (num != own.Number) return true;
                if (_told.Add("u" + num) && _said++ < 30) Agent.Log($"tu tren: llegan coches para el tren nº{num}, que en tu Open Rails es el tuyo; no se cambian (Open Rails los ponía en el tuyo)");
                return false;
            }
            catch { return true; }
        }

        // MOVE: los trenes IA (o sueltos) con el número de tu tren se quitan de la lista antes de que Open Rails la aplique
        static void BeforeMove(object __instance)
        {
            try
            {
                var own = Own(); if (!Mine(own)) return;
                var items = Traverse.Create(__instance).Field("items").GetValue() as IList;
                if (items == null) return;
                for (int i = items.Count - 1; i >= 0; i--)
                {
                    var it = Traverse.Create(items[i]);
                    string user = it.Field("user").GetValue<string>() ?? "";
                    if (!(user.Contains("0xAI") || user.Contains("0xUC"))) continue;
                    if (it.Field("num").GetValue<int>() != own.Number) continue;
                    if (_told.Add(user + "|" + own.Number) && _said++ < 30) Agent.Log($"tu tren: la posición de un tren de la sala ({user}) llega con el nº{own.Number}, el de tu tren; no se aplica al tuyo (y no se volverá a avisar de este)");
                    items.RemoveAt(i);
                }
            }
            catch { }
        }

        // ── Vigilancia (cada 2 s, desde Agent.AfterUpdate): ¿ha cambiado tu tren sin que lo hayas hecho tú?
        static Train _t; static object _cars, _first, _lead; static int _num = int.MinValue, _count = -1; static Train.TRAINTYPE _type;
        public static void Check()
        {
            long now = _clock.ElapsedMilliseconds;
            if (now < _next) return;
            _next = now + 2000;
            try
            {
                var own = Own();
                if (own == null) return;
                object cars = own.Cars, first = own.Cars != null && own.Cars.Count > 0 ? own.Cars[0] : null, lead = own.LeadLocomotive;
                if (_t == null)
                {
                    Agent.Log($"tu tren: nº{own.Number}, {own.Cars?.Count ?? 0} coches, {own.TrainType}, cabeza {(own.LeadLocomotive as TrainCar)?.CarID ?? "—"}");
                }
                else
                {
                    var why = new System.Collections.Generic.List<string>();
                    if (!ReferenceEquals(own, _t)) why.Add("es otro tren");
                    if (own.Number != _num) why.Add($"nº{_num} → nº{own.Number}");
                    if (own.TrainType != _type) why.Add($"{_type} → {own.TrainType}");
                    if (!ReferenceEquals(cars, _cars) || (own.Cars?.Count ?? 0) != _count || !ReferenceEquals(first, _first)) why.Add($"coches cambiados ({_count} → {own.Cars?.Count ?? 0})");
                    if (!ReferenceEquals(lead, _lead)) why.Add($"locomotora de cabeza: {(_lead as TrainCar)?.CarID ?? "—"} → {(lead as TrainCar)?.CarID ?? "—"}");
                    if (why.Count > 0 && _changes++ < 60) Agent.Log("tu tren ha cambiado: " + string.Join(", ", why));
                }
                _t = own; _cars = cars; _first = first; _lead = lead; _num = own.Number; _count = own.Cars?.Count ?? 0; _type = own.TrainType;
            }
            catch { }
        }
    }
}
