// Hoja de ruta durante la conducción: el itinerario se marca en el mapa grande (RoadBook.cs) y el HUD
// (RoadBookHud.cs) enseña las estaciones con la hora estimada de paso. Aquí se monta el grafo de vías
// de la ruta (en segundo plano, la primera vez que se usa), se sigue al tren cada segundo, se lee la
// hora del simulador y el límite de velocidad en vigor, y se rehace el itinerario si el tren se sale.

using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SelectOR
{
    public partial class MainMenuForm
    {
        readonly RoadBook _road = new();
        RoadBookHud _roadHud;
        Timer _roadTimer;
        bool _roadBusy;
        double _roadGameS = double.NaN; DateTime _roadGameAt;   // última hora del juego leída y cuándo
        double _roadLimit = double.NaN, _roadVmax;
        int _roadTicks;

        bool RoadHudAlive => _roadHud != null && !_roadHud.IsDisposed;

        // Conducción nueva: itinerario vacío (el grafo se conserva si la ruta es la misma).
        void RoadDriveStart()
        {
            RoadDriveStop();
            _road.Reset(_curRoute?.Path ?? "");
            _roadVmax = 0; _roadLimit = double.NaN; _roadGameS = double.NaN;
            try { _roadVmax = TrainMaxKmh(_drivenConsist ?? CurrentDrivenConsist()); } catch { }
            _road.DefaultHalt = false;   // las paradas las marca el maquinista en la hoja de ruta
            _roadTimer = new Timer { Interval = 1000 };
            _roadTimer.Tick += async (s, e) => await RoadTick();
            _roadTimer.Start();
        }

        void RoadDriveStop()
        {
            try { _roadTimer?.Stop(); _roadTimer?.Dispose(); } catch { }
            _roadTimer = null;
            try { _roadHud?.CloseHud(); } catch { }
            _roadHud = null;
            _road.Editing = false;
        }

        // El mapa grande lo pide al entrar en modo «Itinerario»: el grafo se monta una sola vez por ruta.
        void EnsureRoadGraph()
        {
            if (_road.Graph != null || _road.Building) return;
            string dir = _road.RouteDir;
            if (string.IsNullOrEmpty(dir)) { _road.Problem = Tr("No hay ruta elegida."); return; }
            _road.Building = true; _road.Problem = null;
            Task.Run(() =>
            {
                RouteGraph g = null;
                try
                {
                    var good = TdbNames(dir);
                    g = RouteGraph.Build(dir, n => CleanMapStation(FixTdbName(n, good)));
                }
                catch { g = null; }
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        if (!string.Equals(dir, _road.RouteDir, StringComparison.OrdinalIgnoreCase)) return;
                        _road.Building = false; _road.Graph = g;
                        if (g == null) _road.Problem = Tr("Esta ruta no trae el esquema de vías (.tdb): no se puede trazar el itinerario.");
                        RoadReplan();
                    }));
                }
                catch { }
            });
        }

        (bool has, double lat, double lon, bool hasHdg, double hdg) RoadTrain()
        {
            var (has, lat, lon) = SmoothPos();
            bool hasHdg = false; double hdg = 0;
            var pts = _driveTrail.Points;
            if (pts.Count >= 2) { var a = pts[^2]; var b = pts[^1]; hdg = LiveMates.Bearing(a.lat, a.lon, b.lat, b.lon); hasHdg = true; }
            return (has && !(lat == 0 && lon == 0), lat, lon, hasHdg, hdg);
        }

        void RoadReplan()
        {
            var t = RoadTrain();
            _road.Replan(t.has, t.lat, t.lon, t.hasHdg, t.hdg);
            _road.Track(t.has, t.lat, t.lon, RoadGameNow());
            _road.ComputeEtas(_roadLimit, _roadVmax, Math.Max(0, _prefs?.RoadDwellS ?? 30));
            if (_road.Points.Count > 0) ShowRoadHud();
            _roadHud?.Invalidate();
        }

        // Hora del juego ahora: la última leída más el tiempo real desde entonces (salvo en pausa).
        double RoadGameNow()
        {
            if (double.IsNaN(_roadGameS)) return double.NaN;
            return _svcPaused ? _roadGameS : _roadGameS + (DateTime.UtcNow - _roadGameAt).TotalSeconds;
        }

        async Task RoadTick()
        {
            if (_roadBusy || _roadTimer == null) return;
            _roadBusy = true;
            try
            {
                _roadTicks++;
                if (_kmHttp != null && (_roadTicks % 2 == 0 || double.IsNaN(_roadGameS)) && _road.HasPlan)
                {
                    double gs = await FetchGameSeconds();
                    if (!double.IsNaN(gs)) { _roadGameS = gs; _roadGameAt = DateTime.UtcNow; }
                }
                if (_kmHttp != null && _roadTicks % 4 == 0 && _road.HasPlan)
                {
                    try { var (_, lim) = TmSpeedLimit(await _kmHttp.GetStringAsync("/API/TRACKMONITORDISPLAY")); if (!double.IsNaN(lim) && lim > 0) _roadLimit = lim; } catch { }
                }
                if (_roadTimer == null) return;
                var t = RoadTrain();
                _road.Track(t.has, t.lat, t.lon, RoadGameNow());
                // fuera del itinerario más de 8 s: se rehace desde donde está el tren por los puntos que faltan
                if (_road.OffRoute && (DateTime.UtcNow - _road.OffSinceUtc).TotalSeconds > 8) RoadReplan();
                else if (!_road.HasPlan && _road.Points.Count > _road.ReachedPoints && _road.Graph != null && t.has && _roadTicks % 3 == 0) RoadReplan();
                _road.ComputeEtas(_roadLimit, _roadVmax, Math.Max(0, _prefs?.RoadDwellS ?? 30));
                if (RoadHudAlive && _roadHud.Visible) _roadHud.Invalidate();
            }
            catch { }
            finally { _roadBusy = false; }
        }

        void ShowRoadHud()
        {
            if (RoadHudAlive) { if (!_roadHud.Visible) _roadHud.Show(); return; }
            try
            {
                _roadHud = new RoadBookHud(_prefs, _road)
                {
                    GameNow = RoadGameNow,
                    Info = () => _roadVmax > 0 ? string.Format(Tr("tren: máx. {0} km/h"), Math.Round(_roadVmax)) : "",
                    CloseRequested = () => { _roadHud?.Hide(); },
                    Changed = () => { _road.ComputeEtas(_roadLimit, _roadVmax, Math.Max(0, _prefs?.RoadDwellS ?? 30)); },
                    OpenMap = () => { OpenBigMapForRoute(); }
                };
                _roadHud.Show();
            }
            catch { _roadHud = null; }
        }

        // Botón «Hoja de ruta» de la barra superior: sin itinerario abre el mapa grande para marcarlo;
        // con itinerario enseña u oculta el HUD.
        void ToggleRoadHudFromBar()
        {
            if (_road.Points.Count == 0) { OpenBigMapForRoute(); return; }
            if (RoadHudAlive && _roadHud.Visible) _roadHud.Hide(); else ShowRoadHud();
        }

        void OpenBigMapForRoute()
        {
            if (!HudAlive || !_serviceHud.BigMapOpen) ToggleBigMapFromBar();
            if (HudAlive && _serviceHud.BigMapOpen) { _road.Editing = true; EnsureRoadGraph(); }
        }

        // Lo que el mapa grande necesita de la hoja de ruta.
        void WireRoadToHud()
        {
            if (!HudAlive) return;
            _serviceHud.Book = _road;
            _serviceHud.BookEnsureGraph = EnsureRoadGraph;
            _serviceHud.BookChanged = RoadReplan;
        }
    }
}
