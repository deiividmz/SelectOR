// Velocidad prefijada del control de crucero, al mantener la tecla (2026-11-08, petición): en New Year se mueve 5 veces
// más deprisa que en Estable, Testing o CYZYRails, y se quiere como en New Year.
//  · New Year: la velocidad prefijada es un mando de muescas (SpeedSelectorController, MSTSNotchController) con
//    StepSize = paso / velocidad máxima / SpeedSelectorStepTimeSeconds, y su Update lo mueve con «boost» 5: al pulsar,
//    un paso enseguida; mientras se mantiene, 5 pasos por cada SpeedSelectorStepTimeSeconds.
//  · Las demás (CruiseControl con «selectedSpeedIncreasing»): UpdateSelectedSpeed llama en cada vuelta a
//    SpeedRegulatorSelectedSpeedIncrease/Decrease, que dan un paso como mucho cada SpeedSelectorStepTimeSeconds.
// Aquí, solo en las segundas: cada vuelta se cuentan los pasos que tocan a ese ritmo de New Year (5 por intervalo, con lo
// que sobra para la siguiente vuelta) y se dan; el primero de cada pulsación, enseguida, como en New Year. Si el .eng no
// trae SpeedSelectorStepTimeSeconds (0), no se toca: ya da un paso por vuelta.

using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace SelectOR.MapAgent
{
    static class Crucero
    {
        const float Boost = 5f;   // el de MSTSNotchController.Update en New Year
        static FieldInfo _total, _hold, _stepTime;
        static MethodInfo _inc, _dec;
        [ThreadStatic] static bool _inside;

        sealed class State { public float Last = -1; public float Acc; public int Dir; }
        static readonly ConditionalWeakTable<object, State> _st = new ConditionalWeakTable<object, State>();

        public static int Install(Harmony h)
        {
            try
            {
                var t = AccessTools.TypeByName("Orts.Simulation.RollingStocks.SubSystems.CruiseControl");
                if (t == null) return 0;
                // (New Year ya lo hace así: no tiene este mecanismo)
                if (AccessTools.Field(t, "selectedSpeedIncreasing") == null) return 0;
                _total = AccessTools.Field(t, "TotalTime");
                _hold = AccessTools.Field(t, "selectedSpeedLeverHoldTime");
                _stepTime = AccessTools.Field(t, "SpeedSelectorStepTimeSeconds");
                _inc = AccessTools.Method(t, "SpeedRegulatorSelectedSpeedIncrease", Type.EmptyTypes);
                _dec = AccessTools.Method(t, "SpeedRegulatorSelectedSpeedDecrease", Type.EmptyTypes);
                if (_total == null || _hold == null || _stepTime == null || _inc == null || _dec == null) return 0;
                if (_hold.FieldType != typeof(double) || _total.FieldType != typeof(float) || _stepTime.FieldType != typeof(float)) return 0;
                h.Patch(_inc, prefix: new HarmonyMethod(typeof(Crucero), nameof(BeforeIncrease)));
                h.Patch(_dec, prefix: new HarmonyMethod(typeof(Crucero), nameof(BeforeDecrease)));
                Agent.Log("control de crucero: la velocidad prefijada se mueve al ritmo de New Year (5 pasos por intervalo del .eng)");
                return 1;
            }
            catch (Exception ex) { Agent.Log("control de crucero: " + ex.GetType().Name + " " + ex.Message); return 0; }
        }

        static bool BeforeIncrease(object __instance) => Step(__instance, 1, _inc);
        static bool BeforeDecrease(object __instance) => Step(__instance, -1, _dec);

        // true: que corra el original tal cual (una vez); false: ya se han dado aquí los pasos que tocaban (o ninguno)
        static bool Step(object cc, int dir, MethodInfo m)
        {
            if (_inside) return true;
            try
            {
                float st = (float)_stepTime.GetValue(cc);
                if (!(st > 0)) return true;
                float now = (float)_total.GetValue(cc);
                var s = _st.GetValue(cc, _ => new State());
                int n;
                // una pulsación nueva (o se había parado más de un intervalo): un paso enseguida, como New Year
                if (s.Dir != dir || s.Last < 0 || now - s.Last > Math.Max(0.25f, 2 * st) || now < s.Last) { s.Acc = 0; n = 1; }
                else
                {
                    s.Acc += (now - s.Last) * Boost / st;
                    n = (int)Math.Floor(s.Acc);
                    if (n > 10) n = 10;   // (una vuelta muy larga: sin saltos enormes)
                    s.Acc -= (float)Math.Floor(s.Acc);
                }
                s.Last = now; s.Dir = dir;
                _inside = true;
                try
                {
                    for (int i = 0; i < n; i++)
                    {
                        _hold.SetValue(cc, double.MinValue / 4);   // (que el original no espere su intervalo)
                        m.Invoke(cc, null);
                    }
                }
                finally { _inside = false; }
                return false;
            }
            catch { _inside = false; return true; }
        }
    }
}
