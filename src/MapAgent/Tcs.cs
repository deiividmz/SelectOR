// Scripts de seguridad (TCS) más nuevos que el Open Rails que los carga (1.2.75).
// Los trenes españoles con ASFA + LZB + ATO (Common_TCS_Spain\Script1\ServerTCS.cs, 38 .eng, p. ej. el 446-140) usan dos
// propiedades del sistema de seguridad que solo tienen los Open Rails nuevos (New Year 174/175, MG, Inestable):
// SetSpeedMpS y SetSpeedAccelerationMpSS (la velocidad y la aceleración que el ATO le pide al control de crucero). En
// CYZYRails, Testing 1.5.1 y Estable 1.6 no existen: el script entero no compila, Open Rails lo descarta sin avisar y pone
// su sistema de seguridad básico (sin LZB ni ASFA en la cabina).
// Aquí, al preparar Open Rails el script (Roslyn: CSharpSyntaxTree.ParseText), si esa versión no tiene alguna de esas
// propiedades y el script la usa, se le añade al propio script, en memoria, en su clase de sistema de seguridad, y el
// agente hace lo de New Year: la velocidad del ATO limita la del control de crucero (la aceleración del ATO no: el control
// de crucero de esas versiones calcula de otra manera). Así compila y funcionan el LZB, el ASFA y el ATO.
// Va en la misma línea que la llave de la clase: los números de línea de los errores no cambian. No toca ningún archivo.

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using HarmonyLib;

namespace SelectOR.MapAgent
{
    public static class Tcs
    {
        // lo que se añade si falta: nombre → miembro. Llaman al agente (por AppDomain: el script no puede ver el agente) para
        // hacer lo de New Year: la velocidad del ATO limita la del control de crucero (Ato*, más abajo); sin agente, nada.
        static readonly (string name, string code)[] Shims =
        {
            ("SetSpeedMpS", "public float? SetSpeedMpS { get { var f = System.AppDomain.CurrentDomain.GetData(\"selector.ato.get\") as System.Func<object, float?>; return f != null ? f(this) : (float?)null; } set { var f = System.AppDomain.CurrentDomain.GetData(\"selector.ato.set\") as System.Action<object, float?>; if (f != null) f(this, value); } }"),
            ("SetSpeedAccelerationMpSS", "public float SetSpeedAccelerationMpSS { get { var f = System.AppDomain.CurrentDomain.GetData(\"selector.ato.getacc\") as System.Func<object, float>; return f != null ? f(this) : 0f; } set { var f = System.AppDomain.CurrentDomain.GetData(\"selector.ato.setacc\") as System.Action<object, float>; if (f != null) f(this, value); } }"),
        };

        // ── el ATO en los Open Rails que no lo tienen: como en New Year (CruiseControl.ASCSetSpeedMpS) sin
        // ASCSpeedTakesPriorityOverSpeedSelector: la velocidad del control de crucero es la menor entre la elegida y la del
        // ATO. (1.2.76) El control de crucero de estas versiones no usa la aceleración del ATO: solo frena cuando el tren ya va
        // más deprisa que la velocidad marcada (y con más fuerza cuanto mayor es la diferencia), con un tope de deceleración
        // (MaxDecelerationMpSS, 0,5 m/s² de serie). Siguiendo la curva de frenado del ATO, que baja sin parar, iba siempre
        // tarde y se pasaba del punto de parada. Ahora, mientras el ATO frena: la velocidad marcada es la de la curva un poco
        // más adelante en el tiempo (AtoLead s, menos cerca de la parada) y más baja si el tren va por encima de la curva; y
        // el tope de deceleración, el que pide el ATO (se devuelve al suyo al acabar).
        sealed class AtoState { public float? Speed; public float Acc; public float? OrigMaxDec; }
        const float AtoLead = 2f;   // s de adelanto sobre la curva del ATO (a 20 km/h o más; menos por debajo)
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, AtoState> _ato = new System.Runtime.CompilerServices.ConditionalWeakTable<object, AtoState>();
        static readonly Dictionary<Type, PropertyInfo> _locoProp = new Dictionary<Type, PropertyInfo>();
        static FieldInfo _ccField, _ccMode, _ccLoco, _ccMaxDec; static PropertyInfo _ccSet; static MemberInfo _locoSpeed;

