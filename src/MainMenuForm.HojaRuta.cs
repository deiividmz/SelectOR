// Hoja de ruta durante la conducción: el itinerario se marca en el mapa grande (RoadBook.cs) y el HUD
// (RoadBookHud.cs) enseña las estaciones con la hora estimada de paso. Aquí se monta el grafo de vías
// de la ruta (en segundo plano, la primera vez que se usa), se sigue al tren cada segundo, se lee la
// hora del simulador y el límite de velocidad en vigor, y se rehace el itinerario si el tren se sale.

using System;
using System.Collections.Generic;
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

        // Horario del tren elegido en Horarios: se guarda al pulsar CONDUCIR y la hoja de ruta lo usa al arrancar.
        sealed class TtRoadPlan { public string PatFile, Train; public List<(string name, double arr, double dep)> Schedule; }
        TtRoadPlan _roadTtPlan;
        string _roadPatPending;

        TtRoadPlan BuildTtRoadPlan()
        {
            var tr = _cboTTTrain?.SelectedItem as Orts.Formats.OR.TimetableFileLite.TrainInformation;
            if (tr == null) return null;
            var plan = new TtRoadPlan { Train = tr.Train, PatFile = ResolvePath(tr.Path)?.FilePath };
            if (_ttStops.TryGetValue(tr, out var stops) && stops != null && stops.Count > 0)
            {
                static double Sec(string hm) { int m = TtStops.Minutes(hm); return m < 0 ? double.NaN : m * 60.0; }
                plan.Schedule = stops.Select(x => (x.Station, Sec(x.Arr), Sec(x.Dep))).ToList();
            }
            return plan.PatFile == null && plan.Schedule == null ? null : plan;
        }

        // Actividad: su recorrido y las paradas del tren del jugador (andén → estación del .tdb) con sus horas.
        static TtRoadPlan BuildActRoadPlan(ORTS.Menu.Activity a, string routeDir)
        {
            if (a == null) return null;
            var plan = new TtRoadPlan { Train = a.Name };
            try { plan.PatFile = a.Path?.FilePath; } catch { }
            try
            {
                var act = new Orts.Formats.Msts.ActivityFile(a.FilePath);
                var list = act.Tr_Activity?.Tr_Activity_File?.Player_Service_Definition?.Player_Traffic_Definition?.Player_Traffic_List;
                var tdb = string.IsNullOrEmpty(routeDir) ? Array.Empty<string>() : System.IO.Directory.GetFiles(routeDir, "*.tdb");
                if (list != null && list.Count > 0 && tdb.Length > 0)
                {
                    var db = new Orts.Formats.Msts.TrackDatabaseFile(tdb[0]);
                    var station = new Dictionary<uint, string>();
                    foreach (var it in db.TrackDB?.TrItemTable ?? Array.Empty<Orts.Formats.Msts.TrItem>())
                        if (it is Orts.Formats.Msts.PlatformItem pl && !string.IsNullOrWhiteSpace(pl.Station)) station[pl.TrItemId] = pl.Station.Trim();
                    var sched = new List<(string name, double arr, double dep)>();
                    foreach (var item in list)
                    {
                        if (item == null || !station.TryGetValue((uint)item.PlatformStartID, out var name)) continue;
                        double arr = item.ArrivalTime.TimeOfDay.TotalSeconds, dep = item.DepartTime.TimeOfDay.TotalSeconds;
                        if (sched.Count > 0 && string.Equals(sched[^1].name, name, StringComparison.OrdinalIgnoreCase)) continue;   // andenes de la misma estación
                        sched.Add((name, arr, dep));
                    }
                    if (sched.Count > 0) plan.Schedule = sched;
                }
            }
            catch { }
            return plan.PatFile == null && plan.Schedule == null ? null : plan;
        }

        // Aplica el plan (de un horario o de una actividad) a la hoja de ruta de la conducción en curso.
        void ApplyRoadPlan(TtRoadPlan plan)
        {
            if (plan == null || _roadTimer == null) return;
            _road.Schedule = plan.Schedule;
            if (plan.Schedule != null) _road.ClearHaltChoices();
            _roadPatPending = plan.PatFile;
            if (_roadPatPending != null) { if (_road.Graph != null) LoadPatIntoRoad(); else EnsureRoadGraph(); }
            else if (_road.HasPlan) RoadReplan();
        }

        // Puntos del recorrido (.pat) en el mundo: la cadena principal de nodos, como en el mapa del recorrido,
        // marcando los de cambio de sentido.
        static List<(float x, float z, bool rev)> PatWorldPoints(string patFile)
        {
            var list = new List<(float x, float z, bool rev)>();
            try
            {
                var pf = new Orts.Formats.Msts.PathFile(patFile);
                var pdps = pf.TrackPDPs; var nodes = pf.TrPathNodes;
                if (nodes != null && nodes.Count > 0)
                {
                    uint idx = 0; int guard = 0;
                    while (idx != 0xffffffff && idx < nodes.Count && guard++ < 100000)
                    {
                        var n = nodes[(int)idx];
                        if (n.fromPDP < pdps.Count) { var p = pdps[(int)n.fromPDP]; list.Add((p.TileX * 2048f + p.X, p.TileZ * 2048f + p.Z, (n.pathFlags & 0x03) != 0)); }
                        idx = n.nextMainNode;
                    }
                }
                if (list.Count < 2) { list.Clear(); foreach (var p in pdps) list.Add((p.TileX * 2048f + p.X, p.TileZ * 2048f + p.Z, false)); }
            }
            catch { }
            return list;
        }

        // Con el grafo de la ruta listo: el recorrido del horario pasa a ser el itinerario de la hoja de ruta.
        void LoadPatIntoRoad()
        {
            string pat = _roadPatPending; _roadPatPending = null;
            if (string.IsNullOrEmpty(pat) || _road.Graph == null) return;
            var all = PatWorldPoints(pat);
            if (all.Count < 2) return;
            // Solo la salida, los cambios de sentido y la llegada: entre ellos, el camino por la vía. Ajustar CADA
            // punto del .pat a la vía más cercana metía apartaderos y vías paralelas, y el itinerario daba vueltas.
            var pts = new List<(float x, float z, bool rev)>();
            for (int i = 0; i < all.Count; i++) if (i == 0 || i == all.Count - 1 || all[i].rev) pts.Add(all[i]);
            double lastLa = double.NaN, lastLo = double.NaN;
            for (int i = 0; i < pts.Count; i++)
            {
                if (!OrGeo.TryLatLon(pts[i].x, pts[i].z, out double la, out double lo)) continue;
                if (!double.IsNaN(lastLa))
                {
                    double dy = (la - lastLa) * 111320, dx = (lo - lastLo) * 111320 * Math.Cos(la * Math.PI / 180);
                    if (dx * dx + dy * dy < 40 * 40) continue;   // puntos casi iguales
                }
                if (_road.AddPoint(la, lo, 120)) { lastLa = la; lastLo = lo; }
            }
            if (_road.Points.Count >= 2) RoadReplan();
        }

        // Conducción nueva: itinerario vacío (el grafo se conserva si la ruta es la misma); en Horarios, el del tren.
        void RoadDriveStart()
        {
            RoadDriveStop();
            var plan = _roadTtPlan; _roadTtPlan = null;
            _road.Reset(_curRoute?.Path ?? "");
            _road.Schedule = null;
            _roadPatPending = null;
            _roadVmax = 0; _roadLimit = double.NaN; _roadGameS = double.NaN;
            try { _roadVmax = TrainMaxKmh(_drivenConsist ?? CurrentDrivenConsist()); } catch { }
            _road.DefaultHalt = false;   // las paradas las marca el maquinista en la hoja de ruta
            _roadTimer = new Timer { Interval = 1000 };
            _roadTimer.Tick += async (s, e) => await RoadTick();
            _roadTimer.Start();
            ApplyRoadPlan(plan);   // Horarios: el recorrido y las horas del tren
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
                        if (g != null && _roadPatPending != null) { LoadPatIntoRoad(); return; }   // (ya replanifica)
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
            _road.ComputeEtas(_roadLimit, _roadVmax, Math.Max(0, _prefs?.RoadDwellS ?? 30), RoadGameNow());
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
                _road.ComputeEtas(_roadLimit, _roadVmax, Math.Max(0, _prefs?.RoadDwellS ?? 30), RoadGameNow());
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
                    Changed = () => { _road.ComputeEtas(_roadLimit, _roadVmax, Math.Max(0, _prefs?.RoadDwellS ?? 30), RoadGameNow()); },
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
