// Detalle del mapa grande del HUD (MapDetail.cs): se calcula una vez por ruta a partir del grafo de vías (el
// mismo que usa la hoja de ruta; se construye una sola vez por ruta) y se pasa al HUD.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SelectOR
{
    public partial class MainMenuForm
    {
        readonly Dictionary<string, Task<RouteGraph>> _graphTasks = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, HudMapDetail> _hudDetailCache = new(StringComparer.OrdinalIgnoreCase);

        // El grafo de vías de una ruta, construido una sola vez (hoja de ruta y mapa grande lo comparten).
        Task<RouteGraph> RouteGraphFor(string dir)
        {
            lock (_graphTasks)
            {
                if (_graphTasks.TryGetValue(dir, out var t) && !(t.IsCompleted && t.Result == null)) return t;
                t = Task.Run(() =>
                {
                    try { var good = TdbNames(dir); return RouteGraph.Build(dir, n => CleanMapStation(FixTdbName(n, good))); }
                    catch { return null; }
                });
                _graphTasks[dir] = t;
                return t;
            }
        }

        // El detalle del .tdb de una ruta (la vía del HUD y lo demás), calculado una sola vez y compartido por el HUD, el
        // informe del servicio y los mapas de la ruta (Ruta, «Mapa», Actividad, Horarios). null si no se puede.
        readonly Dictionary<string, Task<HudMapDetail>> _detailTasks = new(StringComparer.OrdinalIgnoreCase);

        Task<HudMapDetail> RouteDetailAsync(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return Task.FromResult<HudMapDetail>(null);
            lock (_detailTasks)
            {
                if (_detailTasks.TryGetValue(dir, out var t) && !(t.IsCompleted && t.Result == null)) return t;
                t = Task.Run(async () =>
                {
                    try { var g = await RouteGraphFor(dir); return g == null ? null : HudMapDetail.From(g); }
                    catch { return null; }
                });
                _detailTasks[dir] = t;
                return t;
            }
        }

        // Solo la vía del HUD (Ruta, «Mapa», Actividad, Horarios): si el detalle completo ya está, el suyo; si no, solo la
        // vía, que en las rutas grandes sale en una fracción del tiempo del grafo entero.
        readonly Dictionary<string, Task<HudMapDetail>> _trackTasks = new(StringComparer.OrdinalIgnoreCase);

        Task<HudMapDetail> RouteTrackAsync(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return Task.FromResult<HudMapDetail>(null);
            lock (_detailTasks)
                if (_detailTasks.TryGetValue(dir, out var full) && full.IsCompletedSuccessfully && full.Result != null) return full;
            lock (_trackTasks)
            {
                if (_trackTasks.TryGetValue(dir, out var t) && !(t.IsCompleted && t.Result == null)) return t;
                t = Task.Run(() => { try { return RouteGraph.TrackLatLon(dir); } catch { return null; } });
                _trackTasks[dir] = t;
                return t;
            }
        }

        async void PushHudDetail()
        {
            string dir = _curRoute?.Path ?? "";
            if (dir.Length == 0) return;
            var hud = _serviceHud;
            if (hud == null || hud.IsDisposed) return;
            if (!_hudDetailCache.TryGetValue(dir, out var d))
            {
                try { d = await RouteDetailAsync(dir); }
                catch { d = null; }
                if (d == null) return;
                _hudDetailCache[dir] = d;
            }
            if (!string.Equals(dir, _curRoute?.Path, StringComparison.OrdinalIgnoreCase)) return;
            hud = _serviceHud;
            try { if (hud != null && !hud.IsDisposed) hud.SetDetail(d); } catch { }
        }
    }
}
