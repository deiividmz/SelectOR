// (1.2.90) RADIO DE LA SALA: el sonido. Captura y reproducción con el sonido de Windows (winmm: waveIn/waveOut, sin librerías),
// la voz en IMA ADPCM (4 bits por muestra: 16 kHz → 64 kbit/s, bloques de 40 ms) y, al oírla, el efecto de radio (filtro de
// 300 a 3.000 Hz, un poco de saturación y de ruido) con su «chasquido» al empezar y al acabar cada transmisión.
// Todo en un hilo propio: la interfaz no se para nunca por el sonido.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace SelectOR
{
    public sealed class RadioAudio : IDisposable
    {
        public const int Rate = 16000, BlockSamples = 640;   // 40 ms
        public static int BlockBytes => 4 + BlockSamples / 2; // cabecera ADPCM + 4 bits por muestra

        // ---------------------------------------------------------------- winmm
        [StructLayout(LayoutKind.Sequential)] struct WaveFormat { public short wFormatTag, nChannels; public int nSamplesPerSec, nAvgBytesPerSec; public short nBlockAlign, wBitsPerSample, cbSize; }
        [StructLayout(LayoutKind.Sequential)] struct WaveHdr { public IntPtr lpData; public int dwBufferLength, dwBytesRecorded; public IntPtr dwUser; public int dwFlags, dwLoops; public IntPtr lpNext, reserved; }
        const int WHDR_DONE = 1, WAVE_MAPPER = -1, CALLBACK_NULL = 0;
        [DllImport("winmm.dll")] static extern int waveInOpen(out IntPtr h, int dev, ref WaveFormat f, IntPtr cb, IntPtr inst, int flags);
        [DllImport("winmm.dll")] static extern int waveInPrepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveInUnprepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveInAddBuffer(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveInStart(IntPtr h);
        [DllImport("winmm.dll")] static extern int waveInReset(IntPtr h);
        [DllImport("winmm.dll")] static extern int waveInClose(IntPtr h);
        [DllImport("winmm.dll")] static extern int waveOutOpen(out IntPtr h, int dev, ref WaveFormat f, IntPtr cb, IntPtr inst, int flags);
        [DllImport("winmm.dll")] static extern int waveOutPrepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutUnprepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutWrite(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutReset(IntPtr h);
        [DllImport("winmm.dll")] static extern int waveOutClose(IntPtr h);
        static readonly int HdrSize = Marshal.SizeOf<WaveHdr>();

        static WaveFormat Fmt() => new WaveFormat { wFormatTag = 1, nChannels = 1, nSamplesPerSec = Rate, wBitsPerSample = 16, nBlockAlign = 2, nAvgBytesPerSec = Rate * 2 };

        // un búfer de winmm: la cabecera y los datos, en memoria que no se mueve
        sealed class Buf
        {
            public IntPtr Hdr, Data; public int Bytes; public bool Queued;
            public Buf(int bytes) { Bytes = bytes; Data = Marshal.AllocHGlobal(bytes); Hdr = Marshal.AllocHGlobal(HdrSize); Reset(); }
            public void Reset() { var h = new WaveHdr { lpData = Data, dwBufferLength = Bytes }; Marshal.StructureToPtr(h, Hdr, false); }
            public WaveHdr H => Marshal.PtrToStructure<WaveHdr>(Hdr);
            public bool Done => (H.dwFlags & WHDR_DONE) != 0;
            public void Free() { Marshal.FreeHGlobal(Data); Marshal.FreeHGlobal(Hdr); }
        }

        // ---------------------------------------------------------------- estado
        public event Action<byte[]> Captured;   // (hilo del sonido) un bloque de voz ya comprimido, mientras se habla
        public float Volume = 1f;               // lo que se oye (0–2)
        public bool Available { get; private set; } = true;
        public string LastError;
        readonly Thread _thread; volatile bool _run = true;
        volatile bool _talk;                    // pulsado: capturando
        IntPtr _in = IntPtr.Zero, _out = IntPtr.Zero;
        readonly List<Buf> _inBufs = new(), _outBufs = new();
        // (2026-11-10, canales) lo que se oye, por quién: cada voz en su cola y se mezclan (un jefe oye a la vez su banda y a los
        // demás jefes); los chasquidos y pitidos, en la suya («~»)
        readonly Dictionary<string, Queue<short[]>> _play = new();
        sealed class Spk
        {
            public readonly AdpcmState St = new();
            public readonly Biquad Hp = Biquad.HighPass(400, Rate, 0.707), Hp2 = Biquad.HighPass(400, Rate, 0.707), Lp = Biquad.LowPass(2800, Rate, 0.707), Lp2 = Biquad.LowPass(2800, Rate, 0.707);
        }
        void Play(string who, short[] pcm) { lock (_play) { if (!_play.TryGetValue(who, out var q)) _play[who] = q = new Queue<short[]>(); q.Enqueue(pcm); } }
        readonly ConcurrentQueue<Action> _todo = new();
        readonly AdpcmState _enc = new();
        readonly Dictionary<string, Spk> _dec = new();   // (cada voz, su descompresor y su filtro de radio: 400–2.800 Hz)
        readonly Random _rnd = new();
        float _agc = 1f;

        public RadioAudio()
        {
            _thread = new Thread(Loop) { IsBackground = true, Name = "Radio de la sala" };
            _thread.Start();
        }

        public bool Talking => _talk;
        public void Talk(bool on) { if (_talk == on) return; _talk = on; _todo.Enqueue(() => { if (on) StartIn(); else StopIn(); }); }

        // un bloque de voz de otro (del hilo de la red): se descomprime, se le pone el efecto de radio y a la cola
        public void Receive(string from, byte[] block)
        {
            if (block == null || block.Length < 4) return;
            Spk sp;
            lock (_dec) { if (!_dec.TryGetValue(from ?? "", out sp)) _dec[from ?? ""] = sp = new Spk(); }
            var pcm = Adpcm.Decode(block, sp.St);
            RadioizeCore(pcm, sp.Hp, sp.Hp2, sp.Lp, sp.Lp2, _rnd, Volume);
            Play(from ?? "", pcm);
        }

        // el chasquido: al empezar una transmisión, un golpe seco; al acabar, un «shhh» corto (como un walkie al soltar)
        public void Squelch(bool start, string who = null)
        {
            lock (_dec) { if (who == null) _dec.Clear(); else _dec.Remove(who); }   // (quien empieza o acaba, de cero; los demás siguen)
            Play("~", SquelchCore(start, _rnd, Volume));
        }

        static short[] SquelchCore(bool start, Random rnd, float vol)
        {
            int n = start ? Rate * 6 / 100 : Rate * 18 / 100;
            var s = new short[n];
            for (int i = 0; i < n; i++)
            {
                double env = start ? (i < Rate / 200 ? 1.0 : Math.Exp(-(i - Rate / 200) / (Rate * 0.012))) : Math.Exp(-i / (Rate * 0.06));
                double v = (rnd.NextDouble() * 2 - 1) * env * (start ? 9000 : 5000);
                if (start && i < Rate / 400) v += 14000 * Math.Sin(i * 2 * Math.PI * 1100 / Rate);
                s[i] = Clip(v * vol);
            }
            return s;
        }

        // «canal ocupado»: dos pitidos cortos, solo para quien lo intenta
        public void Busy()
        {
            int n = Rate * 32 / 100; var s = new short[n];
            for (int i = 0; i < n; i++) { bool on = i < Rate / 10 || i > Rate * 2 / 10 && i < Rate * 3 / 10; s[i] = on ? Clip(6000 * Math.Sin(i * 2 * Math.PI * 520 / Rate) * Volume) : (short)0; }
            Play("~", s);
        }

        // (petición: «que se escuche como a través de una radio») banda estrecha de radio (400–2.800 Hz, con más pendiente),
        // con el detalle de un equipo portátil (8 kHz), saturada y con su soplido de fondo

        static void RadioizeCore(short[] pcm, Biquad hp, Biquad hp2, Biquad lp, Biquad lp2, Random rnd, float vol)
        {
            double held = 0;
            for (int i = 0; i < pcm.Length; i++)
            {
                double x = lp2.Run(lp.Run(hp2.Run(hp.Run(pcm[i] / 32768.0))));   // (el filtro, en todas las muestras)
                if ((i & 1) == 0)                                                 // (y se queda con una de cada dos: 8 kHz)
                {
                    x = Math.Tanh(x * 3.2) / Math.Tanh(3.2);                      // saturada, como el altavoz de una radio
                    x += (rnd.NextDouble() * 2 - 1) * 0.022;                      // y su soplido de fondo
                    held = Math.Round(x * 96) / 96;                               // (con menos detalle)
                }
                pcm[i] = Clip(held * 32767 * 1.1 * vol);
            }
        }

        // cómo sonaría una grabación (16 kHz) por la radio, con su chasquido al empezar y al soltar (para probarla)
        public static short[] Preview(short[] pcm)
        {
            var rnd = new Random(7); var c = (short[])pcm.Clone();
            RadioizeCore(c, Biquad.HighPass(400, Rate, 0.707), Biquad.HighPass(400, Rate, 0.707), Biquad.LowPass(2800, Rate, 0.707), Biquad.LowPass(2800, Rate, 0.707), rnd, 1f);
            var a = SquelchCore(true, rnd, 1f); var z = SquelchCore(false, rnd, 1f);
            var o = new short[a.Length + c.Length + z.Length];
            a.CopyTo(o, 0); c.CopyTo(o, a.Length); z.CopyTo(o, a.Length + c.Length);
            return o;
        }

        static short Clip(double v) => (short)Math.Max(-32768, Math.Min(32767, v));

        // (2026-11-09) Dispositivos de Windows por su nombre (en Ajustes se guarda el nombre: los números cambian al enchufar y
        // desenchufar). null o uno que ya no está: el predeterminado de Windows.
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct WaveInCaps { public short wMid, wPid; public int vDriverVersion; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szPname; public int dwFormats; public short wChannels, wReserved1; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct WaveOutCaps { public short wMid, wPid; public int vDriverVersion; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szPname; public int dwFormats; public short wChannels, wReserved1; public int dwSupport; }
        [DllImport("winmm.dll")] static extern int waveInGetNumDevs();
        [DllImport("winmm.dll")] static extern int waveOutGetNumDevs();
        [DllImport("winmm.dll", CharSet = CharSet.Unicode, EntryPoint = "waveInGetDevCapsW")] static extern int waveInGetDevCaps(IntPtr id, ref WaveInCaps c, int size);
        [DllImport("winmm.dll", CharSet = CharSet.Unicode, EntryPoint = "waveOutGetDevCapsW")] static extern int waveOutGetDevCaps(IntPtr id, ref WaveOutCaps c, int size);
        public static string InDevice, OutDevice;   // (RoomRadio, de AppPrefs) los elegidos
        public static List<string> InputNames()
        {
            var l = new List<string>();
            try { int n = waveInGetNumDevs(); for (int i = 0; i < n; i++) { var c = new WaveInCaps(); if (waveInGetDevCaps((IntPtr)i, ref c, Marshal.SizeOf<WaveInCaps>()) == 0 && !string.IsNullOrEmpty(c.szPname)) l.Add(c.szPname); } } catch { }
            return l;
        }
        public static List<string> OutputNames()
        {
            var l = new List<string>();
            try { int n = waveOutGetNumDevs(); for (int i = 0; i < n; i++) { var c = new WaveOutCaps(); if (waveOutGetDevCaps((IntPtr)i, ref c, Marshal.SizeOf<WaveOutCaps>()) == 0 && !string.IsNullOrEmpty(c.szPname)) l.Add(c.szPname); } } catch { }
            return l;
        }
        static int DevIndex(List<string> names, string want)
        {
            if (string.IsNullOrEmpty(want)) return WAVE_MAPPER;
            int i = names.FindIndex(x => string.Equals(x, want, StringComparison.OrdinalIgnoreCase));
            return i >= 0 ? i : WAVE_MAPPER;
        }

        // ---------------------------------------------------------------- el hilo
        void Loop()
        {
            try
            {
                var f = Fmt();
                int r = waveOutOpen(out _out, DevIndex(OutputNames(), OutDevice), ref f, IntPtr.Zero, IntPtr.Zero, CALLBACK_NULL);
                if (r != 0 && !string.IsNullOrEmpty(OutDevice)) r = waveOutOpen(out _out, WAVE_MAPPER, ref f, IntPtr.Zero, IntPtr.Zero, CALLBACK_NULL);   // (la elegida falla: la predeterminada)
                if (r != 0) { _out = IntPtr.Zero; LastError = "waveOutOpen " + r; }
                for (int i = 0; i < 12; i++) _outBufs.Add(new Buf(Rate * 2 * 4 / 10));   // (400 ms: cabe el chasquido entero)
                while (_run)
                {
                    while (_todo.TryDequeue(out var a)) { try { a(); } catch (Exception ex) { LastError = ex.Message; } }
                    PumpIn();
                    PumpOut();
                    Thread.Sleep(8);
                }
            }
            catch (Exception ex) { LastError = ex.Message; Available = false; }
            finally
            {
                try { StopIn(); } catch { }
                if (_out != IntPtr.Zero)
                {
                    waveOutReset(_out);
                    foreach (var b in _outBufs) { if (b.Queued) waveOutUnprepareHeader(_out, b.Hdr, HdrSize); b.Free(); }
                    waveOutClose(_out);
                }
            }
        }

        void StartIn()
        {
            if (_in != IntPtr.Zero) return;
            var f = Fmt();
            int r = waveInOpen(out _in, DevIndex(InputNames(), InDevice), ref f, IntPtr.Zero, IntPtr.Zero, CALLBACK_NULL);
            if (r != 0 && !string.IsNullOrEmpty(InDevice)) r = waveInOpen(out _in, WAVE_MAPPER, ref f, IntPtr.Zero, IntPtr.Zero, CALLBACK_NULL);
            if (r != 0) { _in = IntPtr.Zero; LastError = "waveInOpen " + r; MicError?.Invoke(); return; }
            _enc.Reset(); _agc = 1f;
            for (int i = 0; i < 6; i++)
            {
                var b = new Buf(BlockSamples * 2); _inBufs.Add(b);
                waveInPrepareHeader(_in, b.Hdr, HdrSize); waveInAddBuffer(_in, b.Hdr, HdrSize); b.Queued = true;
            }
            waveInStart(_in);
        }
        public event Action MicError;   // (sin micrófono)

        void StopIn()
        {
            if (_in == IntPtr.Zero) return;
            PumpIn();   // (lo último que se dijo)
            waveInReset(_in);
            foreach (var b in _inBufs) { waveInUnprepareHeader(_in, b.Hdr, HdrSize); b.Free(); }
            _inBufs.Clear();
            waveInClose(_in); _in = IntPtr.Zero;
        }

        void PumpIn()
        {
            if (_in == IntPtr.Zero) return;
            foreach (var b in _inBufs)
            {
                if (!b.Queued || !b.Done) continue;
                var h = b.H; int n = h.dwBytesRecorded / 2;
                if (n > 0)
                {
                    var pcm = new short[BlockSamples];
                    Marshal.Copy(b.Data, pcm, 0, Math.Min(n, BlockSamples));
                    // un poco de ganancia automática: quien habla bajito se oye igual que quien grita
                    int peak = 1; foreach (var v in pcm) peak = Math.Max(peak, Math.Abs((int)v));
                    float want = Math.Min(6f, 20000f / peak);
                    _agc = want < _agc ? want : _agc + (want - _agc) * 0.05f;
                    for (int i = 0; i < pcm.Length; i++) pcm[i] = Clip(pcm[i] * _agc);
                    try { Captured?.Invoke(Adpcm.Encode(pcm, _enc)); } catch { }
                }
                waveInUnprepareHeader(_in, b.Hdr, HdrSize); b.Reset();
                if (_talk) { waveInPrepareHeader(_in, b.Hdr, HdrSize); waveInAddBuffer(_in, b.Hdr, HdrSize); } else b.Queued = false;
            }
        }

        void PumpOut()
        {
            if (_out == IntPtr.Zero) { lock (_play) _play.Clear(); return; }
            foreach (var b in _outBufs)
                if (b.Queued && b.Done) { waveOutUnprepareHeader(_out, b.Hdr, HdrSize); b.Queued = false; b.Reset(); }
            while (true)
            {
                Buf free = null; foreach (var b in _outBufs) if (!b.Queued) { free = b; break; }
                if (free == null) return;
                // un trozo de cada voz que tenga algo, sumados
                short[] pcm = null;
                lock (_play)
                {
                    var parts = new List<short[]>();
                    foreach (var q in _play.Values) if (q.Count > 0) parts.Add(q.Dequeue());
                    foreach (var k in _play.Where(kv => kv.Value.Count == 0).Select(kv => kv.Key).ToList()) _play.Remove(k);
                    if (parts.Count == 1) pcm = parts[0];
                    else if (parts.Count > 1)
                    {
                        pcm = new short[parts.Max(x => x.Length)];
                        for (int i = 0; i < pcm.Length; i++) { int sum = 0; foreach (var x in parts) if (i < x.Length) sum += x[i]; pcm[i] = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, sum)); }
                    }
                }
                if (pcm == null) return;
                int bytes = Math.Min(pcm.Length * 2, free.Bytes);
                Marshal.Copy(pcm, 0, free.Data, bytes / 2);
                var h = new WaveHdr { lpData = free.Data, dwBufferLength = bytes };
                Marshal.StructureToPtr(h, free.Hdr, false);
                waveOutPrepareHeader(_out, free.Hdr, HdrSize); waveOutWrite(_out, free.Hdr, HdrSize); free.Queued = true;
            }
        }

        public void Dispose() { _run = false; try { _thread.Join(1500); } catch { } }

        // ---------------------------------------------------------------- IMA ADPCM
        public sealed class AdpcmState { public int Pred, Index; public void Reset() { Pred = 0; Index = 0; } }
        public static class Adpcm
        {
            static readonly int[] IndexTable = { -1, -1, -1, -1, 2, 4, 6, 8, -1, -1, -1, -1, 2, 4, 6, 8 };
            static readonly int[] StepTable = {
                7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45, 50, 55, 60, 66, 73, 80, 88, 97, 107, 118, 130, 143,
                157, 173, 190, 209, 230, 253, 279, 307, 337, 371, 408, 449, 494, 544, 598, 658, 724, 796, 876, 963, 1060, 1166, 1282, 1411,
                1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428, 4871, 5358, 5894, 6484, 7132, 7845, 8630, 9493,
                10442, 11487, 12635, 13899, 15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767 };

            // [pred lo, pred hi, índice, 0] + 4 bits por muestra
            public static byte[] Encode(short[] pcm, AdpcmState st)
            {
                var o = new byte[4 + (pcm.Length + 1) / 2];
                o[0] = (byte)(st.Pred & 0xFF); o[1] = (byte)((st.Pred >> 8) & 0xFF); o[2] = (byte)st.Index;
                int pred = st.Pred, idx = st.Index;
                for (int i = 0; i < pcm.Length; i++)
                {
                    int step = StepTable[idx], diff = pcm[i] - pred, code = 0;
                    if (diff < 0) { code = 8; diff = -diff; }
                    int vd = step >> 3;
                    if (diff >= step) { code |= 4; diff -= step; vd += step; }
                    step >>= 1; if (diff >= step) { code |= 2; diff -= step; vd += step; }
                    step >>= 1; if (diff >= step) { code |= 1; vd += step; }
                    pred += (code & 8) != 0 ? -vd : vd;
                    pred = Math.Max(-32768, Math.Min(32767, pred));
                    idx = Math.Max(0, Math.Min(88, idx + IndexTable[code]));
                    if ((i & 1) == 0) o[4 + i / 2] = (byte)code; else o[4 + i / 2] |= (byte)(code << 4);
                }
                st.Pred = pred; st.Index = idx;
                return o;
            }

            public static short[] Decode(byte[] b, AdpcmState st)
            {
                int n = (b.Length - 4) * 2; var o = new short[n];
                int pred = (short)(b[0] | (b[1] << 8)), idx = Math.Max(0, Math.Min(88, (int)b[2]));
                for (int i = 0; i < n; i++)
                {
                    int code = (i & 1) == 0 ? b[4 + i / 2] & 0x0F : b[4 + i / 2] >> 4;
                    int step = StepTable[idx], vd = step >> 3;
                    if ((code & 4) != 0) vd += step; if ((code & 2) != 0) vd += step >> 1; if ((code & 1) != 0) vd += step >> 2;
                    pred += (code & 8) != 0 ? -vd : vd;
                    pred = Math.Max(-32768, Math.Min(32767, pred));
                    idx = Math.Max(0, Math.Min(88, idx + IndexTable[code]));
                    o[i] = (short)pred;
                }
                st.Pred = pred; st.Index = idx;
                return o;
            }
        }

        // ---------------------------------------------------------------- filtro (RBJ)
        sealed class Biquad
        {
            double b0, b1, b2, a1, a2, x1, x2, y1, y2;
            public double Run(double x) { double y = b0 * x + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2; x2 = x1; x1 = x; y2 = y1; y1 = y; return y; }
            static Biquad Make(double b0, double b1, double b2, double a0, double a1, double a2) => new Biquad { b0 = b0 / a0, b1 = b1 / a0, b2 = b2 / a0, a1 = a1 / a0, a2 = a2 / a0 };
            public static Biquad LowPass(double f, double rate, double q)
            {
                double w = 2 * Math.PI * f / rate, c = Math.Cos(w), al = Math.Sin(w) / (2 * q);
                return Make((1 - c) / 2, 1 - c, (1 - c) / 2, 1 + al, -2 * c, 1 - al);
            }
            public static Biquad HighPass(double f, double rate, double q)
            {
                double w = 2 * Math.PI * f / rate, c = Math.Cos(w), al = Math.Sin(w) / (2 * q);
                return Make((1 + c) / 2, -(1 + c), (1 + c) / 2, 1 + al, -2 * c, 1 - al);
            }
        }
    }
}
