// Mandos de juego para conducir en Open Rails (Ajustes de SelectOR → Mandos): joysticks, cuadrantes de palancas,
// volantes, mandos de PC, de PlayStation, de Xbox… Se leen aunque SelectOR no sea la ventana activa (al conducir lo es
// Open Rails), de dos maneras:
//  · La API clásica de Windows (winmm: joyGetPosEx): cualquier mando que Windows reconozca (hasta 16), 6 ejes
//    (X Y Z R U V), 32 botones y la cruceta (POV), que sale como 4 botones más (33–36). El nombre es el que le da
//    Windows (registro: OEMName por VID/PID).
//  · XInput: los mandos de Xbox (y los compatibles, «modo X»), hasta 4, con los gatillos por separado (por winmm van los
//    dos en el mismo eje). Si hay alguno, sus copias en winmm no se enseñan (saldría el mismo mando dos veces).
//  · Cada palanca o botón se asigna a una acción (JoyBinding, en AppPrefs): las palancas, a un mando analógico del
//    pupitre de Open Rails (regulador, frenos, inversor, el combinado regulador + freno dinámico y la velocidad del
//    control de crucero), que el agente del mapa pone en Open Rails; los botones, a una orden de Open Rails por su NOMBRE
//    (cada versión las numera distinto), que el agente pulsa como un botón de dispositivo externo (el del RailDriver).
//  · Source: de dónde se leen (las pruebas ponen uno simulado).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace SelectOR
{
    // Una asignación: tal palanca (eje) o botón de tal mando → tal acción.
    public sealed class JoyBinding
    {
        public string Device { get; set; }      // VID:PID (y #n si hay varios iguales) · XINPUT:n (mando de Xbox)
        public string DeviceName { get; set; }  // para enseñarlo aunque no esté conectado
        public bool Axis { get; set; }          // palanca (eje) o botón
        public int Index { get; set; }          // eje 0–5 o botón (0–31; 32–35 la cruceta)
        public string Action { get; set; }      // THROTTLE · TRAIN_BRAKE · … (palancas) u orden de Open Rails (botones)
        public bool Invert { get; set; }
        public double Dead { get; set; } = 0.03; // zona muerta en cada extremo (y en el centro de las palancas con centro)
    }

    public static class Joy
    {
        // ---------------- dispositivos ----------------
        public sealed class Device
        {
            public int Id; public string Key, Name; public int Axes, Buttons;
            public uint[] Min = new uint[6], Max = new uint[6];
            public bool Pov;           // (winmm) con cruceta: sus 4 direcciones son los botones 33–36
            public bool XInput;        // mando de Xbox (XInput)
        }
        public sealed class State { public readonly float[] Axes = new float[6]; public ulong Buttons; }

        public interface ISource
        {
            List<Device> Enumerate();
            bool Read(Device d, State s);
        }

        public static ISource Source = new Both();

        public const int PovFirst = 32;   // botones de la cruceta: 32 ↑, 33 →, 34 ↓, 35 ←
        static readonly string[] AxisNames = { "X", "Y", "Z", "R", "U", "V" };
        static readonly string[] XAxisNames = { "Stick izq. ↔", "Stick izq. ↕", "Stick der. ↔", "Stick der. ↕", "Gatillo izq. (LT)", "Gatillo der. (RT)" };
        static readonly string[] XButtonNames = { "A", "B", "X", "Y", "LB", "RB", "Back", "Start", "Stick izq. (LS)", "Stick der. (RS)", "Cruceta ↑", "Cruceta ↓", "Cruceta ←", "Cruceta →" };
        static readonly string[] PovNames = { "Cruceta ↑", "Cruceta →", "Cruceta ↓", "Cruceta ←" };

        public static bool IsXInput(string deviceKey) => deviceKey != null && deviceKey.StartsWith("XINPUT:", StringComparison.Ordinal);
        public static string AxisName(int i) => i >= 0 && i < 6 ? AxisNames[i] : "?";
        public static string AxisName(string deviceKey, int i) => IsXInput(deviceKey) && i >= 0 && i < 6 ? I18n.T(XAxisNames[i]) : AxisName(i);
        public static string ButtonName(string deviceKey, int i)
        {
            if (IsXInput(deviceKey)) return i >= 0 && i < XButtonNames.Length ? I18n.T(XButtonNames[i]) : (i + 1).ToString();
            if (i >= PovFirst && i < PovFirst + 4) return I18n.T(PovNames[i - PovFirst]);
            return (i + 1).ToString();
        }
        public static bool Down(State s, int i) => i >= 0 && i < 64 && (s.Buttons & (1UL << i)) != 0;

        // Los dos a la vez: XInput (Xbox) y winmm (el resto), sin repetir los de Xbox.
        sealed class Both : ISource
        {
            readonly WinMM _mm = new WinMM(); readonly XInput _x = new XInput();
            public List<Device> Enumerate()
            {
                var xs = _x.Enumerate();
                var mm = _mm.Enumerate();
                if (xs.Count > 0) mm.RemoveAll(d => d.Key.StartsWith("045E:", StringComparison.OrdinalIgnoreCase)
                                                     || d.Name.IndexOf("xbox", StringComparison.OrdinalIgnoreCase) >= 0
                                                     || d.Name.IndexOf("xinput", StringComparison.OrdinalIgnoreCase) >= 0);
                xs.AddRange(mm);
                return xs;
            }
            public bool Read(Device d, State s) => d.XInput ? _x.Read(d, s) : _mm.Read(d, s);
        }

        sealed class WinMM : ISource
        {
            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            struct JOYCAPSW
            {
                public ushort wMid, wPid;
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szPname;
                public uint wXmin, wXmax, wYmin, wYmax, wZmin, wZmax, wNumButtons, wPeriodMin, wPeriodMax, wRmin, wRmax, wUmin, wUmax, wVmin, wVmax, wCaps, wMaxAxes, wNumAxes, wMaxButtons;
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szRegKey;
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szOEMVxD;
            }
            [StructLayout(LayoutKind.Sequential)]
            struct JOYINFOEX { public uint dwSize, dwFlags, dwXpos, dwYpos, dwZpos, dwRpos, dwUpos, dwVpos, dwButtons, dwButtonNumber, dwPOV, dwReserved1, dwReserved2; }
            [DllImport("winmm.dll")] static extern uint joyGetNumDevs();
            [DllImport("winmm.dll", CharSet = CharSet.Unicode)] static extern int joyGetDevCapsW(IntPtr id, ref JOYCAPSW caps, int size);
            [DllImport("winmm.dll")] static extern int joyGetPosEx(int id, ref JOYINFOEX info);
            const uint JOY_RETURNALL = 0xFF, JOY_RETURNPOVCTS = 0x200, JOYCAPS_HASPOV = 0x10;

            public List<Device> Enumerate()
            {
                var l = new List<Device>();
                try
                {
                    uint n = Math.Min(16u, joyGetNumDevs());
                    var seen = new Dictionary<string, int>();
                    for (int id = 0; id < n; id++)
                    {
                        var info = new JOYINFOEX { dwSize = (uint)Marshal.SizeOf<JOYINFOEX>(), dwFlags = JOY_RETURNALL };
                        if (joyGetPosEx(id, ref info) != 0) continue;   // no conectado
                        var c = new JOYCAPSW();
                        if (joyGetDevCapsW((IntPtr)id, ref c, Marshal.SizeOf<JOYCAPSW>()) != 0) continue;
                        string key = $"{c.wMid:X4}:{c.wPid:X4}";
                        seen[key] = seen.TryGetValue(key, out int k) ? k + 1 : 1;
                        if (seen[key] > 1) key += "#" + seen[key];
                        bool pov = (c.wCaps & JOYCAPS_HASPOV) != 0;
                        l.Add(new Device
                        {
                            Id = id, Key = key, Name = OemName(c.wMid, c.wPid) ?? (string.IsNullOrWhiteSpace(c.szPname) ? "Mando " + (id + 1) : c.szPname.Trim()),
                            Axes = (int)Math.Min(6, c.wNumAxes), Buttons = (int)Math.Min(32, c.wNumButtons), Pov = pov,
                            Min = new[] { c.wXmin, c.wYmin, c.wZmin, c.wRmin, c.wUmin, c.wVmin },
                            Max = new[] { c.wXmax, c.wYmax, c.wZmax, c.wRmax, c.wUmax, c.wVmax }
                        });
                    }
                }
                catch { }
                return l;
            }

            public bool Read(Device d, State s)
            {
                var info = new JOYINFOEX { dwSize = (uint)Marshal.SizeOf<JOYINFOEX>(), dwFlags = JOY_RETURNALL | JOY_RETURNPOVCTS };
                try { if (joyGetPosEx(d.Id, ref info) != 0) return false; } catch { return false; }
                uint[] v = { info.dwXpos, info.dwYpos, info.dwZpos, info.dwRpos, info.dwUpos, info.dwVpos };
                for (int i = 0; i < 6; i++)
                {
                    double span = (double)d.Max[i] - d.Min[i];
                    s.Axes[i] = span > 0 ? (float)Math.Max(0, Math.Min(1, (v[i] - (double)d.Min[i]) / span)) : 0.5f;
                }
                ulong b = info.dwButtons;
                // la cruceta: centésimas de grado (0 arriba, 9000 derecha…), 0xFFFF suelta; en diagonal, las dos
                uint pov = info.dwPOV;
                if (d.Pov && pov <= 36000)
                {
                    double a = pov / 100.0;
                    bool In(double c) => Math.Abs(((a - c) % 360 + 540) % 360 - 180) < 67.5;
                    if (In(0)) b |= 1UL << PovFirst;
                    if (In(90)) b |= 1UL << (PovFirst + 1);
                    if (In(180)) b |= 1UL << (PovFirst + 2);
                    if (In(270)) b |= 1UL << (PovFirst + 3);
                }
                s.Buttons = b;
                return true;
            }

            // El nombre que Windows da al mando («Saitek Pro Flight Throttle Quadrant»…).
            static string OemName(ushort vid, ushort pid)
            {
                foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
                    try
                    {
                        using var k = root.OpenSubKey($@"System\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM\VID_{vid:X4}&PID_{pid:X4}");
                        if (k?.GetValue("OEMName") is string s && s.Trim().Length > 0) return s.Trim();
                    }
                    catch { }
                return null;
            }
        }

        // Mandos de Xbox: 6 ejes (sticks y gatillos, 0…1; los sticks en reposo, 0,5) y 14 botones.
        sealed class XInput : ISource
        {
            [StructLayout(LayoutKind.Sequential)]
            struct XINPUT_STATE { public uint dwPacketNumber; public ushort wButtons; public byte bLeftTrigger, bRightTrigger; public short sThumbLX, sThumbLY, sThumbRX, sThumbRY; }
            [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")] static extern int Get14(int user, out XINPUT_STATE st);
            [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetState")] static extern int Get910(int user, out XINPUT_STATE st);
            int _api;   // 0 sin probar · 1 xinput1_4 · 2 xinput9_1_0 · −1 ninguna

            bool Get(int i, out XINPUT_STATE st)
            {
                st = default;
                try
                {
                    if (_api == 0) { try { Get14(0, out _); _api = 1; } catch { try { Get910(0, out _); _api = 2; } catch { _api = -1; } } }
                    if (_api == 1) return Get14(i, out st) == 0;
                    if (_api == 2) return Get910(i, out st) == 0;
                }
                catch { }
                return false;
            }

            public List<Device> Enumerate()
            {
                var l = new List<Device>();
                for (int i = 0; i < 4; i++)
                    if (Get(i, out _))
                        l.Add(new Device { Id = i, Key = "XINPUT:" + (i + 1), Name = string.Format(I18n.T("Mando de Xbox {0}"), i + 1), Axes = 6, Buttons = 14, XInput = true });
                return l;
            }

            // (orden de los botones: A B X Y LB RB Back Start LS RS ↑ ↓ ← →)
            static readonly ushort[] Bits = { 0x1000, 0x2000, 0x4000, 0x8000, 0x0100, 0x0200, 0x0020, 0x0010, 0x0040, 0x0080, 0x0001, 0x0002, 0x0004, 0x0008 };

            public bool Read(Device d, State s)
            {
                if (!Get(d.Id, out var st)) return false;
                static float Stick(short v) => (float)Math.Max(0, Math.Min(1, (v + 32768.0) / 65535.0));
                s.Axes[0] = Stick(st.sThumbLX); s.Axes[1] = 1 - Stick(st.sThumbLY);   // (↕: arriba = 0, como un joystick)
                s.Axes[2] = Stick(st.sThumbRX); s.Axes[3] = 1 - Stick(st.sThumbRY);
                s.Axes[4] = st.bLeftTrigger / 255f; s.Axes[5] = st.bRightTrigger / 255f;
                ulong b = 0;
                for (int i = 0; i < Bits.Length; i++) if ((st.wButtons & Bits[i]) != 0) b |= 1UL << i;
                s.Buttons = b;
                return true;
            }
        }

        // ---------------- acciones ----------------
        // Palancas: el mando analógico del pupitre de Open Rails (CABViewControlTypes), y dos de SelectOR:
        //  · THROTTLE_DYNAMIC: una sola palanca con el centro en reposo: hacia un lado el regulador, hacia el otro el
        //    freno dinámico (las locomotoras con mando combinado).
        //  · CRUISE_SPEED: la velocidad del control de crucero (la del triángulo amarillo del pupitre), de 0 a la máxima
        //    del servicio (clic derecho en el velocímetro) o, si no, a la del tren; de 5 en 5 km/h.
        public static readonly (string id, string label)[] AxisActions =
        {
            ("THROTTLE", "Regulador"),
            ("TRAIN_BRAKE", "Freno de tren"),
            ("ENGINE_BRAKE", "Freno de locomotora"),
            ("DYNAMIC_BRAKE", "Freno dinámico"),
            ("THROTTLE_DYNAMIC", "Regulador + freno dinámico"),
            ("CRUISE_SPEED", "Control de crucero: velocidad"),
            ("DIRECTION", "Inversor"),
        };

        // Palancas con el reposo en el centro (−1…1).
        public static bool Centered(string action) => action == "DIRECTION" || action == "THROTTLE_DYNAMIC";

        // Las que no pueden ir a la vez (mandan lo mismo a Open Rails): al asignar una, se quitan las otras.
        public static string[] Conflicts(string action) => action switch
        {
            "THROTTLE_DYNAMIC" => new[] { "THROTTLE", "DYNAMIC_BRAKE" },
            "THROTTLE" or "DYNAMIC_BRAKE" => new[] { "THROTTLE_DYNAMIC" },
            _ => Array.Empty<string>()
        };

        // Botones: órdenes de Open Rails por su nombre («|»: la primera que tenga esa versión).
        public static readonly (string id, string label)[] ButtonActions =
        {
            ("ControlHorn", "Bocina"),
            ("ControlBell", "Campana"),
            ("ControlSander", "Arenero"),
            ("ControlForwards", "Inversor adelante"),
            ("ControlBackwards", "Inversor atrás"),
            ("ControlThrottleIncrease", "Subir regulador"),
            ("ControlThrottleDecrease", "Bajar regulador"),
            ("ControlDynamicBrakeIncrease", "Subir freno dinámico"),
            ("ControlDynamicBrakeDecrease", "Bajar freno dinámico"),
            ("ControlTrainBrakeIncrease", "Apretar freno de tren"),
            ("ControlTrainBrakeDecrease", "Aflojar freno de tren"),
            ("ControlEngineBrakeIncrease", "Apretar freno de locomotora"),
            ("ControlEngineBrakeDecrease", "Aflojar freno de locomotora"),
            ("ControlBailOff", "Afloje (bail-off)"),
            ("ControlEmergencyPushButton", "Seta de emergencia"),
            ("ControlAlerter", "Hombre muerto (alertador)"),
            ("ControlCruiseControlModeIncrease", "Control de crucero: modo +"),
            ("ControlCruiseControlModeDecrease", "Control de crucero: modo −"),
            ("ControlSpeedRegulatorModeIncrease", "Control de crucero: manual / automático +"),
            ("ControlSpeedRegulatorModeDecrease", "Control de crucero: manual / automático −"),
            ("ControlSpeedRegulatorSelectedSpeedIncrease", "Control de crucero: subir velocidad"),
            ("ControlSpeedRegulatorSelectedSpeedDecrease", "Control de crucero: bajar velocidad"),
            ("ControlSpeedRegulatorSelectedSpeedToZero", "Control de crucero: velocidad a cero"),
            ("ControlSpeedRegulatorMaxAccelerationIncrease", "Control de crucero: más aceleración"),
            ("ControlSpeedRegulatorMaxAccelerationDecrease", "Control de crucero: menos aceleración"),
            ("ControlPantograph1", "Pantógrafo 1"),
            ("ControlPantograph2", "Pantógrafo 2"),
            ("ControlCircuitBreakerClosingOrder", "Cerrar disyuntor"),
            ("ControlHeadlightIncrease", "Luces: subir"),
            ("ControlHeadlightDecrease", "Luces: bajar"),
            ("ControlWiper", "Limpiaparabrisas"),
            ("ControlDoorLeft", "Puertas izquierda"),
            ("ControlDoorRight", "Puertas derecha"),
            ("ControlHandbrakeFull", "Freno de mano: apretar"),
            ("ControlHandbrakeNone", "Freno de mano: soltar"),
            ("GameSwitchAhead|GameFacingSwitchAhead", "Aguja de delante"),
            ("GameSwitchBehind|GameFacingSwitchBehind", "Aguja de detrás"),
            ("ControlCabRadio", "Radio de cabina"),
            ("GamePause", "Pausa"),
            ("GameScreenshot", "Captura de pantalla"),
        };

        public static string Label(string action)
        {
            foreach (var a in AxisActions) if (a.id == action) return I18n.T(a.label);
            foreach (var a in ButtonActions) if (a.id == action) return I18n.T(a.label);
            return action;
        }

        // ---------------- de la palanca al valor que va a Open Rails ----------------
        // Regulador, frenos y crucero: 0…1, con zona muerta en los extremos (para llegar seguro a 0 y a 1). Inversor y
        // combinado: −1…1 con zona muerta en el centro (inversor: < −0,5 atrás, > 0,5 adelante; en vapor, el corte de
        // admisión · combinado: + regulador, − freno dinámico).
        public static float Value(JoyBinding b, float raw)
        {
            double v = b.Invert ? 1 - raw : raw, d = Math.Max(0, Math.Min(0.2, b.Dead));
            if (Centered(b.Action))
            {
                double c = v * 2 - 1;   // −1…1
                if (Math.Abs(c) < Math.Max(d, 0.08)) return 0;
                double s = Math.Sign(c), m = (Math.Abs(c) - Math.Max(d, 0.08)) / (1 - Math.Max(d, 0.08) - d);
                return (float)(s * Math.Max(0, Math.Min(1, m)));
            }
            if (v <= d) return 0;
            if (v >= 1 - d) return 1;
            return (float)((v - d) / (1 - 2 * d));
        }
    }
}
