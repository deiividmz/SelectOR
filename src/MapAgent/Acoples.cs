// Acoples y desacoples de los trenes de los demás, en tu Open Rails (multijugador; 1.2.77).
// Al reacoplar los vagones sueltos, a algún jugador se le quedaba el tren de otro sin ver. Open Rails busca los dos trenes
// del «COUPLE» por su número y, si no los encuentra tal cual, tira el acople sin decir nada (o falla con un error):
//  · el tren recién llegado aún no está en la lista (Open Rails los mete en el siguiente ciclo, MPManager.HandleTrainList);
//  · la lista cambia mientras se recorre (el acople llega por el hilo de red, la lista la usa la simulación);
//  · los números no cuadran o hay dos trenes con el mismo número o los mismos coches (uno de ellos no se ve).
// Aquí:
//  · cada TRAIN, COUPLE y UNCOUPLE que llega queda en el registro del agente (números, coches y lo que hizo Open Rails);
//  · los trenes del acople se buscan por los identificadores de sus coches, no solo por el número;
//  · si un tren aún no está en la lista o la lista estaba cambiando, el acople se repite en el ciclo siguiente;
//  · los trenes repetidos (los mismos coches en otro tren) se quitan, y no se crea otro tren con coches que ya hay.
// Con Harmony, en memoria (no cambia ningún archivo de Open Rails).

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Orts.MultiPlayer;
using Orts.Simulation.Physics;

namespace SelectOR.MapAgent
{
    public static class Acoples
    {
        static FieldInfo _cIds, _cNum, _cRem, _cLead, _cWho;     // MSGCouple
        static FieldInfo _tIds, _tNum;                           // MSGTrain
        static FieldInfo _added, _removed;                       // MPManager (trenes que entran / salen en el siguiente ciclo)
        static readonly object _lock = new object();
        static readonly List<object> _later = new List<object>();                    // acoples que se repiten en el siguiente ciclo
        static readonly Dictionary<object, int> _tries = new Dictionary<object, int>();
        const int MaxTries = 6;

        // Números cambiados un momento para que Open Rails encuentre los trenes buenos (se devuelven al acabar).
        sealed class State { public List<(Train t, int n)> Restore = new List<(Train, int)>(); public bool Ours; }

        // (desde Agent.Install) cuántos parches ha puesto.
        public static int Install(Harmony h)
        {
            int n = 0;
            try
            {
                var c = typeof(MSGCouple);
                _cIds = AccessTools.Field(c, "ids"); _cNum = AccessTools.Field(c, "TrainNum"); _cRem = AccessTools.Field(c, "RemovedTrainNum");
                _cLead = AccessTools.Field(c, "Lead"); _cWho = AccessTools.Field(c, "whoControls");
                _added = AccessTools.Field(typeof(MPManager), "addedTrains"); _removed = AccessTools.Field(typeof(MPManager), "removedTrains");
                var hc = AccessTools.Method(c, "HandleMsg");
                if (hc != null && _cIds != null && _cNum != null && _cRem != null)
                {
                    h.Patch(hc, prefix: new HarmonyMethod(typeof(Acoples), nameof(BeforeCouple)), postfix: new HarmonyMethod(typeof(Acoples), nameof(AfterCouple)),
                            finalizer: new HarmonyMethod(typeof(Acoples), nameof(CoupleFailed)));
                    n++;
                }
                var htl = AccessTools.Method(typeof(MPManager), "HandleTrainList");
                if (htl != null) { h.Patch(htl, postfix: new HarmonyMethod(typeof(Acoples), nameof(AfterTrainList))); n++; }

                var t = typeof(MSGTrain);
                _tIds = AccessTools.Field(t, "ids"); _tNum = AccessTools.Field(t, "TrainNum");
                var ht = AccessTools.Method(t, "HandleMsg");
                if (ht != null && _tIds != null && _tNum != null)
                {
                    h.Patch(ht, prefix: new HarmonyMethod(typeof(Acoples), nameof(BeforeTrain)), finalizer: new HarmonyMethod(typeof(Acoples), nameof(TrainFailed)));
                    n++;
                }

                var hu = AccessTools.Method(typeof(MSGUncouple), "HandleMsg");
                if (hu != null)
                {
                    h.Patch(hu, prefix: new HarmonyMethod(typeof(Acoples), nameof(BeforeUncouple)), postfix: new HarmonyMethod(typeof(Acoples), nameof(AfterUncouple)),
                            finalizer: new HarmonyMethod(typeof(Acoples), nameof(UncoupleFailed)));
                    n++;
                }
            }
            catch (Exception ex) { Agent.Log("acoples: " + ex.GetType().Name + " " + ex.Message); }
            return n;
        }

