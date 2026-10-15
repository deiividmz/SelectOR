// Mandos de juego para conducir en Open Rails (Ajustes de SelectOR → Mandos): joysticks, cuadrantes de palancas,
// volantes… Se leen con la API clásica de Windows (winmm: joyGetPosEx), que se puede leer aunque SelectOR no sea la
// ventana activa (al conducir lo es Open Rails): hasta 16 dispositivos, 6 ejes (X Y Z R U V) y 32 botones cada uno. El
// nombre de cada mando es el que le da Windows (registro: OEMName por VID/PID).
//  · Cada palanca o botón se asigna a una acción (JoyBinding, en AppPrefs): las palancas, a un mando analógico del
//    pupitre de Open Rails (regulador, frenos, inversor), que el agente del mapa pone en su «dispositivo web» (el mismo
//    camino que su servidor web, POST /API/CABCONTROLS); los botones, a una orden de Open Rails por su NOMBRE (cada
//    versión las numera distinto), que el agente pulsa como un botón de dispositivo externo (el del RailDriver).
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
        public string Device { get; set; }      // VID:PID (y #n si hay varios iguales)
        public string DeviceName { get; set; }  // para enseñarlo aunque no esté conectado
        public bool Axis { get; set; }          // palanca (eje) o botón
        public int Index { get; set; }          // eje 0–5 (X Y Z R U V) o botón 0–31
        public string Action { get; set; }      // THROTTLE · TRAIN_BRAKE · … (palancas) u orden de Open Rails (botones)
        public bool Invert { get; set; }
        public double Dead { get; set; } = 0.03; // zona muerta en cada extremo (y en el centro del inversor)
    }

    public static class Joy
    {
        // ---------------- dispositivos ----------------
        public sealed class Device
        {
            public int Id; public string Key, Name; public int Axes, Buttons;
            public uint[] Min = new uint[6], Max = new uint[6];
        }
        public sealed class State { public readonly float[] Axes = new float[6]; public uint Buttons; }

        public interface ISource
        {
            List<Device> Enumerate();
            bool Read(Device d, State s);
        }

        public static ISource Source = new WinMM();

        static readonly string[] AxisNames = { "X", "Y", "Z", "R", "U", "V" };
        public static string AxisName(int i) => i >= 0 && i < 6 ? AxisNames[i] : "?";

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
            const uint JOY_RETURNALL = 0xFF;

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
                        l.Add(new Device
                        {
                            Id = id, Key = key, Name = OemName(c.wMid, c.wPid) ?? (string.IsNullOrWhiteSpace(c.szPname) ? "Mando " + (id + 1) : c.szPname.Trim()),
                            Axes = (int)Math.Min(6, c.wNumAxes), Buttons = (int)Math.Min(32, c.wNumButtons),
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
                var info = new JOYINFOEX { dwSize = (uint)Marshal.SizeOf<JOYINFOEX>(), dwFlags = JOY_RETURNALL };
                try { if (joyGetPosEx(d.Id, ref info) != 0) return false; } catch { return false; }
                uint[] v = { info.dwXpos, info.dwYpos, info.dwZpos, info.dwRpos, info.dwUpos, info.dwVpos };
                for (int i = 0; i < 6; i++)
                {
                    double span = (double)d.Max[i] - d.Min[i];
                    s.Axes[i] = span > 0 ? (float)Math.Max(0, Math.Min(1, (v[i] - (double)d.Min[i]) / span)) : 0.5f;
                }
                s.Buttons = info.dwButtons;
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

        // ---------------- acciones ----------------
        // Palancas: el mando analógico del pupitre de Open Rails (CABViewControlTypes).
        public static readonly (string id, string label)[] AxisActions =
        {
            ("THROTTLE", "Regulador"),
            ("TRAIN_BRAKE", "Freno de tren"),
            ("ENGINE_BRAKE", "Freno de locomotora"),
            ("DYNAMIC_BRAKE", "Freno dinámico"),
            ("DIRECTION", "Inversor"),
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
            ("ControlTrainBrakeIncrease", "Apretar freno de tren"),
            ("ControlTrainBrakeDecrease", "Aflojar freno de tren"),
            ("ControlEngineBrakeIncrease", "Apretar freno de locomotora"),
            ("ControlEngineBrakeDecrease", "Aflojar freno de locomotora"),
            ("ControlBailOff", "Afloje (bail-off)"),
            ("ControlEmergencyPushButton", "Seta de emergencia"),
            ("ControlAlerter", "Hombre muerto (alertador)"),
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
        // Regulador y frenos: 0…1, con zona muerta en los extremos (para llegar seguro a 0 y a 1). Inversor: −1…1 con
        // zona muerta en el centro (Open Rails: < −0,5 atrás, > 0,5 adelante; en vapor, el corte de admisión).
        public static float Value(JoyBinding b, float raw)
        {
            double v = b.Invert ? 1 - raw : raw, d = Math.Max(0, Math.Min(0.2, b.Dead));
            if (b.Action == "DIRECTION")
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
