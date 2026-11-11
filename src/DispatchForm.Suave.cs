// (2026-11-09) Trenes del puesto sin saltos: la sala manda la posición de cada tren cada uno o dos segundos. Entre una y
// la siguiente, el tren se dibuja avanzando poco a poco de la de antes a la nueva (en el tiempo que tardó en llegar), y el
// plano se repinta unas diez veces por segundo mientras alguno se mueve (menos si dibujar cuesta). La cámara del
// seguimiento (DispatchForm.Trenes.cs) va con él.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows.Forms;

namespace SelectOR
{
    public sealed partial class DispatchForm
    {
        readonly Dictionary<string, (double x0, double z0, double x1, double z1, DateTime at, double secs)> _smooth = new(StringComparer.Ordinal);
        Timer _smoothTimer;
        double _paintMs = 20;

        void InitSmooth()
        {
            _smoothTimer = new Timer { Interval = 100 };
            _smoothTimer.Tick += (s, e) =>
            {
                if (IsDisposed || WindowState == FormWindowState.Minimized || !Visible) return;
                var p = _pilot; if (p == null || !p.Ready) return;
                var now = DateTime.UtcNow;
                bool moving = p.Trains.Any(t => t.kmh >= 1) || _smooth.Values.Any(v => (now - v.at).TotalSeconds < v.secs) || now < _locateUntil.AddSeconds(0.2);   // (y el aro del tren localizado)
                if (!moving) return;
                FollowTick();
                _canvas.Invalidate();
                // (si dibujar el plano cuesta, menos veces por segundo)
                _smoothTimer.Interval = (int)Math.Max(80, Math.Min(400, _paintMs * 2.5));
            };
            _smoothTimer.Start();
            FormClosed += (s, e) => _smoothTimer.Stop();
        }

        void PaintTimed(System.Drawing.Graphics g)
        {
            var sw = Stopwatch.StartNew();
            Paint_(g);
            _paintMs = _paintMs * 0.8 + sw.Elapsed.TotalMilliseconds * 0.2;
        }

        // la posición de un tren para dibujarlo (en lat/lon): entre la de antes y la última que llegó
        bool TrainLL(string name, int tx, int tz, double x, double z, out double la, out double lo)
        {
            var (sx, sz) = SmoothWorld(name, tx * 2048.0 + x, tz * 2048.0 + z);
            return OrGeo.TryLatLon(sx, sz, out la, out lo);
        }

        (double x, double z) SmoothWorld(string name, double wx, double wz)
        {
            var now = DateTime.UtcNow;
            if (name == null) return (wx, wz);
            if (!_smooth.TryGetValue(name, out var s)) { _smooth[name] = (wx, wz, wx, wz, now, 1); return (wx, wz); }
            if (s.x1 != wx || s.z1 != wz)
            {
                var (cx, cz) = Lerp(s, now);   // (desde donde se ve ahora: sin saltos aunque llegue antes de tiempo)
                double gap = Math.Max(0.4, Math.Min(3.0, (now - s.at).TotalSeconds));
                double jump = Math.Sqrt((wx - cx) * (wx - cx) + (wz - cz) * (wz - cz));
                s = jump > 400 ? (wx, wz, wx, wz, now, gap) : (cx, cz, wx, wz, now, gap);   // (más de 400 m: se ha movido de sitio, sin deslizarse)
                _smooth[name] = s;
            }
            return Lerp(s, now);
        }

        static (double x, double z) Lerp((double x0, double z0, double x1, double z1, DateTime at, double secs) s, DateTime now)
        {
            double f = Math.Max(0, Math.Min(1, (now - s.at).TotalSeconds / Math.Max(0.05, s.secs)));
            return (s.x0 + (s.x1 - s.x0) * f, s.z0 + (s.z1 - s.z0) * f);
        }
    }
}
