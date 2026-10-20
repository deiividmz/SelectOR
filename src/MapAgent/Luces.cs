// Luces de CYZYRails (1.2.72). CYZYRails ilumina el entorno con luces de punto (RenderFrame.AddPointLight: las farolas
// de la ruta —CyzyLamps.txt—, los faros), pero usa solo las 24 más cercanas a la cámara. Aquí se reparten mejor (ver
// abajo). Solo con sus gráficos mejorados (si no, CYZYRails no dibuja luces de punto). En otro Open Rails no se pone nada.
// (La luz en el interior de los coches de viajeros se probó y se quitó a petición en la 1.2.72.)

using System;
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
                return InstallFade(h, rf) ? 1 : 0;
            }
            catch (Exception ex) { Agent.Log("luces: " + ex.GetType().Name + " " + ex.Message); return 0; }
        }

        // ── Luces lejanas. CYZYRails usa solo las 24 luces más cercanas a la cámara (RenderFrame.GetPointLights) y la 25.ª no
        // se ve nada: en una estación grande (marquesinas largas, fluorescentes del techo de las subterráneas) las del fondo se
        // encendían y se apagaban a tramos al moverse. Aquí se elige en su lugar, igual que él pero con dos cosas más:
        //  · si hay más de 24, las luces juntas se agrupan en una sola (en su centro y con la suma de su luz), en celdas más
        //    grandes cuanto más lejos (como el detalle de los objetos lejanos) y solo lo justo para que quepan; las de al lado
        //    de la cámara siguen por separado. Así las 24 cubren la estación entera;
        //  · si aun así hay más de 24, las que están cerca del corte se van apagando poco a poco y el cambio no se nota.
        const float FadePart = 0.35f;
        static FieldInfo _lights, _camXna, _lPos, _lCol, _lInt, _lDown, _lSurf;
        static Func<object, int, Vector3> _rPos, _rCol;
        static Func<object, int, float> _rInt;
        static Func<object, int, bool> _rDown, _rSurf;
        static int _fadeErrors;

        struct Light { public Vector3 P, C; public float I, D; public bool Down, Surf; }
        static Light[] _all = new Light[256]; static int _allCount;   // las agrupadas, de la primera pasada del fotograma
        static Light[] _pick = new Light[256];
        static readonly System.Collections.Generic.Dictionary<long, int> _cells = new System.Collections.Generic.Dictionary<long, int>();

        static bool InstallFade(Harmony h, Type rf)
        {
            try
            {
                var get = AccessTools.Method(rf, "GetPointLights");
                var pl = AccessTools.Inner(rf, "PointLight");
                _lights = AccessTools.Field(rf, "PointLights");
                _camXna = AccessTools.Field(rf, "XNACameraLocation");
                if (get == null || pl == null || _lights == null || _camXna == null || get.ReturnType != typeof(int)) return false;
                _lPos = AccessTools.Field(pl, "Position"); _lCol = AccessTools.Field(pl, "Color"); _lInt = AccessTools.Field(pl, "Intensity");
                _lDown = AccessTools.Field(pl, "Downward"); _lSurf = AccessTools.Field(pl, "Surface");
                if (_lPos == null || _lCol == null || _lInt == null || _lDown == null || _lSurf == null) return false;
                Readers(pl);
                h.Patch(get, prefix: new HarmonyMethod(typeof(Luces), nameof(BeforeLights)));
                Agent.Log("CYZYRails: luces lejanas agrupadas y apagadas poco a poco (sin tramos al acercarse)" + (_rPos == null ? " (lectura lenta)" : ""));
                return true;
            }
            catch (Exception ex) { Agent.Log("luces lejanas: " + ex.GetType().Name + " " + ex.Message); return false; }
        }

        // lectores compilados de la lista de luces (su tipo es privado); si no se puede, por reflexión
        static void Readers(Type pl)
        {
            try
            {
                var listT = typeof(System.Collections.Generic.List<>).MakeGenericType(pl);
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

        // (en lugar de GetPointLights) false = ya está hecho
        static bool BeforeLights(object __instance, Vector4[] positions, Vector4[] colors, float sceneUnitsPerCandela, bool surfaceLightsOnly, ref int __result)
        {
            try
            {
                if (positions == null || colors == null || !(_lights.GetValue(__instance) is System.Collections.IList list)) return true;
                var cam = (Vector3)_camXna.GetValue(__instance);
                // CYZYRails pide primero las de superficie y luego todas, con la misma lista: se agrupan en la primera
                if (surfaceLightsOnly || _allCount == 0) Group(list, cam);
                int max = Math.Min(24, Math.Min(positions.Length, colors.Length)), n = 0;
                if (_pick.Length < _allCount) _pick = new Light[_all.Length];
                for (int k = 0; k < _allCount; k++)
                    if (!surfaceLightsOnly || _all[k].Surf) _pick[n++] = _all[k];
                Array.Sort(_pick, 0, n, ByDistance.It);
                float cut = n > max ? _pick[max].D : -1, band = Math.Max(cut * FadePart, 1f);
                int count = Math.Min(n, max);
                for (int k = 0; k < count; k++)
                {
                    var l = _pick[k];
                    float w = 1f;
                    if (cut > 0) { float t = MathHelper.Clamp((cut - l.D) / band, 0f, 1f); w = t * t * (3f - 2f * t); }
                    positions[k] = new Vector4(l.P, l.I * w * sceneUnitsPerCandela);
                    colors[k] = new Vector4(l.C, l.Down ? 1 : 0);
                }
                if (!surfaceLightsOnly) _allCount = 0;   // la siguiente vez, otro fotograma
                __result = count;
                return false;
            }
            catch (Exception ex) { if (_fadeErrors++ < 3) Agent.Log("luces lejanas: " + ex.GetType().Name + " " + ex.Message); _allCount = 0; return true; }
        }

        sealed class ByDistance : System.Collections.Generic.IComparer<Light>
        {
            public static readonly ByDistance It = new ByDistance();
            public int Compare(Light a, Light b) => a.D.CompareTo(b.D);
        }

        // cuánto se agrupa: la celda es esta parte de la distancia a la cámara (0 = nada). Se sube solo lo justo para que quepan
        // en 24 (y se baja cuando sobra sitio, con margen para que no esté cambiando a cada paso)
        static readonly float[] Ks = { 0f, 0.1f, 0.15f, 0.22f, 0.32f, 0.45f, 0.65f, 0.9f };
        static int _k;
        static Light[] _raw = new Light[256]; static int _rawCount;

        static void Group(System.Collections.IList list, Vector3 cam)
        {
            int total = list.Count;
            if (_raw.Length < total) _raw = new Light[Math.Max(total, _raw.Length * 2)];
            if (_all.Length < total) _all = new Light[_raw.Length];
            bool fast = _rPos != null;
            for (int i = 0; i < total; i++)
            {
                Light l;
                if (fast) { l.P = _rPos(list, i); l.C = _rCol(list, i); l.I = _rInt(list, i); l.Down = _rDown(list, i); l.Surf = _rSurf(list, i); }
                else { var o = list[i]; l.P = (Vector3)_lPos.GetValue(o); l.C = (Vector3)_lCol.GetValue(o); l.I = (float)_lInt.GetValue(o); l.Down = (bool)_lDown.GetValue(o); l.Surf = (bool)_lSurf.GetValue(o); }
                l.D = Vector3.Distance(l.P, cam);
                _raw[i] = l;
            }
            _rawCount = total;
            if (total <= 24) { _k = 0; GroupWith(0f, cam); return; }
            int k = Math.Min(_k, Ks.Length - 1);
            if (k > 0 && GroupWith(Ks[k - 1], cam) <= 20) k--;             // sobra sitio: un poco menos
            while (GroupWith(Ks[k], cam) > 24 && k < Ks.Length - 1) k++;    // no caben: un poco más
            _k = k;
        }

        static int GroupWith(float k, Vector3 cam)
        {
            _allCount = 0; _cells.Clear();
            for (int i = 0; i < _rawCount; i++)
            {
                var l = _raw[i];
                // celda de 1, 2, 4, 8… m (la potencia de 2 por debajo de k × distancia); la rejilla es la misma en todas las
                // baldosas (2048 m), así que no se mueve con la cámara
                float want = k * l.D;
                if (want < 1f) { _all[_allCount++] = l; continue; }
                int level = Math.Min(10, (int)Math.Floor(Math.Log(want, 2)));
                float size = 1 << level;
                long key = ((long)level << 58) ^ ((long)(l.Down ? 1 : 0) << 57) ^ ((long)(l.Surf ? 1 : 0) << 56)
                         ^ (((long)Math.Floor(l.P.X / size) & 0xFFFFF) << 36) ^ (((long)Math.Floor(l.P.Y / size) & 0xFFFF) << 20) ^ ((long)Math.Floor(l.P.Z / size) & 0xFFFFF);
                if (_cells.TryGetValue(key, out int at))
                {
                    var g = _all[at]; float sum = g.I + l.I;
                    g.P = (g.P * g.I + l.P * l.I) / sum; g.C = (g.C * g.I + l.C * l.I) / sum; g.I = sum;
                    g.D = Vector3.Distance(g.P, cam);
                    _all[at] = g;
                }
                else { _cells[key] = _allCount; _all[_allCount++] = l; }
            }
            return _allCount;
        }
    }
}
