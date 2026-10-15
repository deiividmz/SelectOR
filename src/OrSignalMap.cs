// Numeración de las señales de Open Rails (SignalObject.thisRef), con el propio código de Open Rails (Orts.Simulation de
// la carpeta de Open Rails), sin arrancar el simulador: los mismos pasos que hace al cargar la ruta (Signals):
// BuildSignalWorld → BuildSignalList (con MergeHeads) → AddCFG → AddWorldInfo → SplitBackfacing. Así el número de cada
// señal es el mismo que en el Open Rails del despachador (si es la misma versión). Hace falta para pedir un cambio de
// señal por multijugador («SIGNALCHANGE quien número orden»).
// Resultado: para cada elemento de señal del .tdb (TrItem), el número de su señal; null + motivo si no se puede.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

namespace SelectOR
{
    public static class OrSignalMap
    {
        const BindingFlags BF = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        public sealed class Result
        {
            public readonly Dictionary<int, int> ObjectOfItem = new();   // TrItem del .tdb → número de señal (thisRef)
            public int Objects;                                          // señales (sin contar los postes de velocidad)
            // Las cabezas en el orden en que el despachador manda su estado (MSGSignalStatus: elemento·100000 + posición
            // en su tramo, de menor a mayor): el elemento del .tdb y el número de su señal.
            public readonly List<(int item, int obj)> Heads = new();
        }

        public static (Result res, string err) Build(string orDir, string routeDir, string tdbPath, object tdb, object tsec)
        {
            try
            {
                Assembly Load(string name)
                {
                    foreach (var a in AppDomain.CurrentDomain.GetAssemblies()) if (a.GetName().Name == name) return a;
                    return Assembly.LoadFrom(Path.Combine(orDir, name + ".dll"));
                }
                var sim = Load("Orts.Simulation"); var fmt = Load("Orts.Formats.Msts");
                Type T(Assembly a, string n) => a.GetType(n, true);
                var tSim = T(sim, "Orts.Simulation.Simulator"); var tSignals = T(sim, "Orts.Simulation.Signalling.Signals");
                var tSigObj = T(sim, "Orts.Simulation.Signalling.SignalObject"); var tCfg = T(fmt, "Orts.Formats.Msts.SignalConfigurationFile");

                // sigcfg.dat de la ruta (como Open Rails: el de OpenRails\ si lo hay)
                string cfgPath = Path.Combine(routeDir, "OpenRails", "sigcfg.dat");
                bool orts = File.Exists(cfgPath);
                if (!orts) cfgPath = Path.Combine(routeDir, "sigcfg.dat");
                if (!File.Exists(cfgPath)) return (null, "sin sigcfg.dat");
                object cfg = tCfg.GetConstructor(new[] { typeof(string), typeof(bool) })?.Invoke(new object[] { cfgPath, orts })
                             ?? Activator.CreateInstance(tCfg, cfgPath);

                // Simulator «vacío»: solo lo que leen esos pasos
                object s = RuntimeHelpers.GetUninitializedObject(tSim);
                SetAny(s, "RoutePath", routeDir); SetAny(s, "TDB", tdb); SetAny(s, "TSectionDat", tsec);

                object g = RuntimeHelpers.GetUninitializedObject(tSignals);
                InitCollections(g, tSignals);
                SetAny(g, "Simulator", s);
                SetAny(g, "trackDB", Get(tdb, "TrackDB")); SetAny(g, "tsectiondat", tsec); SetAny(g, "tdbfile", tdb);
                var fnsProp = Get(cfg, "SignalFunctions");
                var fnField = tSignals.GetField("SignalFunctions", BF);
                if (fnsProp != null && fnField != null) fnField.SetValue(g, Activator.CreateInstance(fnField.FieldType, fnsProp));
                SetAny(g, "ORTSNormalsubtypes", Get(cfg, "ORTSNormalSubtypes"));

                var trackDB = Get(tdb, "TrackDB");
                var items = Get(trackDB, "TrItemTable"); var nodes = Get(trackDB, "TrackNodes");
                var bw = tSignals.GetMethods(BF).First(x => x.Name == "BuildSignalWorld");
                var ctT = bw.GetParameters()[2].ParameterType;   // System.Threading o el de Open Rails (ORTS.Common)
                object tok = ctT == typeof(CancellationToken) ? CancellationToken.None : NotCancelled(ctT);
                bw.Invoke(g, new[] { s, cfg, tok });
                var buildList = tSignals.GetMethod("BuildSignalList", BF);
                var pars = buildList.GetParameters();
                var args = new object[pars.Length];
                args[0] = items; args[1] = nodes; args[2] = tsec; args[3] = tdb;
                for (int i = 4; i < pars.Length; i++) args[i] = Activator.CreateInstance(pars[i].ParameterType);   // platformList, MilepostList…
                buildList.Invoke(g, args);
                if ((int)tSignals.GetField("foundSignals", BF).GetValue(g) > 0)
                {
                    // AddCFG de Open Rails carga además los scripts de las señales (no hace falta aquí): de él solo cuenta
                    // que una señal cuyas cabezas son todas de velocidad pasa a «SpeedSignal» (y no se separa después)
                    SpeedSignals(objsOf(g, tSignals), cfg, items, tSigObj);
                    Call(g, tSignals, "AddWorldInfo");
                    Call(g, tSignals, "SplitBackfacing", items, nodes);
                }

                var res = new Result();
                var keyed = new List<(long key, int item, int obj)>();
                var objs = (Array)Get(g, "SignalObjects");
                var fType = tSigObj.GetField("Type", BF) ?? (MemberInfo)tSigObj.GetProperty("Type", BF) as FieldInfo;
                foreach (var o in objs)
                {
                    if (o == null) continue;
                    string type = Get(o, "Type")?.ToString() ?? "";
                    if (type != "Signal" && type != "SpeedSignal") continue;
                    int thisRef = Convert.ToInt32(Get(o, "thisRef"));
                    res.Objects++;
                    if (Get(o, "SignalHeads") is IEnumerable heads)
                        foreach (var h in heads)
                        {
                            int item = Convert.ToInt32(Get(h, "TDBIndex"));
                            res.ObjectOfItem[item] = thisRef;
                            keyed.Add(((long)item * 100000 + Convert.ToInt32(Get(h, "trItemIndex")), item, thisRef));
                        }
                }
                foreach (var k in keyed.OrderBy(k => k.key)) res.Heads.Add((k.item, k.obj));
                return (res, null);
            }
            catch (Exception ex)
            {
                var e = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                return (null, e.GetType().Name + ": " + e.Message + " @ " + string.Join(" < ", (e.StackTrace ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).Take(4).Select(l => l.Trim())));
            }
        }

