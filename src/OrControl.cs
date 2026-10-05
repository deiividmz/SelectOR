// Mandar órdenes a Open Rails desde el pupitre: se pulsa la MISMA tecla que usaría el maquinista,
// tomada de la configuración de teclado de Open Rails del propio usuario (Opciones → Teclado), así
// que si la ha cambiado, se respeta. Se envía como pulsación real (SendInput con código de
// exploración) a la ventana del simulador, que sigue siendo la activa porque el pupitre no roba el
// foco. Las órdenes «de mantener» (bocinas, arena, disyuntor) quedan pulsadas mientras se mantiene
// el botón del ratón; las demás, una pulsación corta que el simulador llega a leer.
// IMPORTANTE: cada versión de OR numera sus órdenes a su manera (en las New Year van 3 puestos
// por delante de la Testing, en la NewYear MG uno por detrás…). Por eso las órdenes se piden por
// NOMBRE («ControlHorn») y el número se busca en la DLL de OR que esté cargada; con el número fijo
// de la versión con la que se compila SelectOR, en otra versión se pulsaba la tecla de otra orden.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using ORTS.Common.Input;
using ORTS.Settings;

namespace SelectOR
{
    static class OrControl
    {
        static InputSettings _input;

        // Mientras se manda una orden (y un rato después) el pupitre NO consulta al simulador: el
        // servidor web de OR responde desde otro hilo leyendo el estado del tren, y leerlo justo
        // cuando la orden lo está cambiando (luces, pantógrafo…) puede hacer fallar al simulador.
        static long _quietUntil;
        public static bool Quiet => Environment.TickCount64 < System.Threading.Interlocked.Read(ref _quietUntil);
        public static void Calm(int ms)
        {
            long t = Environment.TickCount64 + ms;
            if (t > System.Threading.Interlocked.Read(ref _quietUntil)) System.Threading.Interlocked.Exchange(ref _quietUntil, t);
        }
        static readonly HashSet<string> _held = new HashSet<string>(StringComparer.Ordinal);

        // Posición de la orden en ESTA versión de OR (typeof resuelve al tipo de la DLL cargada).
        static int Index(string cmd)
        {
            try { return Convert.ToInt32(Enum.Parse(typeof(UserCommand), cmd)); }
            catch { return -1; }   // esta versión de OR no tiene esa orden
        }

        static UserCommandKeyInput Key(string cmd)
        {
            try
            {
                int i = Index(cmd); if (i < 0) return null;
                _input ??= new InputSettings(Array.Empty<string>());   // lee el teclado configurado en OR
                return i < _input.Commands.Length ? _input.Commands[i] as UserCommandKeyInput : null;
            }
            catch { return null; }
        }

        // Tecla configurada para una orden, como la escribe OR («P», «Shift + Q»…).
        public static string KeyName(string c)
        {
            try { return Key(c)?.ToString() ?? ""; } catch { return ""; }
        }

        // Vuelve a leer el teclado de OR (por si lo ha cambiado entre conducciones).
        public static void Reload() { _input = null; }

        public static bool Down(string c)
        {
            var k = Key(c); if (k == null) return false;
            Calm(600);
            FocusSimulator();
            var ins = new List<INPUT>();
            if (k.Control) ins.Add(Scan(0x1D, false));
            if (k.Shift) ins.Add(Scan(0x2A, false));
            if (k.Alt) ins.Add(Scan(0x38, false));
            ins.Add(Scan(k.ScanCode, false));
            Send(ins);
            lock (_held) _held.Add(c);
            return true;
        }

        public static void Up(string c)
        {
            var k = Key(c); if (k == null) return;
            Calm(500);
            var ins = new List<INPUT> { Scan(k.ScanCode, true) };
            if (k.Alt) ins.Add(Scan(0x38, true));
            if (k.Shift) ins.Add(Scan(0x2A, true));
            if (k.Control) ins.Add(Scan(0x1D, true));
            Send(ins);
            lock (_held) _held.Remove(c);
        }

