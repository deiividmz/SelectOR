// Luces de CYZYRails (1.2.72; reparto nuevo en la 1.2.75). CYZYRails ilumina el entorno con luces de punto
// (RenderFrame.AddPointLight: las farolas de la ruta —CyzyLamps.txt—, los faros), pero usa solo 24 a la vez: las más
// cercanas a la cámara (RenderFrame.GetPointLights), y la 25.ª no se ve nada. En una estación grande las del fondo se
// encendían y se apagaban a tramos. Aquí se eligen de otra manera, sin que ninguna luz cambie nunca de sitio:
//  · solo cuentan las que alumbran lo que se ve (su alcance dentro del campo de visión, con margen); las de detrás de la
//    cámara no gastan plaza;
//  · grupos fijos en el mundo: cada luz quieta pertenece siempre a la misma celda de 8 m, de 32 m y de 128 m, cada una con
//    su luz de conjunto en el centro de sus luces (con la suma de su luz). De cerca se ven las luces sueltas; al alejarse,
//    se funden en la de su celda de 8 m, luego en la de 32 m y, más lejos, en la de 128 m. Solo cambia cuánta luz da cada
//    una (poco a poco), nunca dónde está (en la 1.2.72 los grupos dependían de la distancia y su centro saltaba: se veía
//    el punto de luz en otro sitio y luego se recolocaba);
//  · si aun así no caben en 24, las distancias de esos fundidos se acortan poco a poco hasta que caben (y se alargan
//    cuando sobra sitio); lo que queda fuera se va apagando, no de golpe;
//  · las luces que se mueven (los faros de los trenes) van siempre sueltas;
//  · (1.2.75) los carteles de varias caras (Facades): sus luces se funden igual, por celdas, pero solo con las de su misma cara. Antes, de lejos,
//    las de las tres caras se juntaban en el centro del cartel, dentro, y alumbraban su interior en vez de las caras.
// Solo con los gráficos mejorados de CYZYRails (si no, no dibuja luces de punto). En otro Open Rails no se pone nada.
// (La luz en el interior de los coches de viajeros se probó y se quitó a petición en la 1.2.72.)