        // ── Listas de trenes ────────────────────────────────────────────────────────────────────────────────────────

        // Copia de la lista de la simulación (reintenta si cambia mientras se copia), sin los que van a salir.
        static List<Train> Live()
        {
            var removed = Pending(_removed);
            for (int i = 0; i < 8; i++)
            {
                try
                {
                    var l = new List<Train>(MPManager.Simulator.Trains);
                    if (removed.Count > 0) l.RemoveAll(removed.Contains);
                    return l;
                }
                catch (InvalidOperationException) { }
                catch (ArgumentException) { }
            }
            return new List<Train>();
        }

        static List<Train> Pending(FieldInfo f)
        {
            try
            {
                if (f?.GetValue(MPManager.Instance()) is List<Train> l) lock (l) return new List<Train>(l);
            }
            catch { }
            return new List<Train>();
        }

        static string Ids(Train t) => t?.Cars == null ? "" : string.Join(",", t.Cars.Select(c => c?.CarID));
        static string Desc(Train t) => t == null ? "—" : $"nº{t.Number} ({t.Cars?.Count ?? 0} coches, {t.TrainType})";
        static string Desc(IEnumerable<Train> l) { var a = l.Select(Desc).ToArray(); return a.Length == 0 ? "ninguno" : string.Join(" + ", a); }
        static string Span(string[] ids) => ids == null || ids.Length == 0 ? "" : ids.Length == 1 ? ids[0] : $"{ids[0]} … {ids[ids.Length - 1]}";

        static void Later(object msg, string why)
        {
            int n;
            lock (_lock)
            {
                _tries.TryGetValue(msg, out n); n++;
                if (n > MaxTries) { _tries.Remove(msg); Agent.Log($"   acople descartado tras {MaxTries} intentos ({why})"); return; }
                _tries[msg] = n;
                if (!_later.Contains(msg)) _later.Add(msg);
            }
            Agent.Log($"   acople aplazado al siguiente ciclo ({why}; intento {n})");
        }

        static void Done(object msg) { lock (_lock) _tries.Remove(msg); }

        // (hilo de la simulación, justo después de meter y sacar trenes de la lista) los acoples aplazados.
        static void AfterTrainList()
        {
            object[] todo;
            lock (_lock) { if (_later.Count == 0) return; todo = _later.ToArray(); _later.Clear(); }
            foreach (var m in todo)
                try { ((Message)m).HandleMsg(); } catch (Exception ex) { Agent.Log("acople aplazado: " + ex.GetType().Name + " " + ex.Message); }
        }

        // ── COUPLE ──────────────────────────────────────────────────────────────────────────────────────────────────

