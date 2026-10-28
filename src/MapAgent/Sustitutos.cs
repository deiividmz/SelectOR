// El tren de otro que te falta, con uno TUYO parecido (1.2.81). Antes, Open Rails sustituía cada vehículo que te faltaba
// por el de largo más parecido de toda tu carpeta de trenes (MPManager.SubCar → SubMissingCar): un automotor podía salir
// hecho de una locomotora de mercancías y tres vagones de otra época. Ahora:
//  · se mira el tren entero que llega (el PLAYER, TRAIN o UPDATETRAIN que se está aplicando: su .con si lo trae, sus
//    vehículos y sus largos) y se busca entre tus composiciones enteras (VehiculosCache.Consists: los .con de
//    TRAINS\CONSISTS con todos sus vehículos en tu carpeta) la más parecida: mismo número de coches y largo, locomotoras en
//    el mismo sitio (cabeza, cola, las dos: automotor, reversible, locomotora sola), misma tracción y viajeros o
//    mercancías (lo que se sepa por los vehículos que sí tienes) y nombres parecidos («449», «Civia», «Talgo»…);
//  · cada vehículo que falta se cambia por el de la misma clase de esa composición, en orden (las locomotoras por sus
//    locomotoras, los coches por sus coches); los que tienes, los tuyos;
//  · el mismo vehículo, siempre por el mismo sustituto mientras dure la sesión;
//  · si el sustituto no carga (un .eng roto), se apunta y se prueba otro: antes el tren se quedaba con un coche de menos y
//    Open Rails lo volvía a pedir cada pocos segundos (GETTRAIN/UPDATETRAIN sin fin);
//  · sin pasar por SubCar de Open Rails, que además escribía «Missing car…» en los mensajes de la pantalla desde el hilo de
//    la red (choque con el que los dibuja: NullReferenceException en MessagesWindow, cierre de Open Rails).
// Sin composición parecida (o aún sin la lista), el de largo más parecido de la misma clase; y si no, lo de Open Rails.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using HarmonyLib;
using Orts.MultiPlayer;
using Orts.Simulation.Physics;
using Orts.Simulation.RollingStocks;
using SelectOR.Comun;

namespace SelectOR.MapAgent
{
    public static class Sustitutos
    {
        // el mensaje que se está aplicando (hilo de la red)
        [ThreadStatic] static string[] _cars;
        [ThreadStatic] static int[] _lens;
        [ThreadStatic] static string _con;
        [ThreadStatic] static HashSet<int> _done;
        static readonly ConcurrentDictionary<string, string> _memo = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // vehículo que falta → su sustituto
        static readonly ConcurrentDictionary<string, string[]> _plans = new ConcurrentDictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        static readonly ConcurrentDictionary<string, bool> _bad = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);         // sustitutos que no cargan
        static MethodInfo _load;
        static int _said;
        static readonly bool Off = Environment.GetEnvironmentVariable("SELECTOR_SUSTITUTOS") == "0";

        public static int Install(Harmony h)
        {
            if (Off) return 0;
            int n = 0;
            try
            {
                _load = typeof(RollingStock).GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .Where(m => m.Name == "Load" && m.GetParameters().Length >= 3 && m.GetParameters()[2].ParameterType == typeof(string))
                    .OrderBy(m => m.GetParameters().Length).FirstOrDefault();
                var sub = AccessTools.Method(typeof(MPManager), "SubCar");
                if (sub == null || _load == null) return 0;
                h.Patch(sub, prefix: new HarmonyMethod(typeof(Sustitutos), nameof(BeforeSub)) { priority = Priority.Low });
                n++;
                foreach (var t in new[] { typeof(MSGTrain), typeof(MSGUpdateTrain) })
                {
                    var m = AccessTools.Method(t, "HandleMsg");
                    if (m != null) { h.Patch(m, prefix: new HarmonyMethod(typeof(Sustitutos), nameof(BeforeMsg)), finalizer: new HarmonyMethod(typeof(Sustitutos), nameof(AfterMsg))); n++; }
                }
                var add = AccessTools.Method(typeof(OnlineTrains), "AddPlayers");
                if (add != null) { h.Patch(add, prefix: new HarmonyMethod(typeof(Sustitutos), nameof(BeforePlayer)), finalizer: new HarmonyMethod(typeof(Sustitutos), nameof(AfterMsg))); n++; }
            }
            catch (Exception ex) { Agent.Log("sustitutos: " + ex.GetType().Name + " " + ex.Message); }
            return n;
        }

