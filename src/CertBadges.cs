// Los distintivos de la certificación junto al nombre (2026-11-08; sql/certificacion.sql → cert_badges): «🎓A» (maquinista
// B, A o A+) y «👮» (jefe de circulación titulado). Para la leyenda «EN LA SALA» del mapa grande, el chat de la sala (HUD y
// puesto) y la radio. Se piden a Supabase los nombres que van apareciendo (como mucho cada 20 s; cada uno se vuelve a mirar a
// los 10 min). Sin certificacion.sql, nada.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace SelectOR
{
    public static class CertBadges
    {
        static readonly object _lock = new object();
        static readonly Dictionary<string, (string Tag, DateTime At)> _map = new(StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> _want = new(StringComparer.OrdinalIgnoreCase);
        static DateTime _next = DateTime.MinValue;
        static bool _busy, _off;
        public static event Action Changed;   // (en otro hilo)

        // « 🎓A 👮» o "" (y, si no se sabe aún, se pide)
        public static string Tag(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || _off) return "";
            string t = "";
            bool ask;
            lock (_lock)
            {
                ask = !_map.TryGetValue(name, out var v) || DateTime.UtcNow - v.At > TimeSpan.FromMinutes(10);
                if (_map.TryGetValue(name, out var v2)) t = v2.Tag;
                if (ask) _want.Add(name);
            }
            if (ask) Kick();
            return t.Length > 0 ? " " + t : "";
        }

        static void Kick()
        {
            lock (_lock) { if (_busy || DateTime.UtcNow < _next || _want.Count == 0) return; _busy = true; _next = DateTime.UtcNow.AddSeconds(20); }
            _ = Task.Run(Fetch);
        }

        static async Task Fetch()
        {
            string[] names;
            lock (_lock) { names = _want.Take(200).ToArray(); foreach (var n in names) _want.Remove(n); }
            try
            {
                var (json, err) = await Supa.RpcAsync("cert_badges", new { p_names = names });
                if (err != null)
                {
                    if (err.IndexOf("PGRST202", StringComparison.OrdinalIgnoreCase) >= 0 || err.IndexOf("Could not find", StringComparison.OrdinalIgnoreCase) >= 0) _off = true;
                    lock (_lock) foreach (var n in names) _map[n] = ("", DateTime.UtcNow);   // (no se vuelve a preguntar enseguida)
                    return;
                }
                var got = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                using (var d = JsonDocument.Parse(json))
                    foreach (var e in d.RootElement.EnumerateArray())
                    {
                        string n = e.TryGetProperty("name", out var nn) ? nn.GetString() : null;
                        if (string.IsNullOrEmpty(n)) continue;
                        string dl = e.TryGetProperty("driver", out var dv) && dv.ValueKind == JsonValueKind.String ? dv.GetString() : "";
                        string jf = e.TryGetProperty("jefe", out var jv) && jv.ValueKind == JsonValueKind.String ? jv.GetString() : "";
                        var parts = new List<string>();
                        if (!string.IsNullOrEmpty(dl)) parts.Add("🎓" + dl);
                        if (jf == "titulo") parts.Add("👮");
                        got[n] = string.Join(" ", parts);
                    }
                bool changed = false;
                lock (_lock)
                    foreach (var n in names)
                    {
                        string t = got.TryGetValue(n, out var x) ? x : "";
                        if (!_map.TryGetValue(n, out var old) || old.Tag != t) changed = true;
                        _map[n] = (t, DateTime.UtcNow);
                    }
                if (changed) try { Changed?.Invoke(); } catch { }
            }
            catch { }
            finally { lock (_lock) _busy = false; if (_want.Count > 0) _ = Task.Delay(21000).ContinueWith(_ => Kick()); }
        }
    }
}