        // Pulsación corta (el simulador lee el teclado una vez por fotograma: 120 ms bastan).
        public static async Task Press(string c, int ms = 120)
        {
            if (!Down(c)) return;
            await Task.Delay(ms);
            Up(c);
        }

        // Suelta todo lo que quedara pulsado (al cerrar el pupitre, por si acaso).
        public static void ReleaseAll()
        {
            string[] l; lock (_held) l = new List<string>(_held).ToArray();
            foreach (var c in l) Up(c);
        }

        // ¿Está pulsada ahora la tecla de esta orden (con sus modificadores) y el simulador delante?
        // Sirve para que la bocina y la arena se enciendan en el pupitre también al usar el teclado,
        // en cabinas que no tienen indicador de esas órdenes (OR no las publica de otra forma).
        public static bool IsDown(string cmd)
        {
            try
            {
                var k = Key(cmd); if (k == null || !SimulatorFocused()) return false;
                uint vk = MapVirtualKey((uint)(k.ScanCode > 0xFF ? (0xE000 | (k.ScanCode & 0xFF)) : k.ScanCode), 3 /*MAPVK_VSC_TO_VK_EX*/);
                if (vk == 0 || (GetAsyncKeyState((int)vk) & 0x8000) == 0) return false;
                bool sh = (GetAsyncKeyState(0x10) & 0x8000) != 0, ct = (GetAsyncKeyState(0x11) & 0x8000) != 0, al = (GetAsyncKeyState(0x12) & 0x8000) != 0;
                return sh == k.Shift && ct == k.Control && al == k.Alt;
            }
            catch { return false; }
        }

        // Procesos del simulador, releídos como mucho cada 2 s (se consulta muchas veces por segundo).
        static int[] _orPids = Array.Empty<int>();
        static long _orPidsAt = -10000;

        static bool SimulatorFocused()
        {
            try
            {
                long now = Environment.TickCount64;
                if (now - _orPidsAt > 2000)
                {
                    _orPids = Array.ConvertAll(SimulatorProcesses(), p => { int id = p.Id; p.Dispose(); return id; });
                    _orPidsAt = now;
                }
                GetWindowThreadProcessId(GetForegroundWindow(), out uint pid);
                return Array.IndexOf(_orPids, (int)pid) >= 0;
            }
            catch { return false; }
        }

        // La pulsación va a la ventana activa: si no es el simulador, se le devuelve el foco.
        public static void FocusSimulator()
        {
            try
            {
                IntPtr fg = GetForegroundWindow();
                GetWindowThreadProcessId(fg, out uint pid);
                foreach (var p in SimulatorProcesses())
                    using (p)
                    {
                        if (p.Id == pid) return;   // ya es la activa
                        if (p.MainWindowHandle != IntPtr.Zero) { SetForegroundWindow(p.MainWindowHandle); return; }
                    }
            }
            catch { }
        }

        static Process[] SimulatorProcesses()
        {
            var a = Process.GetProcessesByName("RunActivity");
            var b = Process.GetProcessesByName("RunActivityLAA");
            if (b.Length == 0) return a;
            var all = new Process[a.Length + b.Length]; a.CopyTo(all, 0); b.CopyTo(all, a.Length);
            return all;
        }

        // ---- Lo que hace el usuario en el simulador ----
        // Al pulsar una tecla de función (F2 guardar, F9 operaciones del tren con sus enganches…) o al hacer
        // clic en la ventana de OR, el simulador cambia su estado: durante un momento no se le pregunta nada.
        // Se llama cada ~40 ms mientras se conduce (MainMenuForm.StartKmTracking).
        static readonly bool[] _wasDown = new bool[256];
        public static event Action<int> SimulatorKey;   // tecla de función pulsada en el simulador (VK)

