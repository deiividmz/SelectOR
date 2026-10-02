// Diagnóstico de rendimiento: con SELECTOR_PERFLOG=1 apunta en %TEMP%\selector_perf.log lo que tarda más
// de 8 ms en el hilo de la interfaz (using (Perf.T("qué")) { … }). Sin la variable no hace nada.
using System;
using System.Diagnostics;
using System.IO;

namespace SelectOR
{
    static class Perf
    {
        static readonly bool On = Environment.GetEnvironmentVariable("SELECTOR_PERFLOG") == "1";
        static readonly string LogFile = Path.Combine(Path.GetTempPath(), "selector_perf.log");
        static readonly IDisposable Nada = new Noop();

        public static IDisposable T(string what) => On ? new Timer(what) : Nada;

        sealed class Noop : IDisposable { public void Dispose() { } }
        sealed class Timer : IDisposable
        {
            readonly string _w; readonly long _t0 = Stopwatch.GetTimestamp();
            public Timer(string w) { _w = w; }
            public void Dispose()
            {
                double ms = (Stopwatch.GetTimestamp() - _t0) * 1000.0 / Stopwatch.Frequency;
                if (ms < 8) return;
                try { lock (LogFile) File.AppendAllText(LogFile, $"{DateTime.Now:HH:mm:ss.fff}  {ms,7:F1} ms  {_w}{Environment.NewLine}"); } catch { }
            }
        }
    }
}