        static bool BeforeCouple(object __instance, out State __state)
        {
            __state = new State();
            try
            {
                if (MPManager.IsServer()) return true;
                var ids = _cIds.GetValue(__instance) as string[] ?? new string[0];
                int num = (int)_cNum.GetValue(__instance), rem = (int)_cRem.GetValue(__instance);
                int lead = _cLead != null ? (int)_cLead.GetValue(__instance) : -1;
                string who = _cWho?.GetValue(__instance) as string;
                var live = Live();
                var added = Pending(_added);
                var byNum = live.Where(t => t.Number == num).ToList();
                var byRem = live.Where(t => t.Number == rem).ToList();
                var want = new HashSet<string>(ids);
                // quién tiene cada coche (puede haber más de uno: tren repetido)
                var holders = live.Where(t => t.Cars != null && t.Cars.Any(c => c != null && want.Contains(c.CarID))).ToList();
                var pendingHolders = added.Where(t => t.Cars != null && t.Cars.Any(c => c != null && want.Contains(c.CarID))).ToList();

                bool first; lock (_lock) first = !_tries.ContainsKey(__instance);
                if (first)
                    Agent.Log($"COUPLE {num}+{rem} de «{who}» ({ids.Length} coches: {Span(ids)}); por número: {Desc(byNum)} / {Desc(byRem)}; con esos coches: {Desc(holders)}"
                              + (pendingHolders.Count > 0 ? $"; aún entrando: {Desc(pendingHolders)}" : ""));

                // Un tren del acople aún no está en la lista: en el siguiente ciclo ya estará.
                if (pendingHolders.Count > 0 || (byNum.Count == 0 && added.Any(t => t.Number == num)) || (byRem.Count == 0 && added.Any(t => t.Number == rem)))
                {
                    Later(__instance, "un tren aún no está en la lista"); return false;
                }
                if (holders.Count == 0)
                {
                    Done(__instance);
                    Agent.Log("   ningún tren tiene esos coches: no se puede acoplar aquí"); return false;
                }

                // ¿Cada coche está en un solo tren, y todos en los dos del mensaje? Entonces Open Rails lo hace bien.
                bool dup = ids.Any(id => holders.Count(t => t.Cars.Any(c => c?.CarID == id)) > 1);
                if (!dup && byNum.Count == 1 && byRem.Count == 1 && byNum[0] != byRem[0] && holders.All(t => t == byNum[0] || t == byRem[0]))
                    return true;

                // Si no, se eligen los trenes buenos por sus coches.
                Train target = null;
                if (lead >= 0 && lead < ids.Length) target = holders.FirstOrDefault(t => t.Number == num && t.Cars.Any(c => c?.CarID == ids[lead]))
                                                           ?? holders.FirstOrDefault(t => t.Cars.Any(c => c?.CarID == ids[lead]));
                if (target == null) target = holders.FirstOrDefault(t => t.Number == num);
                if (target == null) target = holders.OrderByDescending(t => t.Cars.Count(c => c != null && want.Contains(c.CarID))).First();
                var taken = new HashSet<string>(target.Cars.Where(c => c != null).Select(c => c.CarID));
                // el resto, por orden: el del número que se quita, luego los que más coches aporten
                var donors = new List<Train>();
                var stale = new List<Train>();
                foreach (var t in holders.Where(x => x != target).OrderByDescending(x => x.Number == rem).ThenByDescending(x => x.Cars.Count(c => c != null && want.Contains(c.CarID))))
                {
                    var adds = t.Cars.Where(c => c != null && want.Contains(c.CarID) && !taken.Contains(c.CarID)).ToList();
                    if (adds.Count == 0)   // solo coches que ya están en otro: tren repetido
                    {
                        if (t.Cars.All(c => c == null || want.Contains(c.CarID))) stale.Add(t);
                        else Agent.Log($"   {Desc(t)} repite coches del acople pero lleva otros: se deja");
                        continue;
                    }
                    foreach (var c in adds) taken.Add(c.CarID);
                    donors.Add(t);
                }
                foreach (var t in stale) { Agent.Log($"   tren repetido {Desc(t)} [{Ids(t)}]: se quita"); MPManager.Instance().AddOrRemoveTrain(t, false); }

                if (donors.Count == 0)
                {
                    Done(__instance);
                    if (target.Number != num) { Agent.Log($"   ya acoplado aquí; nº{target.Number} → nº{num}"); target.Number = num; }
                    else Agent.Log("   ya acoplado aquí");
                    return false;
                }

                // Más de un tren aporta coches: se juntan en el primero (Open Rails solo sabe de dos).
                var donor = donors[0];
                foreach (var extra in donors.Skip(1))
                {
                    var moved = extra.Cars.Where(c => c != null && want.Contains(c.CarID) && !donor.Cars.Contains(c)).ToList();
                    foreach (var c in moved) { donor.Cars.Add(c); c.Train = donor; }
                    Agent.Log($"   {moved.Count} coches de {Desc(extra)} se juntan con nº{donor.Number}");
                    if (extra.Cars.All(c => c == null || want.Contains(c.CarID))) MPManager.Instance().AddOrRemoveTrain(extra, false);
                    else { extra.Cars.RemoveAll(moved.Contains); try { extra.CalculatePositionOfCars(); } catch { } }
                }

                // Números: el bueno con el del mensaje; los demás que lo tuvieran, apartados un momento.
                foreach (var t in live)
                    if (t != target && t != donor && (t.Number == num || t.Number == rem)) { __state.Restore.Add((t, t.Number)); t.Number = int.MinValue + __state.Restore.Count; }
                if (target.Number != num) Agent.Log($"   nº{target.Number} → nº{num} (el del acople)");
                if (donor.Number != rem) Agent.Log($"   nº{donor.Number} → nº{rem} (el que se acopla)");
                target.Number = num; donor.Number = rem;
                __state.Ours = true;
                return true;
            }
            catch (InvalidOperationException) { Later(__instance, "la lista de trenes estaba cambiando"); return false; }
            catch (Exception ex) { Agent.Log("   acople (agente): " + ex.GetType().Name + " " + ex.Message); return true; }
        }