        static object LocoOf(object tcs)
        {
            if (tcs == null) return null;
            var t = tcs.GetType();
            PropertyInfo p;
            lock (_locoProp)
                if (!_locoProp.TryGetValue(t, out p))
                {
                    for (var b = t; b != null && p == null; b = b.BaseType)
                        p = b.GetProperty("Locomotive", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                            ?? b.GetProperty("Loco", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    _locoProp[t] = p;
                }
            try { return p?.GetValue(tcs, null); } catch { return null; }
        }

        static float? AtoGet(object tcs)
        {
            try
            {
                var loco = LocoOf(tcs); if (loco == null || _ccField == null) return null;
                var cc = _ccField.GetValue(loco);
                if (cc == null || _ccMode == null || _ccSet == null || _ccMode.GetValue(cc)?.ToString() != "Auto") return null;
                return (float)_ccSet.GetValue(cc, null);
            }
            catch { return null; }
        }
        static void AtoSet(object tcs, float? v) { var loco = LocoOf(tcs); if (loco != null) _ato.GetOrCreateValue(loco).Speed = v; }
        static float AtoGetAcc(object tcs) { var loco = LocoOf(tcs); return loco != null && _ato.TryGetValue(loco, out var a) ? a.Acc : 0f; }
        static void AtoSetAcc(object tcs, float v) { var loco = LocoOf(tcs); if (loco != null) _ato.GetOrCreateValue(loco).Acc = v; }

        // (CruiseControl.SetSpeedMpS) la menor entre la elegida y la del ATO
        static void AfterSetSpeed(object __instance, ref float __result)
        {
            try
            {
                var loco = _ccLoco?.GetValue(__instance);
                if (loco == null || !_ato.TryGetValue(loco, out var a)) return;
                bool braking = a.Speed.HasValue && a.Speed.Value < __result && a.Acc < -0.05f;
                if (a.Speed.HasValue && a.Speed.Value < __result) __result = a.Speed.Value;
                // (1.2.76) frenando con la curva del ATO: adelanto, corrección y tope de deceleración
                float v = Speed(loco);
                if (braking && v >= 0)
                {
                    float near = Clamp((v - (a.Speed.Value - 1f)) / 1f, 0f, 1f);   // (solo cerca o por encima de la curva)
                    float lead = AtoLead * Clamp(v / (20f / 3.6f), 0.3f, 1f);
                    float set = a.Speed.Value + a.Acc * lead * near - Math.Max(0f, v - a.Speed.Value);
                    __result = Math.Max(0f, Math.Min(__result, set));
                    if (_ccMaxDec != null)
                    {
                        float cur = (float)_ccMaxDec.GetValue(__instance);
                        if (!a.OrigMaxDec.HasValue) a.OrigMaxDec = cur;
                        float want = Math.Max(a.OrigMaxDec.Value, -a.Acc + 0.15f);
                        if (Math.Abs(cur - want) > 0.01f) _ccMaxDec.SetValue(__instance, want);
                    }
                }
                else if (a.OrigMaxDec.HasValue && _ccMaxDec != null)
                {
                    _ccMaxDec.SetValue(__instance, a.OrigMaxDec.Value); a.OrigMaxDec = null;
                }
            }
            catch { }
        }

        static float Clamp(float x, float lo, float hi) => x < lo ? lo : x > hi ? hi : x;

        static float Speed(object loco)
        {
            try
            {
                if (_locoSpeed is PropertyInfo pi) return Math.Abs(Convert.ToSingle(pi.GetValue(loco, null)));
                if (_locoSpeed is FieldInfo fi) return Math.Abs(Convert.ToSingle(fi.GetValue(loco)));
            }
            catch { }
            return -1f;
        }

        static void InstallAto(Harmony h)
        {
            try
            {
                var cc = AccessTools.TypeByName("Orts.Simulation.RollingStocks.SubSystems.CruiseControl");
                var loco = AccessTools.TypeByName("Orts.Simulation.RollingStocks.MSTSLocomotive");
                if (cc == null || loco == null || AccessTools.Field(cc, "ASCSetSpeedMpS") != null) return;   // (ya lo tiene)
                _ccField = AccessTools.Field(loco, "CruiseControl"); _ccMode = AccessTools.Field(cc, "SpeedRegMode");
                _ccLoco = AccessTools.Field(cc, "Locomotive"); _ccSet = AccessTools.Property(cc, "SetSpeedMpS");
                _ccMaxDec = AccessTools.Field(cc, "MaxDecelerationMpSS"); if (_ccMaxDec != null && _ccMaxDec.FieldType != typeof(float)) _ccMaxDec = null;
                _locoSpeed = (MemberInfo)AccessTools.Property(loco, "AbsSpeedMpS") ?? AccessTools.Field(loco, "AbsSpeedMpS");
                var d = AppDomain.CurrentDomain;
                d.SetData("selector.ato.get", new Func<object, float?>(AtoGet));
                d.SetData("selector.ato.set", new Action<object, float?>(AtoSet));
                d.SetData("selector.ato.getacc", new Func<object, float>(AtoGetAcc));
                d.SetData("selector.ato.setacc", new Action<object, float>(AtoSetAcc));
                if (_ccSet?.GetGetMethod(true) != null && _ccLoco != null && _ccSet.PropertyType == typeof(float))
                {
                    h.Patch(_ccSet.GetGetMethod(true), postfix: new HarmonyMethod(typeof(Tcs), nameof(AfterSetSpeed)));
                    Agent.Log("scripts de seguridad: ATO conectado al control de crucero (la velocidad del ATO limita la elegida, como en New Year; frenando, con adelanto sobre su curva"
                              + (_ccMaxDec != null ? " y su deceleración" : "") + (_locoSpeed == null ? "; sin la velocidad del tren: sin adelanto" : "") + ")");
                }
                else Agent.Log("scripts de seguridad: este control de crucero no se deja limitar: el ATO no actúa");
            }
            catch (Exception ex) { Agent.Log("scripts de seguridad (ATO): " + ex.GetType().Name + " " + ex.Message); }
        }

        static readonly Regex ClassRx = new Regex(@"\bclass\s+\w+\s*(?:<[^>{]*>)?\s*:\s*(?:global::)?(?:ORTS\.Scripting\.Api\.)?TrainControlSystem\b[^{;]*\{", RegexOptions.Compiled);

        // (puro: lo usan las pruebas) el script con lo que le falta; apiHas(nombre): ¿lo tiene ya este Open Rails?
        public static string Patch(string text, Func<string, bool> apiHas, List<string> added = null)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf("TrainControlSystem", StringComparison.Ordinal) < 0) return text;
            string add = "";
            foreach (var (name, code) in Shims)
            {
                if (text.IndexOf(name, StringComparison.Ordinal) < 0 || apiHas(name)) continue;
                // (si el propio script ya lo declara, no)
                if (Regex.IsMatch(text, @"\b(?:float\??|double\??|var|void)\s+" + name + @"\b")) continue;
                add += " " + code;
                added?.Add(name);
            }
            if (add.Length == 0) return text;
            int n = 0;
            string res = ClassRx.Replace(text, m => { n++; return m.Value + add + " "; });
            if (n == 0) { added?.Clear(); return text; }
            return res;
        }