        static void BeforeMsg(object __instance)
        {
            try
            {
                var tr = Traverse.Create(__instance);
                _cars = tr.Field("cars").GetValue<string[]>(); _lens = tr.Field("lengths").GetValue<int[]>(); _con = null; _done = new HashSet<int>();
            }
            catch { _cars = null; }
        }
        static void BeforePlayer(MSGPlayer player) { try { _cars = player?.cars; _lens = player?.lengths; _con = player?.con; _done = new HashSet<int>(); } catch { _cars = null; } }
        static Exception AfterMsg(Exception __exception) { _cars = null; _lens = null; _con = null; _done = null; return __exception; }

        static string Rel(string path)
        {
            if (path == null) return "";
            int k = path.IndexOf("\\trainset\\", StringComparison.OrdinalIgnoreCase);
            return (k >= 0 ? path.Substring(k + 10) : path).Replace('/', '\\').TrimStart('\\');
        }

        // (después de Remotos.BeforeSub: el largo ya en metros)
        static bool BeforeSub(Train train, string wagonFilePath, int length, ref TrainCar __result)
        {
            try
            {
                var sim = MPManager.Simulator; if (sim == null || train == null) return true;
                string rel = Rel(wagonFilePath);
                bool eng = rel.EndsWith(".eng", StringComparison.OrdinalIgnoreCase);
                var cands = new List<string>();
                if (_memo.TryGetValue(rel, out var m0)) cands.Add(m0);
                // el sitio de este vehículo en el tren que llega
                int idx = -1;
                if (_cars != null)
                    for (int i = 0; i < _cars.Length; i++)
                        if (!_done.Contains(i) && string.Equals(Rel(_cars[i]), rel, StringComparison.OrdinalIgnoreCase)) { idx = i; _done.Add(i); break; }
                if (idx >= 0)
                {
                    var plan = PlanFor(_con, _cars, _lens);
                    if (plan != null && idx < plan.Length && plan[idx] != null) cands.Add(plan[idx]);
                }
                cands.AddRange(ByLength(eng, length, ClassOf(_cars, eng), 4));
                foreach (var c in cands.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (_bad.ContainsKey(c)) continue;
                    string full = System.IO.Path.Combine(sim.BasePath, "trains", "trainset", c);
                    try
                    {
                        var args = new object[_load.GetParameters().Length];
                        args[0] = sim; args[1] = train; args[2] = full;
                        for (int i = 3; i < args.Length; i++) args[i] = _load.GetParameters()[i].HasDefaultValue ? _load.GetParameters()[i].DefaultValue : null;
                        var car = (TrainCar)_load.Invoke(null, args);
                        if (car == null) { _bad[c] = true; continue; }
                        car.CarLengthM = length;
                        car.RealWagFilePath = wagonFilePath;
                        _memo[rel] = c;
                        __result = car;
                        return false;
                    }
                    catch (Exception ex)
                    {
                        _bad[c] = true;
                        if (_said++ < 40) Agent.Log($"sustituto que no carga: {c} ({(ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException.Message : ex.Message)}): se prueba otro");
                    }
                }
            }
            catch (Exception ex) { if (_said++ < 40) Agent.Log("sustitutos: " + ex.GetType().Name + " " + ex.Message); }
            return true;   // (lo de Open Rails)
        }

        // la clase que se sabe del tren que llega por los vehículos que sí tienes: tracción (e/d/s) o viajeros/mercancías (c/f)
        static char ClassOf(string[] cars, bool eng)
        {
            if (cars == null) return '?';
            var info = VehiculosCache.Info;
            foreach (var c in cars)
            {
                string r = Rel(c);
                if (r.EndsWith(".eng", StringComparison.OrdinalIgnoreCase) != eng) continue;
                if (info.TryGetValue(r, out var x) && x.cls != '?') return x.cls;
            }
            return '?';
        }

        // los de largo más parecido (de la misma clase si se sabe), sin los que no cargan
        static IEnumerable<string> ByLength(bool eng, int length, char cls, int max)
        {
            var info = VehiculosCache.Info; if (info.Count == 0) return Enumerable.Empty<string>();
            string ext = eng ? ".eng" : ".wag";
            // (una pasada, guardando los «max» mejores: la lista tiene decenas de miles)
            var top = new List<(double s, string r)>();
            foreach (var kv in info)
            {
                if (kv.Value.len <= 0 || !kv.Key.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) continue;
                double s = (cls != '?' && kv.Value.cls != cls ? 1000 : 0) + Math.Abs(kv.Value.len - length);
                if (top.Count == max && s >= top[max - 1].s) continue;
                if (_bad.ContainsKey(kv.Key)) continue;
                top.Add((s, kv.Key)); top.Sort((a, b) => a.s.CompareTo(b.s));
                if (top.Count > max) top.RemoveAt(max);
            }
            return top.Select(x => x.r).ToList();
        }

