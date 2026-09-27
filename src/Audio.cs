// Reproductor de audio mínimo sobre winmm (MCI).
//  · Reproduce WAV y MP3 sin librerías externas: el proyecto no copia paquetes NuGet a la carpeta
//    de Open Rails (CopyLocalLockFileAssemblies=false), así que una dependencia tipo NAudio no
//    llegaría al .exe desplegado. MCI viene con Windows.
//  · Reproduce en COLA: los avisos de megafonía se componen de varios clips seguidos
//    (campanada + «próxima parada» + nombre de la estación) y no deben solaparse.
//  · El final del clip se detecta sondeando «status mode» con un Timer de WinForms (no hace falta
//    ventana ni notificaciones MCI).

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace SelectOR
{
    public static class Audio
    {
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
        static extern int mciSendString(string command, StringBuilder ret, int retLen, IntPtr hwnd);

        static string _alias;                       // alias MCI del clip abierto (null = nada sonando)
        static int _seq;                            // contador para que cada alias sea único
        static Timer _poll;
        static readonly Queue<string> _queue = new Queue<string>();
        static Action _onDone;                      // aviso al terminar TODA la cola

        public static bool Playing => _alias != null;

        static string Send(string cmd)
        {
            var sb = new StringBuilder(256);
            return mciSendString(cmd, sb, sb.Capacity, IntPtr.Zero) == 0 ? sb.ToString() : null;
        }

        // Reproduce un solo archivo (corta lo que hubiera sonando).
        public static void Play(string file, Action onDone = null) => PlayAll(new[] { file }, onDone);

        // Reproduce varios archivos seguidos, en orden. Los que no existan se saltan.
        public static void PlayAll(IEnumerable<string> files, Action onDone = null)
        {
            Stop();
            _queue.Clear();
            if (files != null)
                foreach (var f in files)
                    if (!string.IsNullOrWhiteSpace(f) && System.IO.File.Exists(f)) _queue.Enqueue(f);
            _onDone = onDone;
            if (_queue.Count == 0) { var d = _onDone; _onDone = null; d?.Invoke(); return; }
            Next();
        }

        static void Next()
        {
            Close();
            if (_queue.Count == 0)
            {
                var d = _onDone; _onDone = null; _poll?.Stop();
                d?.Invoke();
                return;
            }
            string file = _queue.Dequeue();
            string alias = "selector_pa" + (++_seq);
            // Sin «type»: MCI deduce el dispositivo por la extensión (.wav → waveaudio, .mp3 → mpegvideo).
            if (Send($"open \"{file}\" alias {alias}") == null)
            {
                string type = file.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) ? "mpegvideo" : "waveaudio";
                if (Send($"open \"{file}\" type {type} alias {alias}") == null) { Next(); return; }   // ilegible: al siguiente
            }
            _alias = alias;
            Send($"setaudio {alias} volume to {_volume}");   // no todos los dispositivos lo admiten
            if (Send($"play {alias}") == null) { Next(); return; }
            if (_poll == null)
            {
                _poll = new Timer { Interval = 200 };
                _poll.Tick += (s, e) => { if (_alias != null && Send($"status {_alias} mode") != "playing") Next(); };
            }
            _poll.Start();
        }

        static int _volume = 1000;   // 0–1000

        // Volumen de los avisos (0–100). Algunos dispositivos MCI lo ignoran; entonces manda el mezclador.
        public static void SetVolume(int percent)
        {
            _volume = Math.Max(0, Math.Min(100, percent)) * 10;
            if (_alias != null) Send($"setaudio {_alias} volume to {_volume}");
        }

        static void Close()
        {
            if (_alias == null) return;
            Send($"stop {_alias}");
            Send($"close {_alias}");
            _alias = null;
        }

        // Corta lo que suene y vacía la cola.
        public static void Stop()
        {
            _queue.Clear();
            _onDone = null;
            _poll?.Stop();
            Close();
        }
    }
}