        static void AfterCouple(object __instance, State __state, bool __runOriginal)
        {
            if (__state != null) foreach (var (t, n) in __state.Restore) t.Number = n;
            if (!__runOriginal) return;
            try
            {
                if (MPManager.IsServer()) return;
                Done(__instance);
                var ids = _cIds.GetValue(__instance) as string[] ?? new string[0];
                int num = (int)_cNum.GetValue(__instance);
                string who = _cWho?.GetValue(__instance) as string;
                var live = Live();
                var train = live.FirstOrDefault(t => t.Number == num);
                if (train == null) { Agent.Log($"   COUPLE {num}: no hay tren con ese número después del acople"); return; }
                var have = new HashSet<string>(train.Cars.Where(c => c != null).Select(c => c.CarID));
                int missing = ids.Count(id => !have.Contains(id));
                // los mismos coches en otro tren: ese no se ve (o se ve dos veces); fuera
                var want = new HashSet<string>(ids);
                foreach (var t in live.Where(x => x != train && x.Cars != null && x.Cars.Count > 0 && x.Cars.All(c => c != null && want.Contains(c.CarID))))
                {
                    Agent.Log($"   tren repetido tras el acople {Desc(t)}: se quita");
                    MPManager.Instance().AddOrRemoveTrain(t, false);
                }
                string player = "";
                if (!string.IsNullOrEmpty(who) && who != "NA" && MPManager.OnlineTrains?.Players != null && MPManager.OnlineTrains.Players.TryGetValue(who, out var p))
                    player = p?.Train == train ? $"; «{who}» lo lleva" : $"; «{who}» lleva OTRO tren ({Desc(p?.Train)})";
                Agent.Log($"   acoplado{(__state?.Ours == true ? " (por los coches)" : "")}: {Desc(train)}{(missing > 0 ? $", faltan {missing} coches" : "")}{player}");
            }
            catch (Exception ex) { Agent.Log("   acople (registro): " + ex.GetType().Name + " " + ex.Message); }
        }

        // Un error de Open Rails en el acople: queda apuntado y se repite en el siguiente ciclo (sin cortar los mensajes
        // que venían detrás).
        static Exception CoupleFailed(object __instance, Exception __exception, State __state)
        {
            if (__exception == null) return null;
            try
            {
                if (__state != null) foreach (var (t, n) in __state.Restore) t.Number = n;
                Agent.Log($"   COUPLE: Open Rails falla: {__exception.GetType().Name} {__exception.Message}");
                Later(__instance, "error de Open Rails");
            }
            catch { }
            return null;
        }

        // ── TRAIN ───────────────────────────────────────────────────────────────────────────────────────────────────

