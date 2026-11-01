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
// (1.2.82) EL TREN ENTERO: antes solo se cambiaban los vehículos que faltaban y se quedaban los que sí tenías, y un mismo
// vehículo se cambiaba siempre por el mismo sustituto aunque fuera de otro tren: salían formaciones mezcladas (media 447 con
// coches de 440, cabinas en mitad del tren). Ahora, si al tren que llega le falta algún vehículo, antes de montarlo se cambia
// su lista ENTERA por tu composición parecida: todos sus vehículos, en su orden y girados como en tu .con (y, si el tren es
// más largo que tu composición y cabe un número entero de veces, la unidad repetida: doble o triple composición). Se quedan
// el número de vehículos, sus identificadores y largos (así no se descuadra el multijugador), y cada coche guarda el
// vehículo que era de verdad (RealWagFilePath). SELECTOR_SUSTITUTOS=1 vuelve a cambiar solo los que faltan.

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
        [ThreadStatic] static string[] _orig;      // (1.2.82) lo que era cada vehículo antes de cambiar el tren entero
        [ThreadStatic] static int _origNum;
        [ThreadStatic] static string _origUser;
        static readonly bool PerCar = Environment.GetEnvironmentVariable("SELECTOR_SUSTITUTOS") == "1";
        static readonly ConcurrentDictionary<string, bool> _wholeSaid = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
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
            _orig = null;
            try
            {
                var tr = Traverse.Create(__instance);
                _cars = tr.Field("cars").GetValue<string[]>(); _lens = tr.Field("lengths").GetValue<int[]>(); _con = null; _done = new HashSet<int>();
                int num = tr.Field("TrainNum").GetValue<int>();
                // UPDATETRAIN de un tren que ya está (el tuyo, al desacoplar): sus coches siguen como estaban
                bool exists = __instance is MSGUpdateTrain && MPManager.Simulator?.Trains?.Any(t => t != null && t.Number == num) == true;
                if (!exists) Whole(_cars, tr.Field("flipped").GetValue<int[]>(), _lens, null, num, null);
            }
            catch { _cars = null; }
        }
        static void BeforePlayer(MSGPlayer player)
        {
            _orig = null;
            try { _cars = player?.cars; _lens = player?.lengths; _con = player?.con; _done = new HashSet<int>(); Whole(_cars, player?.flipped, _lens, _con, -1, player?.user); }
            catch { _cars = null; }
        }
        static Exception AfterMsg(Exception __exception)
        {
            try { if (_orig != null) RestoreReal(); } catch { }
            _cars = null; _lens = null; _con = null; _done = null; _orig = null; return __exception;
        }

        // (1.2.82) le falta algún vehículo → la lista entera, de tu composición parecida (en el mismo mensaje: Open Rails la
        // monta después ya con vehículos que tienes)
        static void Whole(string[] cars, int[] flipped, int[] lens, string con, int num, string user)
        {
            if (PerCar || cars == null || flipped == null || cars.Length == 0 || flipped.Length != cars.Length) return;
            var sim = MPManager.Simulator; if (sim == null) return;
            string root = System.IO.Path.Combine(sim.BasePath, "trains", "trainset");
            bool missing = false;
            foreach (var c in cars) if (!System.IO.File.Exists(System.IO.Path.Combine(root, Rel(c)))) { missing = true; break; }
            if (!missing) return;
            var (best, score) = Best(con, cars, lens);
            if (best == null) return;   // sin composición parecida: vehículo a vehículo (BeforeSub)
            int n = cars.Length, cn = best.Cars.Count;
            var map = Map(n, cn, best);
            // el tren que llega va al revés (su primer vehículo girado y en tu composición no, o al contrario): todo girado
            bool flips = best.Flips.Count == cn;
            bool f0 = flips && best.Flips[map[0]];
            bool rev = (flipped[0] != 0) != f0 && Rel(cars[0]).EndsWith(".eng", StringComparison.OrdinalIgnoreCase) == best.Cars[map[0]].eng;
            var orig = (string[])cars.Clone();
            for (int i = 0; i < n; i++)
            {
                cars[i] = best.Cars[map[i]].rel;
                flipped[i] = ((flips && best.Flips[map[i]]) ^ rev) ? 1 : 0;
            }
            _orig = orig; _origNum = num; _origUser = user;
            string key = (con ?? "") + "|" + string.Join(";", orig);
            if (_wholeSaid.TryAdd(key, true))
                Agent.Log($"tren que falta{(string.IsNullOrEmpty(con) ? "" : " («" + con + "»)")} ({n} vehículos): entero con tu composición «{best.Name}» ({cn} vehículos{(n != cn ? (n % cn == 0 ? $", ×{n / cn}" : ", ajustada") : "")}{(rev ? ", al revés" : "")}; puntos {score:F1})");
        }

        // qué vehículo de la composición va en cada sitio del tren (n vehículos con una composición de cn)
        internal static int[] Map(int n, int cn, VehiculosCache.Con c)
        {
            var m = new int[n];
            if (cn == n || n % cn == 0) { for (int i = 0; i < n; i++) m[i] = i % cn; return m; }
            if (cn > n) { for (int i = 0; i < n - 1; i++) m[i] = i; m[n - 1] = cn - 1; return m; }   // (más corto: con su cola)
            // más largo: la cabeza, los del medio repetidos (los coches, si los hay) y la cola
            var mid = new List<int>(); for (int i = 1; i < cn - 1; i++) mid.Add(i);
            var midW = mid.Where(i => !c.Cars[i].eng).ToList(); if (midW.Count > 0) mid = midW;
            if (mid.Count == 0) mid.Add(cn > 1 ? cn - 1 : 0);
            m[0] = 0; m[n - 1] = cn - 1;
            for (int i = 1, k = 0; i < n - 1; i++, k++) m[i] = mid[k % mid.Count];
            return m;
        }

        // cada coche, con el vehículo que era de verdad (por si se acopla y se vuelve a mandar)
        static void RestoreReal()
        {
            var sim = MPManager.Simulator; if (sim == null) return;
            Train t = null;
            if (_origUser != null) { try { var ps = MPManager.OnlineTrains?.Players; if (ps != null && ps.TryGetValue(_origUser, out var op)) t = op?.Train; } catch { } }
            else t = sim.Trains?.FirstOrDefault(x => x != null && x.Number == _origNum);
            if (t == null || t.Cars == null || t.Cars.Count != _orig.Length) return;
            string root = System.IO.Path.Combine(sim.BasePath, "trains", "trainset");
            for (int i = 0; i < _orig.Length; i++) t.Cars[i].RealWagFilePath = System.IO.Path.Combine(root, Rel(_orig[i]));
        }

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
        static (VehiculosCache.Con best, double score) Best(string con, string[] cars, int[] lens)
        {
            var cons = VehiculosCache.Consists; var info = VehiculosCache.Info;
            if (cars == null || cars.Length == 0 || cons == null || cons.Count == 0) return (null, 0);
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
                // (1.2.82) una unidad que cabe un número entero de veces (doble o triple composición) casi como la misma
                int k = n != cn && n % cn == 0 && n / cn <= 4 ? n / cn : 1;
                double s = (k > 1 ? 1.0 * (k - 1) : 3 * Math.Abs(n - cn)) + Math.Abs(total - clen * k) / 10;
                // (1.2.82) una composición de varias libreas (carpetas) se ve mezclada: mejor una unidad de una sola, repetida
                int folders = c.Cars.Select(x => { int b = x.rel.LastIndexOf('\\'); return b > 0 ? x.rel.Substring(0, b) : ""; }).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                if (folders > 1) s += 1.5 * (folders - 1);
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
            return (best, bestS);
        }

        static string[] PlanFor(string con, string[] cars, int[] lens)
        {
            if (cars == null || cars.Length == 0) return null;
            string key = (con ?? "") + "|" + string.Join(";", cars);
            if (_plans.TryGetValue(key, out var done)) return done;
            int n = cars.Length;
            var isEng = cars.Select(c => Rel(c).EndsWith(".eng", StringComparison.OrdinalIgnoreCase)).ToArray();
            var (best, bestS) = Best(con, cars, lens);
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
            Agent.Log($"tren que falta{(string.IsNullOrEmpty(con) ? "" : " («" + con + "»)")} ({n} vehículos): vehículo a vehículo con tu composición «{best.Name}» ({best.Cars.Count} vehículos; puntos {bestS:F1})");
            return plan;
        }
    }
}
