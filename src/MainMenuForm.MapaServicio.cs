// Mapa del informe del servicio («Servicio registrado»): el rastro que ha hecho el tren DESDE QUE SE ABRIÓ EL
// SERVICIO, con la hora del simulador en cada punto, para dibujar sobre el detalle de la ruta el recorrido real, la
// salida y la llegada con su hora, las paradas comerciales y dónde se anotó cada infracción del carné.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace SelectOR
{
    public partial class MainMenuForm
    {
        // lat, lon, segundo del servicio (para situar las infracciones, que llevan at_s) y hora del juego (NaN = no se sabe)
        readonly List<(double lat, double lon, float s, double game)> _svcTrail = new();
        const int SvcTrailMax = 30000;

        void SvcTrailAdd(double lat, double lon)
        {
            if (_pendingServiceId == null) return;
            if (_svcTrail.Count > 0)
            {
                var l = _svcTrail[^1];
                if (Haversine(l.lat, l.lon, lat, lon) < 8) return;
            }
            _svcTrail.Add((lat, lon, (float)ServiceSecondsExact(), _svcLastGameS));
            if (_svcTrail.Count > SvcTrailMax)   // viaje muy largo: uno de cada dos (la salida y el último quedan)
            {
                var keep = new List<(double, double, float, double)>(SvcTrailMax / 2 + 2);
                for (int i = 0; i < _svcTrail.Count; i++) if (i % 2 == 0 || i == _svcTrail.Count - 1) keep.Add(_svcTrail[i]);
                _svcTrail.Clear(); _svcTrail.AddRange(keep);
            }
        }

        static string GameClock(double secs)
        {
            if (double.IsNaN(secs)) return "";
            int t = (int)secs % 86400; if (t < 0) t += 86400;
            return $"{t / 3600:00}:{t % 3600 / 60:00}";
        }

        // Foto del servicio que se acaba de cerrar (antes de que se vacíen sus listas). null = sin rastro.
        async Task<ServiceResultDialog.TripMap> BuildTripMap(DateTime? openedUtc)
        {
            try
            {
                if (_svcTrail.Count < 2) return null;
                var m = new ServiceResultDialog.TripMap();
                foreach (var p in _svcTrail) m.Trail.Add((p.lat, p.lon));
                m.StartTime = GameClock(_svcTrail[0].game);
                m.EndTime = GameClock(double.IsNaN(_svcLastGameS) ? _svcTrail[^1].game : _svcLastGameS);
                foreach (var st in _svcStopsLog)
                    if ((openedUtc == null || st.Utc >= openedUtc.Value.AddSeconds(-5)) && !double.IsNaN(st.Lat))
                        m.Stops.Add((st.Lat, st.Lon, st.Station ?? "", st.Time ?? ""));
                // cada infracción, en el punto del rastro más cercano al segundo del servicio en que se anotó
                foreach (var (code, det) in _infrItems)
                {
                    if (det == null || !det.TryGetValue("at_s", out var o)) continue;
                    double at; try { at = Convert.ToDouble(o, System.Globalization.CultureInfo.InvariantCulture); } catch { continue; }
                    int best = 0; double bd = double.MaxValue;
                    for (int i = 0; i < _svcTrail.Count; i++) { double d = Math.Abs(_svcTrail[i].s - at); if (d < bd) { bd = d; best = i; } }
                    m.Infractions.Add((_svcTrail[best].lat, _svcTrail[best].lon, code));
                }
                m.Detail = await TripDetailFor(_curRoute?.Path ?? "");
                return m;
            }
            catch { return null; }
        }

        // Detalle de la ruta (vía, andenes, apartaderos…): el del HUD si ya está; si no, se calcula (con un límite).
        async Task<HudMapDetail> TripDetailFor(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return null;
            if (_hudDetailCache.TryGetValue(dir, out var d)) return d;
            try
            {
                var job = RouteDetailAsync(dir);
                if (await Task.WhenAny(job, Task.Delay(6000)) == job && job.Result != null) { _hudDetailCache[dir] = job.Result; return job.Result; }
            }
            catch { }
            return null;
        }

        // ---------------- recorrido en el servidor (recorrido-servicio.sql) ----------------

        // Lo que se guarda del servicio que se acaba de registrar: el rastro simplificado y codificado, las horas y
        // las paradas. Se prepara YA (antes de que otro servicio vacíe las listas); null = sin rastro.
        object TrailPayload(DateTime? openedUtc)
        {
            if (_svcTrail.Count < 2) return null;
            var pts = new List<(double lat, double lon)>(_svcTrail.Count);
            foreach (var p in _svcTrail) pts.Add((p.lat, p.lon));
            var simple = SimplifyTrail(pts, 2000);
            var stops = new List<object[]>();
            foreach (var st in _svcStopsLog)
                if ((openedUtc == null || st.Utc >= openedUtc.Value.AddSeconds(-5)) && !double.IsNaN(st.Lat))
                    stops.Add(new object[] { Math.Round(st.Lat, 6), Math.Round(st.Lon, 6), st.Station ?? "", st.Time ?? "" });
            return new
            {
                v = 1, p = EncodePolyline(simple), n = simple.Count,
                st = GameClock(_svcTrail[0].game), et = GameClock(double.IsNaN(_svcLastGameS) ? _svcTrail[^1].game : _svcLastGameS),
                s = stops
            };
        }

        async void SaveServiceTrail(string svc, object payload)
        {
            if (payload == null || string.IsNullOrEmpty(svc)) return;
            try { await Supa.RpcAsync("set_service_trail", new { p_service = svc, p_trail = payload }); } catch { }   // (sin el SQL: no se guarda)
        }

        // El recorrido guardado de un servicio → mapa (sin infracciones: no se guardan). null si no hay o no se entiende.
        internal static ServiceResultDialog.TripMap ParseTrail(JsonElement e)
        {
            try
            {
                if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("p", out var p) || p.ValueKind != JsonValueKind.String) return null;
                var m = new ServiceResultDialog.TripMap();
                m.Trail.AddRange(DecodePolyline(p.GetString()));
                if (m.Trail.Count < 2) return null;
                if (e.TryGetProperty("st", out var st) && st.ValueKind == JsonValueKind.String) m.StartTime = st.GetString();
                if (e.TryGetProperty("et", out var et) && et.ValueKind == JsonValueKind.String) m.EndTime = et.GetString();
                if (e.TryGetProperty("s", out var ss) && ss.ValueKind == JsonValueKind.Array)
                    foreach (var x in ss.EnumerateArray())
                        if (x.ValueKind == JsonValueKind.Array && x.GetArrayLength() >= 4)
                            m.Stops.Add((x[0].GetDouble(), x[1].GetDouble(), x[2].GetString() ?? "", x[3].GetString() ?? ""));
                return m;
            }
            catch { return null; }
        }

        // Douglas-Peucker en metros: fuera los puntos que no se apartan más de «tol» de la recta entre sus vecinos.
        // Se empieza con 3 m y se dobla mientras queden más de «max» puntos.
        internal static List<(double lat, double lon)> SimplifyTrail(List<(double lat, double lon)> pts, int max)
        {
            if (pts.Count <= 2) return new List<(double, double)>(pts);
            double lat0 = pts[0].lat, lon0 = pts[0].lon, kx = 111320.0 * Math.Cos(lat0 * Math.PI / 180), ky = 111320.0;
            var xy = pts.Select(p => ((p.lon - lon0) * kx, (p.lat - lat0) * ky)).ToArray();
            for (double tol = 3; ; tol *= 2)
            {
                var keep = new bool[pts.Count]; keep[0] = keep[^1] = true;
                var stack = new Stack<(int a, int b)>(); stack.Push((0, pts.Count - 1));
                while (stack.Count > 0)
                {
                    var (a, b) = stack.Pop();
                    if (b - a < 2) continue;
                    var (ax, ay) = xy[a]; var (bx, by) = xy[b];
                    double dx = bx - ax, dy = by - ay, len2 = dx * dx + dy * dy;
                    int best = -1; double bd = tol * tol;
                    for (int i = a + 1; i < b; i++)
                    {
                        var (px, py) = xy[i];
                        double t = len2 > 0 ? Math.Max(0, Math.Min(1, ((px - ax) * dx + (py - ay) * dy) / len2)) : 0;
                        double ex = ax + t * dx - px, ey = ay + t * dy - py, d2 = ex * ex + ey * ey;
                        if (d2 > bd) { bd = d2; best = i; }
                    }
                    if (best < 0) continue;
                    keep[best] = true; stack.Push((a, best)); stack.Push((best, b));
                }
                var res = new List<(double, double)>();
                for (int i = 0; i < pts.Count; i++) if (keep[i]) res.Add(pts[i]);
                if (res.Count <= max || tol > 5000) return res;
            }
        }

        // «Encoded polyline» de Google (precisión 1e-5, ~1 m): unos 6-8 caracteres por punto; la web lo lee igual.
        internal static string EncodePolyline(List<(double lat, double lon)> pts)
        {
            var sb = new StringBuilder(pts.Count * 8);
            long pla = 0, plo = 0;
            void Enc(long v)
            {
                v = v < 0 ? ~(v << 1) : v << 1;
                while (v >= 0x20) { sb.Append((char)((0x20 | (v & 0x1f)) + 63)); v >>= 5; }
                sb.Append((char)(v + 63));
            }
            foreach (var (la, lo) in pts)
            {
                long a = (long)Math.Round(la * 1e5), b = (long)Math.Round(lo * 1e5);
                Enc(a - pla); Enc(b - plo); pla = a; plo = b;
            }
            return sb.ToString();
        }

        internal static List<(double lat, double lon)> DecodePolyline(string s)
        {
            var res = new List<(double, double)>();
            if (string.IsNullOrEmpty(s)) return res;
            int i = 0; long la = 0, lo = 0;
            long Dec()
            {
                long r = 0; int sh = 0, b;
                do { b = s[i++] - 63; r |= (long)(b & 0x1f) << sh; sh += 5; } while (b >= 0x20);
                return (r & 1) != 0 ? ~(r >> 1) : r >> 1;
            }
            try { while (i < s.Length) { la += Dec(); lo += Dec(); res.Add((la / 1e5, lo / 1e5)); } } catch { }
            return res;
        }
    }
}