        static Array objsOf(object g, Type tSignals) => (Array)Get(g, "SignalObjects");

        static void SpeedSignals(Array objs, object cfg, object items, Type tSigObj)
        {
            var types = Get(cfg, "SignalTypes") as IDictionary;
            var itemArr = (Array)items;
            var typeField = tSigObj.GetField("Type", BF);
            if (types == null || typeField == null) return;
            object speedSignal = Enum.Parse(typeField.FieldType, "SpeedSignal");
            foreach (var o in objs)
            {
                if (o == null || typeField.GetValue(o).ToString() != "Signal" || Get(o, "SignalHeads") is not IEnumerable heads) continue;
                bool any = false, allSpeed = true;
                foreach (var h in heads)
                {
                    any = true;
                    var it = itemArr.GetValue(Convert.ToInt32(Get(h, "TDBIndex")));
                    string name = Get(it, "SignalType") as string;
                    object st = name != null && types.Contains(name) ? types[name] : null;
                    if (st == null) { allSpeed = false; break; }   // tipo desconocido: Open Rails no le pone función (no es de velocidad)
                    object fn = Get(st, "Function") ?? Get(st, "FnType");
                    string f = (Get(fn, "MstsFunction") ?? fn)?.ToString() ?? "";
                    if (f != "SPEED") { allSpeed = false; break; }
                }
                if (any && allSpeed) typeField.SetValue(o, speedSignal);
            }
        }

        // El CancellationToken propio de Open Rails (ORTS.Common): uno que nunca se cancela.
        static object NotCancelled(Type t)
        {
            // ORTS.Common: struct CancellationToken(CancellationTokenSource) y CancellationTokenSource(Action ping)
            var srcT = t.Assembly.GetType(t.Namespace + ".CancellationTokenSource");
            if (srcT != null)
            {
                var sc = srcT.GetConstructor(new[] { typeof(Action) });
                if (sc != null) { var src = sc.Invoke(new object[] { (Action)(() => { }) }); var tk = srcT.GetProperty("Token")?.GetValue(src); if (tk != null && tk.GetType() == t) return tk; }
            }
            foreach (var c in t.GetConstructors(BF))
            {
                var ps = c.GetParameters();
                if (ps.Length == 0) return c.Invoke(null);
                if (ps.Length == 1 && ps[0].ParameterType == typeof(Func<bool>)) return c.Invoke(new object[] { (Func<bool>)(() => false) });
            }
            return RuntimeHelpers.GetUninitializedObject(t);
        }

        static object Get(object o, string n)
        {
            if (o == null) return null;
            var t = o.GetType();
            for (var ty = t; ty != null; ty = ty.BaseType)
            {
                var f = ty.GetField(n, BF); if (f != null) return f.GetValue(f.IsStatic ? null : o);
                var p = ty.GetProperty(n, BF); if (p != null) return p.GetValue(o);
            }
            return null;
        }

        static void SetAny(object o, string n, object v)
        {
            for (var ty = o.GetType(); ty != null; ty = ty.BaseType)
            {
                var f = ty.GetField(n, BF); if (f != null) { f.SetValue(f.IsStatic ? null : o, v); return; }
                var p = ty.GetProperty(n, BF); if (p != null && p.GetSetMethod(true) != null) { p.GetSetMethod(true).Invoke(o, new[] { v }); return; }
                var bf = ty.GetField("<" + n + ">k__BackingField", BF); if (bf != null) { bf.SetValue(o, v); return; }
            }
        }

        static void Call(object o, Type t, string m, params object[] a)
        {
            var mi = t.GetMethods(BF).FirstOrDefault(x => x.Name == m && x.GetParameters().Length == a.Length)
                     ?? throw new MissingMethodException(t.Name, m);
            mi.Invoke(o, a);
        }

        // Las listas y diccionarios que el constructor de Signals crea al declararlos (aquí no se ejecuta).
        static void InitCollections(object o, Type t)
        {
            foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                var ft = f.FieldType;
                if (!ft.IsGenericType) continue;
                var gd = ft.GetGenericTypeDefinition();
                if (gd == typeof(List<>) || gd == typeof(Dictionary<,>) || gd == typeof(SortedList<,>) || gd == typeof(HashSet<>))
                    try { f.SetValue(o, Activator.CreateInstance(ft)); } catch { }
            }
        }
    }
}
