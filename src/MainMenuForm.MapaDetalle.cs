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

        async void PushHudDetail()
        {
            string dir = _curRoute?.Path ?? "";
            if (dir.Length == 0) return;
            var hud = _serviceHud;
            if (hud == null || hud.IsDisposed) return;
            if (!_hudDetailCache.TryGetValue(dir, out var d))
            {
                try
                {
                    var g = await RouteGraphFor(dir);
                    d = g == null ? null : await Task.Run(() => HudMapDetail.From(g));
                }
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