        static Type _api;
        static bool ApiHas(string name)
        {
            if (_api == null) return true;   // (sin saber: no se toca)
            const BindingFlags bf = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy;
            return _api.GetProperty(name, bf) != null || _api.GetField(name, bf) != null || _api.GetMethod(name, bf) != null;
        }

        static int _done;
        // (desde Agent.Install) 1 = puesto ya; 0 = cuando se cargue Roslyn
        public static int Install(Harmony h)
        {
            if (TryPatch(h)) return 1;
            AssemblyLoadEventHandler onLoad = null;
            onLoad = (s, e) =>
            {
                try
                {
                    if (e.LoadedAssembly.GetName().Name != "Microsoft.CodeAnalysis.CSharp") return;
                    AppDomain.CurrentDomain.AssemblyLoad -= onLoad;
                    TryPatch(h);
                }
                catch (Exception ex) { Agent.Log("scripts de seguridad: " + ex.GetType().Name + " " + ex.Message); }
            };
            AppDomain.CurrentDomain.AssemblyLoad += onLoad;
            return 0;
        }

        static bool TryPatch(Harmony h)
        {
            if (_done != 0) return true;
            Type tree = null;
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                if (a.GetName().Name == "Microsoft.CodeAnalysis.CSharp") { tree = a.GetType("Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree", false); break; }
            if (tree == null) return false;
            _done = 1;
            try
            {
                _api = AccessTools.TypeByName("ORTS.Scripting.Api.TrainControlSystem");
                var missing = new List<string>();
                foreach (var (name, _) in Shims) if (!ApiHas(name)) missing.Add(name);
                if (missing.Count == 0) return true;   // (este Open Rails ya las tiene: nada que hacer)
                InstallAto(h);
                int n = 0;
                foreach (var m in tree.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name != "ParseText") continue;
                    var ps = m.GetParameters();
                    // (la de texto y la de SourceText: la de texto es tan corta que .NET la incrusta en quien la llama y no se
                    // pasa por ella; la de SourceText, no)
                    if (ps.Length < 3 || ps[0].Name != "text" || Array.FindIndex(ps, p => p.Name == "path" && p.ParameterType == typeof(string)) < 0) continue;
                    if (ps[0].ParameterType != typeof(string) && ps[0].ParameterType.FullName != "Microsoft.CodeAnalysis.Text.SourceText") continue;
                    h.Patch(m, prefix: new HarmonyMethod(typeof(Tcs), nameof(BeforeParse)));
                    n++;
                }
                Agent.Log($"scripts de seguridad: a este Open Rails le falta {string.Join(", ", missing)}; se añade a los scripts que lo usan ({n} puntos)");
            }
            catch (Exception ex) { Agent.Log("scripts de seguridad: " + ex.GetType().Name + " " + ex.Message); }
            return true;
        }

