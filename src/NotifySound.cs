// Sonido de aviso propio (no el de Windows): dos notas suaves, tipo campanita, de medio segundo.
//  · Se sintetiza en memoria al primer uso (WAV 16 bits mono): no hace falta ningún archivo.
//  · Suena con PlaySound de winmm, aparte del reproductor MCI de la megafonía (Audio), así que no
//    corta un aviso de estación que esté sonando.
//  · Varios avisos seguidos (un lote de notificaciones, varios mensajes) suenan una sola vez.

using System;
using System.IO;
using System.Runtime.InteropServices;

namespace SelectOR
{
    public static class NotifySound
    {
        [DllImport("winmm.dll", EntryPoint = "PlaySoundW")]
        static extern bool PlaySound(IntPtr sound, IntPtr hmod, uint flags);
        const uint SND_ASYNC = 0x0001, SND_NODEFAULT = 0x0002, SND_MEMORY = 0x0004;

        static byte[] _wav;
        static GCHandle _pin;                    // el búfer no se puede mover mientras suena (SND_ASYNC)
        static DateTime _lastUtc = DateTime.MinValue;

        public static Func<bool> Enabled;        // preferencia del usuario (null = activado)

        public static void Play(bool force = false)
        {
            try
            {
                if (!force && Enabled != null && !Enabled()) return;
                var now = DateTime.UtcNow;
                if (!force && (now - _lastUtc).TotalSeconds < 1.5) return;
                _lastUtc = now;
                if (_wav == null) { _wav = Build(); _pin = GCHandle.Alloc(_wav, GCHandleType.Pinned); }
                PlaySound(_pin.AddrOfPinnedObject(), IntPtr.Zero, SND_ASYNC | SND_NODEFAULT | SND_MEMORY);
            }
            catch { }
        }

        // Dos notas (La5 → Mi6) con un armónico suave, ataque corto y caída exponencial.
        static byte[] Build()
        {
            const int rate = 44100;
            const double total = 0.55, peak = 0.22;            // volumen moderado
            int n = (int)(rate * total);
            var buf = new double[n];
            void Note(double start, double freq, double tau, double gain)
            {
                int i0 = (int)(start * rate);
                for (int i = i0; i < n; i++)
                {
                    double t = (i - i0) / (double)rate;
                    double env = Math.Min(1, t / 0.006) * Math.Exp(-t / tau);
                    double w = Math.Sin(2 * Math.PI * freq * t) + 0.25 * Math.Sin(2 * Math.PI * freq * 2 * t) + 0.06 * Math.Sin(2 * Math.PI * freq * 3 * t);
                    buf[i] += gain * env * w;
                }
            }
            Note(0.00, 880.0, 0.11, 0.8);
            Note(0.10, 1318.5, 0.16, 1.0);
            double max = 0;
            foreach (var v in buf) max = Math.Max(max, Math.Abs(v));
            int fade = (int)(0.06 * rate);
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            bw.Write("RIFF"u8.ToArray()); bw.Write(36 + n * 2); bw.Write("WAVE"u8.ToArray());
            bw.Write("fmt "u8.ToArray()); bw.Write(16); bw.Write((short)1); bw.Write((short)1);
            bw.Write(rate); bw.Write(rate * 2); bw.Write((short)2); bw.Write((short)16);
            bw.Write("data"u8.ToArray()); bw.Write(n * 2);
            for (int i = 0; i < n; i++)
            {
                double v = buf[i] / Math.Max(max, 1e-9) * peak;
                if (i > n - fade) v *= (n - i) / (double)fade;    // sin chasquido al final
                bw.Write((short)Math.Round(v * short.MaxValue));
            }
            bw.Flush();
            return ms.ToArray();
        }
    }
}
