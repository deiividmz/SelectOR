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
        sealed class TtRoadPlan { public string PatFile, Train; public List<(string name, double arr, double dep)> Schedule; public bool Timetable; }
        TtRoadPlan _roadTtPlan;
        string _roadPatPending;
        TtRoadPlan _roadPlanWaiting;   // plan de un horario o una actividad a la espera de que el escenario esté abierto
        bool _roadHudWaiting;

        TtRoadPlan BuildTtRoadPlan()
        {
            var tr = _cboTTTrain?.SelectedItem as Orts.Formats.OR.TimetableFileLite.TrainInformation;
            if (tr == null) return null;
            var plan = new TtRoadPlan { Train = tr.Train, PatFile = ResolvePath(tr.Path)?.FilePath, Timetable = true };
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
                        // dos paradas seguidas en la misma estación (cambia de andén): una fila, de la primera llegada a
                        // la última salida
                        if (sched.Count > 0 && string.Equals(sched[^1].name, name, StringComparison.OrdinalIgnoreCase)) { sched[^1] = (name, sched[^1].arr, dep); continue; }
                        sched.Add((name, arr, dep));
                    }
                    if (sched.Count > 0) plan.Schedule = sched;
                }
            }
            catch { }
            return plan.PatFile == null && plan.Schedule == null ? null : plan;
        }

        // Aplica el plan (de un horario o de una actividad) a la hoja de ruta de la conducción en curso.
        // Como en Conducción libre, la hoja de ruta sale con la simulación ya abierta (primera posición del tren):
        // mientras Open Rails carga solo se prepara el grafo de vías.
        void ApplyRoadPlan(TtRoadPlan plan)
        {
            if (plan == null || _roadTimer == null) return;
            if (!_scenarioReady) { _roadPlanWaiting = plan; EnsureRoadGraph(); return; }
            _roadPlanWaiting = null;
            _road.Schedule = plan.Schedule;
            _road.ScheduleFirstPass = plan.Timetable;
            _road.FromPlan = true;
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
            FollowPat(_road, pat);
            if (_road.Points.Count >= 2) RoadReplan();
        }

        // El itinerario sigue el recorrido (.pat) TRAMO A TRAMO, como Open Rails: los puntos del .pat que caen en
        // un desvío son nodos del .tdb, y entre dos seguidos va un tramo concreto; a mitad de cada uno se pone un
        // punto de paso (oculto). Antes solo se usaban la salida, los cambios de sentido y la llegada, y entre ellos
        // el camino MÁS CORTO: si el recorrido iba por otro lado, faltaban estaciones (y sus paradas). La salida, la
        // llegada y los cambios de sentido se ponen en el tramo del recorrido (no en una vía paralela).
        internal static void FollowPat(RoadBook road, string pat)
        {
            var g = road.Graph;
            if (g == null) return;
            var all = PatWorldPoints(pat);
            if (all.Count < 2) return;
            var node = all.Select(p => g.NodeAtWorld(p.x, p.z)).ToArray();
            // Un nodo vale si un tramo lo une al nodo anterior o al siguiente del recorrido: a veces, justo en el
            // mismo sitio, hay el extremo de un trozo de vía suelto (sin conexión), y el itinerario se quedaba sin camino.
            var ok = new bool[node.Length];
            for (int i = 0; i < node.Length; i++)
            {
                if (node[i] < 0) continue;
                bool prevN = i > 0 && node[i - 1] >= 0, nextN = i + 1 < node.Length && node[i + 1] >= 0;
                ok[i] = (!prevN && !nextN)
                     || (prevN && (node[i - 1] == node[i] || g.EdgesBetween(node[i - 1], node[i]).Count > 0))
                     || (nextN && (node[i + 1] == node[i] || g.EdgesBetween(node[i], node[i + 1]).Count > 0));
            }
            for (int i = 0; i < node.Length; i++) if (!ok[i]) node[i] = -1;
            var stops = new HashSet<string>((road.Schedule ?? new List<(string name, double arr, double dep)>())
                .Select(x => RoadBook.NormName(g.CleanName != null ? g.CleanName(x.name ?? "") : x.name)));
            for (int i = 0; i < all.Count; i++)
            {
                bool visible = i == 0 || i == all.Count - 1 || all[i].rev;
                if (visible && OrGeo.TryLatLon(all[i].x, all[i].z, out double la, out double lo))
                {
                    // candidatos: los tramos del desvío anterior y del siguiente del recorrido (aunque entre medias
                    // haya otros puntos que no son desvíos) y los que les siguen; nunca una vía suelta de al lado
                    var cand = new HashSet<int>();
                    if (node[i] >= 0) cand.UnionWith(g.EdgesAt(node[i]));
                    else
                    {
                        int pj = i - 1; while (pj >= 0 && node[pj] < 0) pj--;
                        int nj = i + 1; while (nj < all.Count && node[nj] < 0) nj++;
                        if (pj >= 0) cand.UnionWith(g.EdgesAt(node[pj]));
                        if (nj < all.Count) cand.UnionWith(g.EdgesAt(node[nj]));
                        foreach (int e in cand.ToList()) { cand.UnionWith(g.EdgesAt(g.Edges[e].A)); cand.UnionWith(g.EdgesAt(g.Edges[e].B)); }
                    }
                    var last = road.Points.Count > 0 ? road.Points[^1] : null;
                    bool dup = last != null && !last.Guide && Math.Abs(last.Lat - la) * 111320 < 5 && Math.Abs(last.Lon - lo) * 111320 * Math.Cos(la * Math.PI / 180) < 5;
                    if (!dup && !(cand.Count > 0 && road.AddPointOn(cand, la, lo, 120))) road.AddPoint(la, lo, 120);
                    if (all[i].rev && i > 0 && i < all.Count - 1 && road.Points.Count > 0) road.Points[^1].Reverse = true;
                }
                if (i + 1 < all.Count && node[i] >= 0 && node[i + 1] >= 0)
                {
                    // Dos desvíos pueden estar unidos por más de un tramo (vía general y vía de apartado de una
                    // estación): se coge el que tiene andén de una estación donde el tren para; si no, el más corto.
                    var es = g.EdgesBetween(node[i], node[i + 1]);
                    if (es.Count > 0)
                    {
                        int e = es.OrderByDescending(x => g.Edges[x].Platforms.Any(pl => stops.Contains(RoadBook.NormName(pl.name))))
                                  .ThenBy(x => g.Edges[x].Len).First();
                        road.AddGuide(e, g.Edges[e].Len / 2);
                    }
                }
            }
        }

        // Conducción nueva: itinerario vacío (el grafo se conserva si la ruta es la misma); en Horarios, el del tren.
        void RoadDriveStart()
        {
            RoadDriveStop();
            var plan = _roadTtPlan; _roadTtPlan = null;
            _road.Reset(_curRoute?.Path ?? "");
            _road.Schedule = null; _road.FromPlan = false; _road.ScheduleFirstPass = false;
            _roadPatPending = null; _roadPlanWaiting = null; _roadHudWaiting = false;
            _roadVmax = 0; _roadLimit = double.NaN; _roadGameS = double.NaN;
            try { _roadVmax = TrainMaxKmh(_drivenConsist ?? CurrentDrivenConsist()); } catch { }
            _road.DefaultHalt = false;   // las paradas las marca el maquinista en la hoja de ruta
            _roadTimer = new Timer { Interval = 1000 };
            _roadTimer.Tick += async (s, e) => await RoadTick();
            _roadTimer.Start();
            ApplyRoadPlan(plan);   // Horarios: el recorrido y las horas del tren
            if (plan == null) ApplyExploreItineraryOnDrive();   // Conducción libre: el itinerario marcado en «Tu viaje»
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
                try { g = RouteGraphFor(dir).GetAwaiter().GetResult(); }   // el mismo grafo que el mapa grande
                catch { g = null; }
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        if (!string.Equals(dir, _road.RouteDir, StringComparison.OrdinalIgnoreCase)) return;
                        _road.Building = false; _road.Graph = g;
                        if (g == null) _road.Problem = Tr("Esta ruta no trae el esquema de vías (.tdb): no se puede trazar el itinerario.");
                        if (g != null && _roadSavedPending != null)   // un guardado pedido mientras se montaba el grafo
                        {
                            string m = ApplySavedItinerary(_roadSavedPending);
                            if (HudAlive) _serviceHud.MapNotice(m);
                            return;
                        }
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
                    try { var (_, lim) = TmSpeedLimit(await OrApi.GetStringAsync(_kmHttp, "/API/TRACKMONITORDISPLAY")); if (!double.IsNaN(lim) && lim > 0) _roadLimit = lim; } catch { }
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

        // Escenario ya abierto: el plan pendiente (Horarios / Actividad) pasa a la hoja de ruta.
        void RoadOnScenarioReady()
        {
            if (_roadPlanWaiting != null) ApplyRoadPlan(_roadPlanWaiting);
            if (_roadHudWaiting) { _roadHudWaiting = false; if (_road.Points.Count > 0) ShowRoadHud(); }
        }

        void ShowRoadHud(bool force = false)
        {
            if (!_scenarioReady && !force) { _roadHudWaiting = true; return; }   // no flota sobre la pantalla de carga
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
            _roadHudWaiting = false;
            if (RoadHudAlive && _roadHud.Visible) _roadHud.Hide(); else ShowRoadHud(force: true);   // pedido a mano: sale en el acto
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
            _serviceHud.SavedList = SavedItinerariesHere;          // itinerarios guardados (MainMenuForm.Itinerarios.cs)
            _serviceHud.SaveBook = SaveCurrentItinerary;
            _serviceHud.LoadSaved = LoadSavedItinerary;
            _serviceHud.DeleteSaved = DeleteSavedItinerary;
        }
    }
}