        static readonly Regex Word = new Regex(@"[A-Za-z0-9ÁÉÍÓÚÑáéíóúñ]{3,}", RegexOptions.Compiled);
        static readonly HashSet<string> Generic = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "eng", "wag", "con", "renfe", "adif", "pack", "trains", "trainset", "the", "and", "del", "los", "las", "cab", "coche", "car" };
        static HashSet<string> Words(IEnumerable<string> s) => new HashSet<string>(s.Where(x => x != null).SelectMany(x => Word.Matches(x).Cast<Match>().Select(m => m.Value.ToLowerInvariant())).Where(w => !Generic.Contains(w)));

        // La composición tuya más parecida al tren que llega y, para cada uno de sus vehículos, el sustituto
        static string[] PlanFor(string con, string[] cars, int[] lens)
        {
            if (cars == null || cars.Length == 0) return null;
            var cons = VehiculosCache.Consists; var info = VehiculosCache.Info;
            if (cons == null || cons.Count == 0) return null;
            string key = (con ?? "") + "|" + string.Join(";", cars);
            if (_plans.TryGetValue(key, out var done)) return done;
            int n = cars.Length;
            var isEng = cars.Select(c => Rel(c).EndsWith(".eng", StringComparison.OrdinalIgnoreCase)).ToArray();
            double total = 0; for (int i = 0; i < n; i++) total += lens != null && i < lens.Length ? (lens[i] > 100 ? lens[i] / 100.0 : lens[i]) : 20;
            int engs = isEng.Count(x => x);
            bool head = isEng[0], tail = isEng[n - 1];
            char traction = ClassOf(cars, true), kind = ClassOf(cars, false);
            var words = Words(new[] { con }.Concat(cars.Select(c => Rel(c).Replace('\\', ' '))));
            VehiculosCache.Con best = null; double bestS = double.MaxValue;
            foreach (var c in cons)
            {
                int cn = c.Cars.Count; double clen = 0; int ce = 0;
                foreach (var (r, e) in c.Cars) { clen += info.TryGetValue(r, out var x) ? x.len : 20; if (e) ce++; }
                if (ce == 0 && engs > 0) continue;   // (sin locomotoras no sirve para un tren con ellas)
                double s = 3 * Math.Abs(n - cn) + Math.Abs(total - clen) / 10;
                bool ch = c.Cars[0].eng, ct = c.Cars[cn - 1].eng;
                if (Math.Min(ce, 2) != Math.Min(engs, 2) || ch != head || ct != tail) s += 5;
                if (traction != '?')
                {
                    var le = c.Cars.FirstOrDefault(x => x.eng);
                    if (le.rel != null && info.TryGetValue(le.rel, out var lx) && lx.cls != '?' && lx.cls != traction) s += 4;
                }
                if (kind != '?')
                {
                    var lw = c.Cars.FirstOrDefault(x => !x.eng);
                    if (lw.rel != null && info.TryGetValue(lw.rel, out var wx) && wx.cls != '?' && wx.cls != kind) s += 4;
                }
                if (words.Count > 0)
                {
                    var cw = Words(new[] { c.Name }.Concat(c.Cars.Select(x => x.rel.Replace('\\', ' '))));
                    s -= 6.0 * words.Count(w => cw.Contains(w)) / words.Count;
                }
                if (s < bestS) { bestS = s; best = c; }
            }
            if (best == null) { _plans[key] = null; return null; }
            var be = best.Cars.Where(x => x.eng).Select(x => x.rel).ToList();
            var bw = best.Cars.Where(x => !x.eng).Select(x => x.rel).ToList();
            var plan = new string[n]; int ke = 0, kw = 0;
            for (int i = 0; i < n; i++)
            {
                if (isEng[i]) plan[i] = be.Count > 0 ? be[ke++ % be.Count] : null;
                else plan[i] = bw.Count > 0 ? bw[kw++ % bw.Count] : null;
            }
            _plans[key] = plan;
            Agent.Log($"tren que falta{(string.IsNullOrEmpty(con) ? "" : " («" + con + "»)")} ({n} vehículos, {total:F0} m): se sustituye con tu composición «{best.Name}» ({best.Cars.Count} vehículos; puntos {bestS:F1})");
            return plan;
        }
    }
}