        public static void WatchUserInput()
        {
            try
            {
                bool focused = SimulatorFocused();
                for (int vk = 0x70; vk <= 0x7B; vk++) Edge(vk, focused, vk == 0x71 ? 3000 : 1200);   // F1–F12 (F2: guardar tarda más)
                Edge(0x01, focused, 250);   // clic izquierdo (botones de la ventana F9, palancas…)
                Edge(0x02, focused, 250);   // clic derecho
            }
            catch { }
        }

        static void Edge(int vk, bool focused, int calmMs)
        {
            bool down = (GetAsyncKeyState(vk) & 0x8000) != 0;
            if (down && !_wasDown[vk] && focused)
            {
                Calm(calmMs);
                if (vk >= 0x70) try { SimulatorKey?.Invoke(vk); } catch { }
            }
            _wasDown[vk] = down;
        }

        // Reenvía al simulador una tecla pulsada en una ventana de SelectOR que tenía el teclado (el mapa
        // grande, el chat…): sin esto, F2 o F9 se quedaban en SelectOR y OR no guardaba ni abría nada.
        // Si el teclado lo tiene una ventana de SelectOR, se devuelve al simulador (sin forzar nada:
        // SetForegroundWindow está permitido porque la ventana activa es nuestra).
        public static void ReturnFocusIfOurs()
        {
            try
            {
                GetWindowThreadProcessId(GetForegroundWindow(), out uint pid);
                if (pid == (uint)Environment.ProcessId) FocusSimulator();
            }
            catch { }
        }

        public static void ForwardKey(int vk, bool shift, bool ctrl, bool alt)
        {
            try
            {
                int sc = (int)MapVirtualKey((uint)vk, 4 /*MAPVK_VK_TO_VSC_EX: 0xE0xx en las extendidas (flechas…)*/);
                if ((sc & 0xFF00) == 0xE000) sc = 0x100 | (sc & 0xFF);   // Scan() las marca como extendidas
                if (sc == 0) return;
                FocusSimulator();
                Calm(vk == 0x71 ? 3000 : 1200);
                var ins = new List<INPUT>();
                if (ctrl) ins.Add(Scan(0x1D, false));
                if (shift) ins.Add(Scan(0x2A, false));
                if (alt) ins.Add(Scan(0x38, false));
                ins.Add(Scan(sc, false));
                Send(ins);
                Task.Delay(80).ContinueWith(_ =>
                {
                    var up = new List<INPUT> { Scan(sc, true) };
                    if (alt) up.Add(Scan(0x38, true));
                    if (shift) up.Add(Scan(0x2A, true));
                    if (ctrl) up.Add(Scan(0x1D, true));
                    Send(up);
                });
            }
            catch { }
        }

        static INPUT Scan(int code, bool up)
        {
            uint flags = KEYEVENTF_SCANCODE | (up ? KEYEVENTF_KEYUP : 0);
            if (code > 0xFF) { flags |= KEYEVENTF_EXTENDEDKEY; code &= 0xFF; }   // teclas extendidas (0xE0 xx)
            return new INPUT { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wScan = (ushort)code, dwFlags = flags } } };
        }

        static void Send(List<INPUT> ins)
        {
            if (ins.Count == 0) return;
            SendInput((uint)ins.Count, ins.ToArray(), Marshal.SizeOf<INPUT>());
        }

        // ---------------- Win32 ----------------
        const uint INPUT_KEYBOARD = 1, KEYEVENTF_EXTENDEDKEY = 0x0001, KEYEVENTF_KEYUP = 0x0002, KEYEVENTF_SCANCODE = 0x0008;

        [StructLayout(LayoutKind.Sequential)]
        struct INPUT { public uint type; public InputUnion u; }

        [StructLayout(LayoutKind.Explicit)]
        struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

        [StructLayout(LayoutKind.Sequential)]
        struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

        [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, INPUT[] inputs, int size);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
        [DllImport("user32.dll")] static extern uint MapVirtualKey(uint uCode, uint uMapType);
    }
}