        // Un tren que ya tienes (los mismos coches) no se crea otra vez: quedaban dos, y uno no se veía.
        static bool BeforeTrain(object __instance)
        {
            try
            {
                if (MPManager.IsServer()) return true;
                var ids = _tIds.GetValue(__instance) as string[] ?? new string[0];
                int num = (int)_tNum.GetValue(__instance);
                var all = Live().Concat(Pending(_added)).ToList();
                var want = new HashSet<string>(ids);
                var holders = all.Where(t => t.Cars != null && t.Cars.Any(c => c != null && want.Contains(c.CarID))).ToList();
                var same = holders.FirstOrDefault(t => ids.Length > 0 && t.Cars.Count == ids.Length && t.Cars.All(c => c != null && want.Contains(c.CarID)));
                if (same != null)
                {
                    string ren = "";
                    if (same.Number != num && !all.Any(t => t != same && t.Number == num)) { ren = $"; nº{same.Number} → nº{num}"; same.Number = num; }
                    Agent.Log($"TRAIN {num} ({ids.Length} coches: {Span(ids)}): ya lo tienes ({Desc(same)}), no se crea otro{ren}");
                    return false;
                }
                // (1.2.81) el mismo número con parte de esos coches (le faltaba alguno que no se pudo sustituir): no otro tren encima
                var part = all.FirstOrDefault(t => t.Number == num && t.Cars != null && t.Cars.Count > 0 && t.Cars.Count < ids.Length && t.Cars.All(c => c != null && want.Contains(c.CarID)));
                if (part != null)
                {
                    Agent.Log($"TRAIN {num} ({ids.Length} coches: {Span(ids)}): ya está ({Desc(part)}, con {part.Cars.Count} de sus coches), no se crea otro encima");
                    return false;
                }
                Agent.Log($"TRAIN {num} ({ids.Length} coches: {Span(ids)})"
                          + (holders.Count > 0 ? $"; esos coches ya están en {Desc(holders)}" : "")
                          + (all.Any(t => t.Number == num) ? $"; ya hay un tren nº{num}" : ""));
            }
            catch (Exception ex) { Agent.Log("TRAIN (agente): " + ex.GetType().Name + " " + ex.Message); }
            return true;
        }

        static Exception TrainFailed(object __instance, Exception __exception)
        {
            if (__exception != null) try { Agent.Log($"   TRAIN {_tNum?.GetValue(__instance)}: Open Rails falla: {__exception.GetType().Name} {__exception.Message}"); } catch { }
            return __exception;
        }

        // ── UNCOUPLE ────────────────────────────────────────────────────────────────────────────────────────────────

        // (los campos, por nombre: así no depende de la versión de Open Rails)
        static string U(object m, string f) { try { return Traverse.Create(m).Field(f).GetValue()?.ToString() ?? ""; } catch { return "?"; } }
        static int UN(object m, string f) { try { return Convert.ToInt32(Traverse.Create(m).Field(f).GetValue()); } catch { return int.MinValue; } }

        static void BeforeUncouple(object __instance)
        {
            try
            {
                int o = UN(__instance, "oldTrainNumber"), n = UN(__instance, "newTrainNumber");
                Agent.Log($"UNCOUPLE de «{U(__instance, "user")}» nº{o} / nº{n} (cabezas {U(__instance, "firstCarIDOld")} / {U(__instance, "firstCarIDNew")}); "
                          + $"antes: {Desc(Live().Where(t => t.Number == o || t.Number == n))}");
            }
            catch { }
        }

        static void AfterUncouple(object __instance)
        {
            try
            {
                int o = UN(__instance, "oldTrainNumber"), n = UN(__instance, "newTrainNumber");
                Agent.Log($"   desacoplado: {Desc(Live().Concat(Pending(_added)).Where(t => t.Number == o || t.Number == n))}");
            }
            catch { }
        }

        static Exception UncoupleFailed(Exception __exception)
        {
            if (__exception != null) try { Agent.Log($"   UNCOUPLE: Open Rails falla: {__exception.GetType().Name} {__exception.Message}"); } catch { }
            return __exception;
        }
    }
}
