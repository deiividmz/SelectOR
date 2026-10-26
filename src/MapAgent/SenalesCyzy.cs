// Señales de CYZYRails (1.2.77): sus luces se veían casi blancas en el 3D, sobre todo de noche.
// Por qué (SceneryShader.mgfx de CYZY, técnicas SignalLight y SignalLightGlow, pixel shader 35): el color de la luz (el de
// la sigcfg de la ruta: en CGL, rojo 195,30,39 · verde 79,186,130 · amarillo 255,155,0) se pasa a lineal, se multiplica
// por SignalLightHdr (HighDefinitionRenderer: 3 de noche, 1,5 de día) y se vuelve a codificar en gamma ANTES del tone
// mapping, que lo trata como lineal. Así los canales secundarios suben mucho (el verde 79,186,130 sale ≈ 0,51/1,2/0,84,
// casi menta-blanco) y el tone mapping comprime el canal principal: el color se va a pastel o blanco. La autoexposición
// no tiene la culpa (de noche su EV queda entre −4 y 0: nunca aclara).
// Aquí, solo en CYZYRails y solo cómo se dibujan (los aspectos de las señales no se tocan):
//  · el color de cada luz de señal, a saturación y brillo máximos con su mismo tono (SignalLightPrimitive; ver Pure): rojo
//    puro, verde intenso, ámbar vivo; las blancas, igual;
//  · más intensas (petición): SignalLightHdr de día 1,5 → 2,4 y de noche 3 → 3,2 (otros valores ×1,6), y el halo ×1,4;
//  · (1.2.77, «de lejos bien, de cerca blancas») sin la luz de punto que CYZY pone en el centro de cada luz encendida
//    (SignalShapeHead.PrepareFrame → RenderFrame.AddPointLight, 60 cd): el sombreador solo limita la distancia a 5 cm
//    (d² ≥ 0,0025), así que la lente y la visera de la propia señal recibían ~24 000 lux y de cerca se veían quemadas en
//    blanco. El color lo dan la lente y su halo (SignalLight/SignalLightGlow), que no usan esa luz. Y CYZY solo usa las
//    24 luces más cercanas: las de las señales ya no les quitan sitio a las farolas y marquesinas;
//  · (1.2.77, «quita el filtro blanco») la textura de la lente (en CGL SigLight.ace: una lente con relieve en grises y
//    blancos; también los indicadores con letras y números) no se tiñe: el sombreador 35 hace lerp(textura, color, R de
//    la textura), y un gris de 0,7 suma 0,21 de blanco a los tres canales. De cerca la lente ocupa mucho y se veía con
//    un velo blanco (de lejos manda el halo, que es blanco con transparencia: color puro). Aquí, al cargar cada material de
//    luz de señal (SignalLightMaterial), su textura (en memoria; la de la ruta no se toca) pasa a blanco o negro: blanco
//    donde no es oscura (la lente sale del color puro de la luz), negro donde lo es (las letras siguen nítidas). La
//    transparencia, igual.