using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace SelectOR.MapAgent
{
    public static class Luces
    {
        // (desde Sol.Install, cuando ya están los tipos del 3D) 1 = puesto.
        public static int Install(Harmony h)
        {
            try
            {
                var rf = AccessTools.TypeByName("Orts.Viewer3D.RenderFrame");
                if (rf == null || AccessTools.TypeByName("Orts.Viewer3D.LampDefinitions") == null) return 0;   // (no es CYZYRails)
                bool ok = InstallPick(h, rf);
                if (ok) InstallFacades(h);
                return ok ? 1 : 0;
            }
            catch (Exception ex) { Agent.Log("luces: " + ex.GetType().Name + " " + ex.Message); return 0; }
        }

        static FieldInfo _lights, _camXna, _view, _proj, _lPos, _lCol, _lInt, _lDown, _lSurf;
        static Func<object, int, Vector3> _rPos, _rCol;
        static Func<object, int, float> _rInt;
        static Func<object, int, bool> _rDown, _rSurf;
        static int _errors;

        static bool InstallPick(Harmony h, Type rf)
        {
            try
            {
                var get = AccessTools.Method(rf, "GetPointLights");
                var pl = AccessTools.Inner(rf, "PointLight");
                _lights = AccessTools.Field(rf, "PointLights");
                _camXna = AccessTools.Field(rf, "XNACameraLocation");
                _view = AccessTools.Field(rf, "XNACameraView"); _proj = AccessTools.Field(rf, "XNACameraProjection");
                if (get == null || pl == null || _lights == null || _camXna == null || get.ReturnType != typeof(int)) return false;
                _lPos = AccessTools.Field(pl, "Position"); _lCol = AccessTools.Field(pl, "Color"); _lInt = AccessTools.Field(pl, "Intensity");
                _lDown = AccessTools.Field(pl, "Downward"); _lSurf = AccessTools.Field(pl, "Surface");
                if (_lPos == null || _lCol == null || _lInt == null || _lDown == null || _lSurf == null) return false;
                Readers(pl);
                h.Patch(get, prefix: new HarmonyMethod(typeof(Luces), nameof(BeforeLights)));
                Agent.Log("CYZYRails: luces elegidas por lo que se ve, en grupos fijos de cerca a lejos (sin saltos)" + (_view == null || _proj == null ? " (sin campo de visión)" : "") + (_rPos == null ? " (lectura lenta)" : ""));
                return true;
            }
            catch (Exception ex) { Agent.Log("luces lejanas: " + ex.GetType().Name + " " + ex.Message); return false; }
        }

        // lectores compilados de la lista de luces (su tipo es privado); si no se puede, por reflexión
        static void Readers(Type pl)
        {
            try
            {
                var listT = typeof(List<>).MakeGenericType(pl);
                var o = System.Linq.Expressions.Expression.Parameter(typeof(object), "o");
                var i = System.Linq.Expressions.Expression.Parameter(typeof(int), "i");
                var item = System.Linq.Expressions.Expression.Property(System.Linq.Expressions.Expression.Convert(o, listT), "Item", i);
                Func<FieldInfo, System.Linq.Expressions.Expression> f = fi => System.Linq.Expressions.Expression.Field(item, fi);
                _rPos = System.Linq.Expressions.Expression.Lambda<Func<object, int, Vector3>>(f(_lPos), o, i).Compile();
                _rCol = System.Linq.Expressions.Expression.Lambda<Func<object, int, Vector3>>(f(_lCol), o, i).Compile();
                _rInt = System.Linq.Expressions.Expression.Lambda<Func<object, int, float>>(f(_lInt), o, i).Compile();
                _rDown = System.Linq.Expressions.Expression.Lambda<Func<object, int, bool>>(f(_lDown), o, i).Compile();
                _rSurf = System.Linq.Expressions.Expression.Lambda<Func<object, int, bool>>(f(_lSurf), o, i).Compile();
            }
            catch { _rPos = null; }
        }

        // ── el reparto
        public static Func<double> Now = () => _clock.Elapsed.TotalSeconds;   // (las pruebas lo cambian)
        static readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        public static bool Sliding = true;   // (pruebas: false = sin fundidos en el tiempo)

        const int Max = 24, Room = 21;        // plazas; las que se quieren usar (con margen para los fundidos)
        const float Reach = 30f, FadeTime = 0.3f;
        // niveles: sueltas, celdas de 8, 32 y 128 m. Se pasa al nivel lv entre Near[lv−1] y Far[lv−1] metros de la cámara
        // al centro de la celda de ese nivel (a escala 1)
        static readonly float[] Size = { 0f, 8f, 32f, 128f };
        static readonly float[] Near = { 22f, 70f, 220f }, Far = { 38f, 110f, 320f };
        // al apretar (escala < 1) se aprietan antes los niveles lejanos que lo cercano (exponente), y nunca más cerca que MinNear
        static readonly float[] Exp = { 0.35f, 0.8f, 1f }, MinNear = { 6f, 14f, 40f };
        static float NearAt(int j, float s) => Math.Max(MinNear[j], Near[j] * (float)Math.Pow(s, Exp[j]));
        static float FarAt(int j, float s) { float n = NearAt(j, s); return n * Far[j] / Near[j]; }
        const int L = 4;
        static float _scale = 1f, _scaleTry = 1f;   // se acorta cuando no caben (0,1…1)

        struct Raw { public Vector3 P, C; public float I; public bool Down, Surf; public long K, G; }   // G: su cara de cartel (0: ninguna)
        struct Cand { public long K; public Vector3 P, C; public float I, W, D; public bool Down, Surf; }
        struct Acc { public Vector3 P, C; public float I; public int N; public bool Down, Surf; }

        static Raw[] _raw = new Raw[256]; static int _rawCount;
        static readonly List<Cand> _cand = new List<Cand>();
        static readonly Cand[] _out = new Cand[Max]; static int _outCount; static bool _ready;
        static HashSet<long> _seen = new HashSet<long>(), _seenNext = new HashSet<long>();   // luces quietas: las que estaban el fotograma anterior
        static Dictionary<long, float> _shown = new Dictionary<long, float>(), _shownNext = new Dictionary<long, float>();
        static readonly Dictionary<long, Acc>[] _cells = { null, new Dictionary<long, Acc>(), new Dictionary<long, Acc>(), new Dictionary<long, Acc>() };
        static readonly Dictionary<long, float> _part = new Dictionary<long, float>();
        static readonly Dictionary<long, Cand> _last = new Dictionary<long, Cand>();   // lo que se mandó el fotograma anterior
        static readonly List<Cand> _pool = new List<Cand>();
        static double _lastT = -1; static Vector3 _lastCam;

        // (en lugar de GetPointLights) false = ya está hecho
        static bool BeforeLights(object __instance, Vector4[] positions, Vector4[] colors, float sceneUnitsPerCandela, bool surfaceLightsOnly, ref int __result)
        {
            try
            {
                if (positions == null || colors == null || !(_lights.GetValue(__instance) is System.Collections.IList list)) return true;
                // CYZYRails pide primero las de superficie y luego todas, con la misma lista: se reparte en la primera
                if (surfaceLightsOnly || !_ready) Pick(__instance, list);
                int max = Math.Min(Max, Math.Min(positions.Length, colors.Length)), n = 0;
                for (int k = 0; k < _outCount && n < max; k++)
                {
                    var l = _out[k];
                    if (surfaceLightsOnly && !l.Surf) continue;
                    positions[n] = new Vector4(l.P, l.I * l.W * sceneUnitsPerCandela);
                    colors[n] = new Vector4(l.C, l.Down ? 1 : 0);
                    n++;
                }
                if (!surfaceLightsOnly) _ready = false;   // la siguiente vez, otro fotograma
                __result = n;
                return false;
            }
            catch (Exception ex) { if (_errors++ < 3) Agent.Log("luces: " + ex.GetType().Name + " " + ex.Message); _ready = false; return true; }
        }

        static long Key(int kind, Vector3 p, float size, bool down, bool surf) =>
            ((long)kind << 60) ^ ((long)(down ? 1 : 0) << 59) ^ ((long)(surf ? 1 : 0) << 58)
            ^ (((long)Math.Floor(p.X / size) & 0xFFFFF) << 38) ^ (((long)Math.Floor(p.Y / size) & 0x3FFFF) << 20) ^ ((long)Math.Floor(p.Z / size) & 0xFFFFF);
        static long KeyAt(int lv, Vector3 p, bool down, bool surf) => Key(lv, p, Size[lv], down, surf);
        // la celda de una luz en el nivel lv: la de su sitio (8, 32 o 128 m) y, si es de un cartel, además solo con las de su cara
        static long CellOf(int lv, Raw l) => l.G != 0 ? KeyAt(lv, l.P, l.Down, l.Surf) ^ unchecked(l.G * (long)0x9E3779B97F4A7C15UL) ^ (1L << 57) : KeyAt(lv, l.P, l.Down, l.Surf);

        // ── (1.2.75) carteles de varias caras: sus focos van por fuera de cada cara; de lejos se funden por caras
        static readonly string[] Facades = { "mercadona.s", "povedacartel.s" };
        static FieldInfo _defLamps, _defRoute, _objLamps;
        static string _facRoute; static readonly HashSet<object> _facLists = new HashSet<object>();
        static readonly object _facLock = new object();
        static readonly Dictionary<long, (long g, double t)> _facade = new Dictionary<long, (long, double)>();   // luz (por su sitio) → su cara
        static double _facPrune;

        static void InstallFacades(Harmony h)
        {
            try
            {
                var defs = AccessTools.TypeByName("Orts.Viewer3D.LampDefinitions"); var obj = AccessTools.TypeByName("Orts.Viewer3D.SceneryLamps");
                var prep = obj == null ? null : AccessTools.Method(obj, "PrepareFrame");
                _defLamps = defs == null ? null : AccessTools.Field(defs, "Lamps"); _defRoute = defs == null ? null : AccessTools.Field(defs, "LoadedRoute");
                _objLamps = obj == null ? null : AccessTools.Field(obj, "Lamps");
                if (prep == null || _defLamps == null || !_defLamps.IsStatic || _objLamps == null) { Agent.Log("luces: carteles por caras no disponible"); return; }
                h.Patch(prep, prefix: new HarmonyMethod(typeof(Luces), nameof(BeforeLamps)), postfix: new HarmonyMethod(typeof(Luces), nameof(AfterLamps)));
                Agent.Log("CYZYRails: carteles de varias caras, luces de conjunto por caras (" + string.Join(", ", Facades) + ")");
            }
            catch (Exception ex) { Agent.Log("luces (carteles): " + ex.GetType().Name + " " + ex.Message); }
        }

        // las listas de lámparas de esas formas (las de LampDefinitions; cambian con la ruta)
        static bool IsFacade(object lamps)
        {
            if (lamps == null) return false;
            string route = _defRoute?.GetValue(null) as string;
            if (route != _facRoute || _facLists.Count == 0)
            {
                _facRoute = route; _facLists.Clear();
                if (_defLamps.GetValue(null) is System.Collections.IDictionary d)
                    foreach (System.Collections.DictionaryEntry e in d)
                    {
                        string k = (e.Key?.ToString() ?? "").Replace('/', '\\');
                        string name = k.Substring(k.LastIndexOf('\\') + 1).ToLowerInvariant();
                        if (Array.IndexOf(Facades, name) >= 0 && e.Value != null) _facLists.Add(e.Value);
                    }
            }
            return _facLists.Contains(lamps);
        }

        static void BeforeLamps(object[] __args, out int __state)
        {
            __state = -1;
            try { if (__args != null && __args.Length > 0 && _lights.GetValue(__args[0]) is System.Collections.IList l) __state = l.Count; } catch { }
        }

        // las luces que acaba de poner el objeto: si es un cartel, por caras (las que van en línea, una cara; en el orden del
        // archivo de farolas, cara a cara)
        static void AfterLamps(object __instance, object[] __args, int __state)
        {
            try
            {
                if (__state < 0 || !(_lights.GetValue(__args[0]) is System.Collections.IList list) || list.Count <= __state) return;
                if (!IsFacade(_objLamps.GetValue(__instance))) return;
                long inst = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(__instance);
                double t = Now();
                int face = 0; Vector3 a = default, dir = default; int inFace = 0;
                lock (_facLock)
                {
                    for (int i = __state; i < list.Count; i++)
                    {
                        Vector3 p = _rPos != null ? _rPos(list, i) : (Vector3)_lPos.GetValue(list[i]);
                        bool down = _rDown != null ? _rDown(list, i) : (bool)_lDown.GetValue(list[i]);
                        bool surf = _rSurf != null ? _rSurf(list, i) : (bool)_lSurf.GetValue(list[i]);
                        var q = new Vector2(p.X, p.Z);
                        if (inFace == 0) { a = p; inFace = 1; }
                        else if (inFace == 1) { var d2 = new Vector2(p.X - a.X, p.Z - a.Z); if (d2.Length() > 0.5f) { d2.Normalize(); dir = new Vector3(d2.X, 0, d2.Y); inFace = 2; } }
                        else
                        {
                            float off = Math.Abs((q.X - a.X) * dir.Z - (q.Y - a.Z) * dir.X);   // distancia a la línea de la cara
                            if (off > 1.5f) { face++; a = p; inFace = 1; }
                        }
                        _facade[Key(0, p, 0.05f, down, surf)] = ((inst << 8) ^ (face + 1) ^ (0x5A5AL << 40), t);
                    }
                    if (t - _facPrune > 5)
                    {
                        _facPrune = t;
                        var old = new List<long>(); foreach (var kv in _facade) if (t - kv.Value.t > 3) old.Add(kv.Key);
                        foreach (var k in old) _facade.Remove(k);
                    }
                }
            }
            catch (Exception ex) { if (_errors++ < 3) Agent.Log("luces (carteles): " + ex.GetType().Name + " " + ex.Message); }
        }

        static float Smooth(float a, float b, float x) { float t = MathHelper.Clamp((x - a) / Math.Max(1e-3f, b - a), 0f, 1f); return t * t * (3f - 2f * t); }

        static void Pick(object frame, System.Collections.IList list)
        {
            _ready = true;
            var cam = (Vector3)_camXna.GetValue(frame);
            double t = Now();
            float dt = _lastT < 0 ? 1f : (float)Math.Max(0, Math.Min(0.25, t - _lastT));
            bool jump = _lastT < 0 || Vector3.Distance(cam, _lastCam) > 150 || !Sliding;
            _lastT = t; _lastCam = cam;
            BoundingFrustum fr = null;
            try { if (_view != null && _proj != null) fr = new BoundingFrustum((Matrix)_view.GetValue(frame) * (Matrix)_proj.GetValue(frame)); } catch { fr = null; }
            bool Seen(Vector3 p, float r)
            {
                if (fr == null) return true;
                if (Vector3.DistanceSquared(p, cam) < r * r) return true;   // (alumbra donde está la cámara)
                return fr.Intersects(new BoundingSphere(p, r));
            }

            // las luces de este fotograma
            int total = list.Count;
            if (_raw.Length < total) _raw = new Raw[Math.Max(total, _raw.Length * 2)];
            bool fast = _rPos != null;
            _seenNext.Clear();
            for (int i = 0; i < total; i++)
            {
                Raw l = default;
                if (fast) { l.P = _rPos(list, i); l.C = _rCol(list, i); l.I = _rInt(list, i); l.Down = _rDown(list, i); l.Surf = _rSurf(list, i); }
                else { var o = list[i]; l.P = (Vector3)_lPos.GetValue(o); l.C = (Vector3)_lCol.GetValue(o); l.I = (float)_lInt.GetValue(o); l.Down = (bool)_lDown.GetValue(o); l.Surf = (bool)_lSurf.GetValue(o); }
                l.K = Key(0, l.P, 0.05f, l.Down, l.Surf);
                l.G = 0;
                if (_facade.Count > 0) lock (_facLock) if (_facade.TryGetValue(l.K, out var fg)) l.G = fg.g;
                _seenNext.Add(l.K);
                _raw[i] = l;
            }
            _rawCount = total;

            // celdas fijas de las quietas (con su centro y la suma de su luz)
            for (int lv = 1; lv < L; lv++) _cells[lv].Clear();
            for (int i = 0; i < _rawCount; i++)
            {
                var l = _raw[i]; if (!_seen.Contains(l.K)) continue;
                for (int lv = 1; lv < L; lv++) Add(_cells[lv], CellOf(lv, l), l);
            }

            // candidatas a la escala de ahora; si no caben, la escala baja (poco a poco en el tiempo)
            _scaleTry = _scale; Build(cam, Seen);
            int want = Count();
            float rate = jump ? 1f : Math.Min(1f, dt * 0.8f);
            if (want > Room)
            {
                float s = _scale;
                for (int k = 0; k < 20 && want > Room && s > 0.1f; k++) { s = Math.Max(0.1f, s * 0.9f); _scaleTry = s; Build(cam, Seen); want = Count(); }
                _scale = jump ? s : _scale + (s - _scale) * Math.Max(rate, 0.15f);
            }
            else if (want < Room - 4 && _scale < 1f) _scale = Math.Min(1f, _scale + rate * 0.5f * _scale);
            _scaleTry = _scale; Build(cam, Seen);

            // cada una, poco a poco hacia lo que le toca; las que se veían y ya no tocan (fuera de la vista, otro nivel), apagándose.
            // Luego las 24 que más alumbran de verdad (su luz entre la distancia al cuadrado), para que no se caiga de golpe una
            // luz de conjunto grande por estar lejos
            float a = jump ? 1f : 1f - (float)Math.Exp(-dt / FadeTime);
            _shownNext.Clear(); _pool.Clear();
            foreach (var c0 in _cand)
            {
                var c = c0;
                float w0 = _shown.TryGetValue(c.K, out float s0) ? s0 : (jump ? c.W : 0f);
                c.W = w0 + (c.W - w0) * a;
                if (c.W < 0.004f && c0.W < 0.004f) continue;
                _shownNext[c.K] = c.W; _pool.Add(c);
            }
            if (!jump)
                foreach (var kv in _shown)
                {
                    if (_shownNext.ContainsKey(kv.Key) || !_last.TryGetValue(kv.Key, out var c)) continue;
                    c.W = kv.Value * (1f - a);
                    if (c.W < 0.004f) continue;
                    c.D = Vector3.Distance(c.P, cam);
                    _shownNext[kv.Key] = c.W; _pool.Add(c);
                }
            _pool.Sort((x, y) => (y.W * y.I / (y.D * y.D + 400f)).CompareTo(x.W * x.I / (x.D * x.D + 400f)));
            _outCount = 0;
            foreach (var c in _pool) { if (_outCount >= Max) { _shownNext.Remove(c.K); continue; } _out[_outCount++] = c; }
            _last.Clear(); for (int k = 0; k < _outCount; k++) _last[_out[k].K] = _out[k];
            var tmp = _shown; _shown = _shownNext; _shownNext = tmp;
            var ts = _seen; _seen = _seenNext; _seenNext = ts;
        }

        static void Add(Dictionary<long, Acc> d, long k, Raw l)
        {
            d.TryGetValue(k, out var a);
            float sum = a.I + l.I;
            if (sum > 0) { a.P = (a.P * a.I + l.P * l.I) / sum; a.C = (a.C * a.I + l.C * l.I) / sum; }
            a.I = sum; a.N++; a.Down = l.Down; a.Surf = l.Surf;
            d[k] = a;
        }

        // Cuánto de una luz quieta va a cada nivel (sueltas, 8, 32, 128 m) a la escala _scaleTry; la suma es 1. Un nivel cuya
        // celda no tiene más luces que el anterior no cuenta (se pasa directamente al siguiente).
        static readonly float[] _lw = new float[L];
        static readonly long[] _lk = new long[L];
        static void Levels(Raw l, Vector3 cam)
        {
            float s = _scaleTry, keep = 1f, dl = Vector3.Distance(l.P, cam); int prevN = 1, at = 0;   // at: el nivel en el que está ahora (el último que cuenta)
            for (int lv = 0; lv < L; lv++) { _lw[lv] = 0; _lk[lv] = lv == 0 ? l.K : CellOf(lv, l); }
            for (int lv = 1; lv < L; lv++)
            {
                var c = _cells[lv][_lk[lv]];
                if (c.N <= prevN) continue;   // (las mismas luces: este nivel no cuenta)
                float up = Smooth(NearAt(lv - 1, s), FarAt(lv - 1, s), dl);   // (por la distancia de la propia luz: con la cámara dentro de una celda, su centro puede quedar lejos)
                _lw[at] = keep * (1f - up);
                keep *= up; at = lv; prevN = c.N;
            }
            _lw[at] = keep;
        }

        // las candidatas a la escala _scaleTry
        static void Build(Vector3 cam, Func<Vector3, float, bool> seen)
        {
            _cand.Clear(); _part.Clear();
            for (int i = 0; i < _rawCount; i++)
            {
                var l = _raw[i];
                float d = Vector3.Distance(l.P, cam);
                if (!_seen.Contains(l.K)) { if (seen(l.P, Reach)) _cand.Add(new Cand { K = l.K, P = l.P, C = l.C, I = l.I, W = 1f, D = d, Down = l.Down, Surf = l.Surf }); continue; }   // (se mueve: suelta)
                Levels(l, cam);
                if (_lw[0] > 0.002f && seen(l.P, Reach)) _cand.Add(new Cand { K = l.K, P = l.P, C = l.C, I = l.I, W = _lw[0], D = d, Down = l.Down, Surf = l.Surf });
                for (int lv = 1; lv < L; lv++)
                    if (_lw[lv] > 0) { _part.TryGetValue(_lk[lv], out float a0); _part[_lk[lv]] = a0 + _lw[lv] * l.I; }
            }
            // cada celda: su luz de conjunto, con la parte de luz que le pasan sus luces
            for (int lv = 1; lv < L; lv++)
                foreach (var kv in _cells[lv])
                {
                    if (!_part.TryGetValue(kv.Key, out float part) || part <= 0) continue;
                    var c = kv.Value; float w = c.I > 0 ? Math.Min(1f, part / c.I) : 0;
                    if (w > 0.002f && seen(c.P, Reach + Size[lv] * 0.7f))
                        _cand.Add(new Cand { K = kv.Key, P = c.P, C = c.C, I = c.I, W = w, D = Vector3.Distance(c.P, cam), Down = c.Down, Surf = c.Surf });
                }
        }

        // cuántas plazas hacen falta (las que se ven algo)
        static int Count() { int n = 0; foreach (var c in _cand) if (c.W > 0.05f) n++; return n; }
    }
}
