// Consultas al servidor web de Open Rails (puerto 2150), compartidas por todo SelectOR.
// El servidor de OR atiende cada consulta en un hilo aparte y lee el estado del tren SIN bloqueos mientras la
// simulación lo cambia; cuantas más consultas, más opciones de pillarlo a medio cambiar (y de cerrar el
// simulador, sobre todo al enganchar o desenganchar con F9 o al guardar con F2). Por eso:
//  · la MISMA consulta pedida por varias partes (pupitre, viajeros, carné, piloto automático…) se hace UNA vez:
//    quien llega mientras está en marcha espera esa misma respuesta, y una respuesta reciente se reutiliza
//    durante un instante (Fresh);
//  · una sola consulta a la vez (Max); la posición (/API/MAP, solo lee dónde está el tren) no espera
//    turno, para que el HUD y el mini-mapa no se queden parados detrás del pupitre;
//  · mientras el usuario opera en el simulador (OrControl.Quiet) no se pregunta nada.
// Las llamadas lanzan excepción cuando no se hacen, igual que HttpClient, así que quien ya las protegía con
// try/catch sigue funcionando igual.

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace SelectOR
{
    static class OrApi
    {
        const int Max = 1;                     // consultas (distintas de la posición) a la vez, como mucho
        static readonly SemaphoreSlim Gate = new SemaphoreSlim(Max, Max);
        static readonly object _lock = new object();
        static readonly Dictionary<string, Task<string>> _inFlight = new(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, (long at, string body)> _recent = new(StringComparer.OrdinalIgnoreCase);

        // Cuánto vale una respuesta reciente para quien pregunta lo mismo (ms).
        static int Fresh(string path)
        {
            if (path.StartsWith("/API/TIME", StringComparison.OrdinalIgnoreCase)) return 400;
            if (path.StartsWith("/API/HUD/0", StringComparison.OrdinalIgnoreCase)) return 120;
            if (path.StartsWith("/API/CABCONTROLS", StringComparison.OrdinalIgnoreCase)) return 120;
            if (path.StartsWith("/API/TRACKMONITORDISPLAY", StringComparison.OrdinalIgnoreCase)) return 300;
            return 0;
        }

        static bool NoGate(string path) => path.StartsWith("/API/MAP", StringComparison.OrdinalIgnoreCase) && !path.StartsWith("/API/MAP/INIT", StringComparison.OrdinalIgnoreCase);

        // Diagnóstico (lo leen las pruebas).
        static int _now;
        public static int MaxInFlight, Requests, Shared, Skipped;

        public static Task<string> GetStringAsync(HttpClient http, string path, int waitMs = 1500)
        {
            if (http == null) throw new ObjectDisposedException(nameof(HttpClient));
            if (OrControl.Quiet) { Interlocked.Increment(ref Skipped); return Task.FromException<string>(new OperationCanceledException("OR ocupado")); }
            string key = http.BaseAddress + path;
            lock (_lock)
            {
                int fresh = Fresh(path);
                if (fresh > 0 && _recent.TryGetValue(key, out var r) && Environment.TickCount64 - r.at <= fresh)
                { Interlocked.Increment(ref Shared); return Task.FromResult(r.body); }
                if (_inFlight.TryGetValue(key, out var running)) { Interlocked.Increment(ref Shared); return running; }
                var t = Fetch(http, path, key, waitMs);
                if (!t.IsCompleted) _inFlight[key] = t;
                return t;
            }
        }

        static async Task<string> Fetch(HttpClient http, string path, string key, int waitMs)
        {
            bool gated = !NoGate(path);
            try
            {
                if (gated && !await Gate.WaitAsync(waitMs).ConfigureAwait(false)) { Interlocked.Increment(ref Skipped); throw new TimeoutException("Sin turno para consultar a OR"); }
                try
                {
                    if (OrControl.Quiet) { Interlocked.Increment(ref Skipped); throw new OperationCanceledException("OR ocupado"); }
                    int n = Interlocked.Increment(ref _now);
                    if (n > MaxInFlight) MaxInFlight = n;
                    Interlocked.Increment(ref Requests);
                    try
                    {
                        string body = await http.GetStringAsync(path).ConfigureAwait(false);
                        if (Fresh(path) > 0) lock (_lock) _recent[key] = (Environment.TickCount64, body);
                        return body;
                    }
                    finally { Interlocked.Decrement(ref _now); }
                }
                finally { if (gated) Gate.Release(); }
            }
            finally { lock (_lock) _inFlight.Remove(key); }
        }

        public static void ResetStats() { MaxInFlight = 0; Requests = 0; Shared = 0; Skipped = 0; }
    }
}
