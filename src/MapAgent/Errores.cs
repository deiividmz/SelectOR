// Por qué se cierra Open Rails (1.2.75). Con su registro desactivado (Opciones → «Logging», como en muchos equipos), un
// cierre por error no deja rastro: Open Rails lo escribe solo en su OpenRailsLog.txt y se cierra (sin ventana, si el
// error es de un hilo que no es el de dibujo). Aquí el agente escucha lo mismo que ese registro y lo apunta en el suyo
// (agente-mapa.log):
//  · los errores fatales (Game.ProcessReportError y el vigilante de hilos colgados escriben en Trace un FatalException);
//  · al terminar el proceso: si fue un cierre normal o tras un error, y las últimas excepciones de los segundos antes
//    (también las que Open Rails capturó), para saber qué estaba pasando;
//  · un error sin capturar en cualquier hilo.
// Solo escucha: no cambia nada de Open Rails.

using System;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;

namespace SelectOR.MapAgent
{
    public static class Errores
    {
        static int _installed, _fatals, _waits;
        static readonly Stopwatch _clock = Stopwatch.StartNew();

        // las últimas excepciones (también las que se capturan): solo la referencia, se escriben si hay un cierre
        const int Ring = 32;
        static readonly Exception[] _ex = new Exception[Ring];
        static readonly long[] _at = new long[Ring];
        static readonly string[] _thread = new string[Ring];
        static int _next;
        [ThreadStatic] static bool _inside;

        public static void Install()
        {
            if (Interlocked.Exchange(ref _installed, 1) == 1) return;
            try { Trace.Listeners.Add(new Listener()); } catch (Exception ex) { Agent.Log("errores: " + ex.GetType().Name + " " + ex.Message); }
            try { AppDomain.CurrentDomain.FirstChanceException += OnFirstChance; } catch { }
            try { AppDomain.CurrentDomain.UnhandledException += (s, e) => Fatal("error sin capturar", e.ExceptionObject as Exception, e.ExceptionObject?.ToString()); } catch { }
            try { AppDomain.CurrentDomain.ProcessExit += (s, e) => OnExit(); } catch { }
        }

        static void OnFirstChance(object sender, FirstChanceExceptionEventArgs e)
        {
            if (_inside) return;
            _inside = true;
            try
            {
                int i = (Interlocked.Increment(ref _next) - 1) & (Ring - 1);
                _ex[i] = e.Exception; _at[i] = _clock.ElapsedMilliseconds; _thread[i] = Thread.CurrentThread.Name;
            }
            catch { }
            finally { _inside = false; }
        }

        // Lo que Open Rails escribe en Trace: los FatalException (y los avisos de hilo en espera del vigilante).
        sealed class Listener : TraceListener
        {
            public override void Write(string message) { }
            public override void WriteLine(string message) { }
            public override void Write(object o) => Seen(o);
            public override void WriteLine(object o) => Seen(o);
            public override void Write(object o, string category) => Seen(o);
            public override void WriteLine(object o, string category) => Seen(o);

            static void Seen(object o)
            {
                if (!(o is Exception ex) || _inside) return;
                _inside = true;
                try
                {
                    string kind = ex.GetType().Name;
                    if (kind == "FatalException") Fatal("Open Rails se cierra por un error", ex.InnerException ?? ex, null);
                    else if (kind == "ThreadWaitException") { if (_waits++ < 3) Agent.Log("Open Rails: un hilo espera demasiado: " + Clip(ex.ToString(), 3000)); }
                }
                catch { }
                finally { _inside = false; }
            }
        }

        static void Fatal(string what, Exception ex, string text)
        {
            if (Interlocked.Increment(ref _fatals) > 3) return;
            var sb = new StringBuilder();
            sb.Append(what).Append(" (hilo ").Append(Thread.CurrentThread.Name ?? "?").Append("): ");
            sb.Append(ex != null ? ex.ToString() : text ?? "?");
            Agent.Log(Clip(sb.ToString(), 12000));
            Recent(10000);
        }

        static void OnExit()
        {
            try
            {
                if (_fatals > 0) { Agent.Log("Open Rails terminado (tras el error de arriba)"); return; }
                Agent.Log("Open Rails terminado");
                Recent(5000);
            }
            catch { }
        }

        // las excepciones de los últimos ms milisegundos (de la más antigua a la más nueva)
        static void Recent(long ms)
        {
            try
            {
                long now = _clock.ElapsedMilliseconds;
                int last = Volatile.Read(ref _next);
                var sb = new StringBuilder();
                int n = 0;
                for (int k = Ring; k >= 1; k--)
                {
                    int i = (last - k) & (Ring - 1);
                    var ex = _ex[i];
                    if (ex == null || now - _at[i] > ms) continue;
                    n++;
                    sb.Append("\n  · hace ").Append(((now - _at[i]) / 1000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)).Append(" s, hilo ").Append(_thread[i] ?? "?").Append(": ")
                      .Append(ex.GetType().FullName).Append(": ").Append(Clip(ex.Message, 300));
                    var st = ex.StackTrace;
                    if (!string.IsNullOrEmpty(st)) sb.Append("\n    ").Append(Clip(st.Replace("\r\n", "\n").Replace("\n", "\n    "), 1500));
                }
                if (n > 0) Agent.Log("últimas excepciones antes de cerrarse (" + n + "):" + sb);
            }
            catch { }
        }

        static string Clip(string s, int max) => s == null ? "" : s.Length <= max ? s : s.Substring(0, max) + "…";
    }
}