using System;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace SelectOR.MapAgent
{
    public static class SenalesCyzy
    {
        public const float Purity = 3.2f;         // (1.2.77) el canal intermedio, elevado: CYZY lo pasa a lineal ×HDR y vuelve antes del tone mapping, que lo infla
                                                  //  (con 1: verde 0,255,101 → en pantalla 0,238,209, casi turquesa; con 3,2: ≈ 0,238,115 · ámbar ≈ 238,170,0 · rojo 238,0,0)
        public const double MinSat = 0.30;        // por debajo (blancos, lunares), el color no se toca
        public const float NightHdr = 3.2f;       // de noche (CYZY: 3); (1.2.77, «más intenso») con el color ya puro no se va a blanco
        public const float DayHdr = 2.4f;         // de día (CYZY: 1,5)
        public const float OtherBoost = 1.6f;     // con la cámara física u otros valores
        public const float GlowBoost = 1.4f;      // el halo de cada luz (GlowIntensityDay/Night de la sigcfg)

        // (desde Sol.Install, cuando ya están los tipos del 3D) 1 = puesto.
        public static int Install(Harmony h)
        {
            try
            {
                if (AccessTools.TypeByName("Orts.Viewer3D.LampDefinitions") == null) return 0;   // (no es CYZYRails)
                int n = 0;
                var prim = AccessTools.TypeByName("Orts.Viewer3D.SignalLightPrimitive");
                var ctor = prim == null ? null : AccessTools.GetDeclaredConstructors(prim).Find(c => Array.Exists(c.GetParameters(), p => p.Name == "color" && p.ParameterType == typeof(Color)));
                if (ctor != null) { h.Patch(ctor, prefix: new HarmonyMethod(typeof(SenalesCyzy), nameof(BeforePrimitive))); n++; }
                var sh = AccessTools.TypeByName("Orts.Viewer3D.SceneryShader");
                var set = sh == null ? null : AccessTools.PropertySetter(sh, "SignalLightHdr");
                if (set != null) { h.Patch(set, prefix: new HarmonyMethod(typeof(SenalesCyzy), nameof(BeforeHdr))); n++; }
                var head = AccessTools.TypeByName("Orts.Viewer3D.SignalShape+SignalShapeHead");
                var prep = head == null ? null : AccessTools.Method(head, "PrepareFrame");
                var add = AccessTools.Method(AccessTools.TypeByName("Orts.Viewer3D.RenderFrame"), "AddPointLight");
                if (prep != null && add != null)
                {
                    h.Patch(prep, prefix: new HarmonyMethod(typeof(SenalesCyzy), nameof(BeforeHead)), finalizer: new HarmonyMethod(typeof(SenalesCyzy), nameof(AfterHead)));
                    h.Patch(add, prefix: new HarmonyMethod(typeof(SenalesCyzy), nameof(BeforePointLight)));
                    n++;
                }
                var mat = AccessTools.TypeByName("Orts.Viewer3D.SignalLightMaterial");
                var mctor = mat == null ? null : AccessTools.GetDeclaredConstructors(mat).Find(c => c.GetParameters().Length == 2);
                if (mctor != null) { h.Patch(mctor, postfix: new HarmonyMethod(typeof(SenalesCyzy), nameof(AfterMaterial))); n++; }
                Agent.Log($"CYZYRails: luces de las señales con su color ({n} de 4)");
                return n > 0 ? 1 : 0;
            }
            catch (Exception ex) { Agent.Log("señales CYZY: " + ex.GetType().Name + " " + ex.Message); return 0; }
        }

        static void BeforePrimitive(ref Color color, ref float glowDay, ref float glowNight) { try { color = Pure(color); glowDay *= GlowBoost; glowNight *= GlowBoost; } catch { } }

        // (1.2.77) mientras se prepara una cabeza de señal, sus luces de punto no se añaden (las de las farolas, sí)
        [ThreadStatic] static bool _inHead;
        static void BeforeHead() { _inHead = true; }
        static Exception AfterHead(Exception __exception) { _inHead = false; return __exception; }
        static bool BeforePointLight(bool surface) => !(_inHead && !surface);

        // (1.2.77) la textura de la lente, a blanco y negro (una vez por textura: la comparten los materiales iguales)
        public const int Threshold = 64;   // de 0 a 255: por debajo, negro (el fondo de las letras)
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, object> _done = new System.Runtime.CompilerServices.ConditionalWeakTable<object, object>();
        static void AfterMaterial(object __instance)
        {
            try
            {
                var tex = AccessTools.Field(__instance.GetType(), "Texture")?.GetValue(__instance) as Microsoft.Xna.Framework.Graphics.Texture2D;
                if (tex == null || tex.IsDisposed || _done.TryGetValue(tex, out _)) return;
                var missing = AccessTools.Field(AccessTools.TypeByName("Orts.Viewer3D.SharedMaterialManager"), "MissingTexture")?.GetValue(null);
                if (ReferenceEquals(tex, missing)) return;   // (la de «falta la textura» la usan muchas cosas)
                _done.Add(tex, null);
                if (tex.Format != Microsoft.Xna.Framework.Graphics.SurfaceFormat.Color) { Agent.Log($"señales CYZY: textura de luz en {tex.Format}: se deja como está"); return; }
                for (int lv = 0; lv < tex.LevelCount; lv++)
                {
                    int w = Math.Max(1, tex.Width >> lv), hh = Math.Max(1, tex.Height >> lv);
                    var px = new Color[w * hh];
                    tex.GetData(lv, null, px, 0, px.Length);
                    for (int i = 0; i < px.Length; i++) px[i] = Lens(px[i]);
                    tex.SetData(lv, null, px, 0, px.Length);
                }
            }
            catch (Exception ex) { Agent.Log("señales CYZY: textura de luz: " + ex.GetType().Name + " " + ex.Message); }
        }
        // (pura: las pruebas) un píxel de la lente: blanco si no es oscuro, negro si lo es; la transparencia, igual
        public static Color Lens(Color c)
        {
            if (c.A == 0) return c;
            int m = Math.Max(c.R, Math.Max(c.G, c.B));
            return m >= Threshold ? new Color((byte)255, (byte)255, (byte)255, c.A) : new Color((byte)0, (byte)0, (byte)0, c.A);
        }

        // HighDefinitionRenderer: Lerp(3, 1,5, día) con el canal HDR; 5/9 con la cámara física; 1 sin él
        static void BeforeHdr(ref float value) { value = Hdr(value); }

        // (puras: las pruebas) (1.2.77, «potencia mucho más el color») cada luz de color, a saturación y brillo máximos con
        // su mismo tono (HSV: S = 1, V = 1): el canal más bajo a 0, el más alto a 255 y el intermedio en su proporción
        // (con Purity, un poco hacia 0). Las blancas y casi blancas (saturación < MinSat), igual.
        public static Color Pure(Color c)
        {
            int m = Math.Max(c.R, Math.Max(c.G, c.B)), n = Math.Min(c.R, Math.Min(c.G, c.B));
            if (m <= 0 || (m - n) / (double)m < MinSat) return c;
            byte F(byte v) => (byte)Math.Round(255 * Math.Pow((v - n) / (double)(m - n), Purity));
            return new Color(F(c.R), F(c.G), F(c.B), c.A);
        }
        // (1.2.77) de día (1,5) a DayHdr, de noche (3) a NightHdr, entre medias en proporción; lo demás, ×OtherBoost
        public static float Hdr(float v) => v >= 1.499f && v <= 3.001f ? DayHdr + (v - 1.5f) / 1.5f * (NightHdr - DayHdr) : v * OtherBoost;
    }
}