        static int _told;
        static MethodInfo _from;
        static void BeforeParse(object[] __args, MethodBase __originalMethod)
        {
            try
            {
                var ps = __originalMethod.GetParameters();
                int ip = Array.FindIndex(ps, p => p.Name == "path");
                string path = ip >= 0 ? __args[ip] as string : null;
                if (__args[0] == null || path == null || !path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) return;
                bool isString = __args[0] is string;
                string text = isString ? (string)__args[0] : __args[0].ToString();
                if (text.IndexOf("TrainControlSystem", StringComparison.Ordinal) < 0) return;
                var added = new List<string>();
                string res = Patch(text, ApiHas, added);
                if (ReferenceEquals(res, text) || added.Count == 0) return;
                if (isString) __args[0] = res;
                else
                {
                    // SourceText.From(texto, la codificación de antes)
                    var st = __args[0].GetType();
                    var enc = st.GetProperty("Encoding")?.GetValue(__args[0]) as System.Text.Encoding;
                    if (_from == null)
                        for (var bt = st; bt != null && _from == null; bt = bt.BaseType)
                            foreach (var m in bt.GetMethods(BindingFlags.Public | BindingFlags.Static))
                            {
                                var mp = m.GetParameters();
                                if (m.Name == "From" && mp.Length >= 2 && mp[0].ParameterType == typeof(string) && mp[1].ParameterType == typeof(System.Text.Encoding)) { _from = m; break; }
                            }
                    if (_from == null) return;
                    var fp = _from.GetParameters();
                    var fa = new object[fp.Length];
                    fa[0] = res; fa[1] = enc ?? System.Text.Encoding.UTF8;
                    for (int i = 2; i < fa.Length; i++) fa[i] = fp[i].DefaultValue;
                    __args[0] = _from.Invoke(null, fa);
                }
                if (_told++ < 10) Agent.Log($"scripts de seguridad: {System.IO.Path.GetFileName(path)}: añadido {string.Join(", ", added)} (el ATO, por el control de crucero; el resto, como siempre)");
            }
            catch { }
        }
    }
}
