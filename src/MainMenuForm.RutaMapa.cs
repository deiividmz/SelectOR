// Pestaña «Ruta» con mapa en vivo: si la ruta elegida tiene su .tdb (y esta versión de Open Rails trae la
// conversión a lat/lon), la pestaña enseña solo la descripción, compacta, y debajo el mapa de la ruta con
// los usuarios de la comunidad SelectOR que circulan ahora por ella (live_route_positions, mapa-web.sql:
// los mismos datos que el mapa de la web). Si no se puede montar el trazado, la pestaña queda como
// siempre: imagen de cabecera, descripción y «Ver mapa de la ruta».

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using ORTS.Menu;

namespace SelectOR
{
    public partial class MainMenuForm
    {
        RouteLiveMap _rutaMap;
        Panel _rutaBtnHost;
        bool _rutaMapMode;
        readonly Dictionary<string, RouteMapData> _rutaMapCache = new(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> _rutaMapNone = new(StringComparer.OrdinalIgnoreCase);   // rutas sin trazado utilizable
        int _rutaMapToken;
        Timer _rutaLiveTimer;
        bool _rutaLiveBusy, _rutaLiveNoServer;
        string _rutaLiveRoute;
        const int RutaLiveIntervalMs = 2500;

        // Al elegir ruta: decide el diseño de la pestaña y carga el trazado (en segundo plano, una vez por ruta).
        void RutaMapRouteChanged(Route route)
        {
            int token = ++_rutaMapToken;
            _rutaMap.Live.Clear();
            _rutaLiveRoute = route == null ? null : RouteIds.IdOf(route.Path, route.Name);
            string dir = route?.Path;
            if (string.IsNullOrEmpty(dir) || _rutaMapNone.Contains(dir) || !RutaMapPossible(dir)) { SetRutaMapMode(false); return; }
            if (_rutaMapCache.TryGetValue(dir, out var cached)) { _rutaMap.SetData(cached); SetRutaMapMode(true); RutaLiveKick(); return; }

            _rutaMap.SetData(null);
            _rutaMap.EmptyText = Tr("Cargando el trazado de la ruta…");
            SetRutaMapMode(true);
            Task.Run(async () =>
            {
                RouteMapData d = null;
                try { d = BuildRouteMapData(dir, await RouteTrackAsync(dir)); } catch { d = null; }
                try
                {
                    if (!IsHandleCreated) return;
                    BeginInvoke((Action)(() =>
                    {
                        if (d == null) _rutaMapNone.Add(dir); else _rutaMapCache[dir] = d;
                        if (token != _rutaMapToken) return;
                        if (d == null) { SetRutaMapMode(false); return; }
                        _rutaMap.SetData(d);
                        UpdateRouteOverview();   // nº de estaciones
                        RutaLiveKick();
                    }));
                }
                catch { }
            });
        }

        static bool RutaMapPossible(string dir)
        {
            try { return Directory.Exists(dir) && Directory.GetFiles(dir, "*.tdb").Length > 0 && OrGeo.Available; }
            catch { return false; }
        }

        // Con trazado: el mapa en vivo; sin él, la imagen de la ruta y «Ver mapa de la ruta».
        void SetRutaMapMode(bool on)
        {
            if (_rutaMap == null) return;
            _rutaMapMode = on;
            _banner.Visible = !on;
            _rutaBtnHost.Visible = !on;
            _rutaMap.Visible = on;
            RutaLiveTimerUpdate();
            UpdateRouteOverview();
        }

        // ---------------- posiciones en vivo ----------------
        void RutaLiveTimerUpdate()
        {
            if (_rutaLiveTimer == null) return;
            bool want = _rutaMapMode && _activePage == 0;
            if (want && !_rutaLiveTimer.Enabled) { _rutaLiveTimer.Start(); RutaLiveKick(); }
            else if (!want && _rutaLiveTimer.Enabled) _rutaLiveTimer.Stop();
        }

        void RutaLiveKick() { if (_rutaMapMode && _activePage == 0) _ = RutaLiveTick(); }

        async Task RutaLiveTick()
        {
            if (_rutaLiveBusy || !_rutaMapMode || _rutaMap?.Data == null || _activePage != 0) return;
            if (WindowState == FormWindowState.Minimized || !Visible) return;
            string route = _rutaLiveRoute;
            if (string.IsNullOrWhiteSpace(route)) return;
            if (!Supa.IsConfigured || !Supa.IsLoggedIn)
            {
                _rutaMap.Live.Clear();
                _rutaMap.Status = Tr("Inicia sesión en Empresas para ver quién circula por esta ruta");
                return;
            }
            if (_rutaLiveNoServer) return;
            _rutaLiveBusy = true;
            try
            {
                var (json, err) = await Supa.RpcAsync("live_route_positions", new { p_route = route });
                if (route != _rutaLiveRoute) return;
                if (err != null)
                {
                    if (NoLeagueOnServer(err)) { _rutaLiveNoServer = true; _rutaMap.Live.Clear(); _rutaMap.Status = Tr("El servidor todavía no tiene el mapa en vivo"); }
                    else _rutaMap.Live.Expire();
                    return;
                }
                _rutaMap.Live.MeName = Tr("Tú");
                _rutaMap.Live.Update(ParseLive(json));
                int n = _rutaMap.Live.Count;
                _rutaMap.Status = n == 0 ? Tr("Nadie circula ahora por esta ruta")
                                : n == 1 ? Tr("1 maquinista en la ruta") : string.Format(Tr("{0} maquinistas en la ruta"), n);
                _rutaMap.Invalidate();
            }
            catch { _rutaMap.Live.Expire(); }
            finally { _rutaLiveBusy = false; }
        }

        // ---------------- trazado ----------------
        // La vía del HUD (detail: la del .tdb, en lat/lon con la conversión de OR) y las estaciones con su nombre bien
        // escrito. Sin el detalle, el trazado de antes. null si no hay trazado utilizable.
        static RouteMapData BuildRouteMapData(string routeDir, HudMapDetail detail)
        {
            bool hud = detail != null && detail.Track.Count > 0;
            ReadTdbWorld(routeDir, out var net, out var stWorld, withNet: !hud);   // con la vía del HUD, solo las estaciones
            var segLat = new List<double[]>(); var segLon = new List<double[]>();
            if (hud)
                foreach (var l in detail.Track) { if (l.Lat.Length >= 2) { segLat.Add(l.Lat); segLon.Add(l.Lon); } }
            else if (net != null)
                foreach (var poly in net)
                {
                    if (poly.Length < 2) continue;
                    var simp = Rdp(poly, 1.0);
                    var la = new double[simp.Count]; var lo = new double[simp.Count];
                    for (int i = 0; i < simp.Count; i++)
                        if (!OrGeo.TryLatLon(simp[i].X, simp[i].Y, out la[i], out lo[i])) return null;
                    segLat.Add(la); segLon.Add(lo);
                }
            if (segLat.Count == 0) return null;

            var good = TdbNames(routeDir);
            var groups = new Dictionary<string, (string name, double sLat, double sLon, int n, int w)>(StringComparer.OrdinalIgnoreCase);
            if (stWorld != null)
                foreach (var kv in stWorld)
                {
                    if (kv.Value.n <= 0) continue;
                    if (!OrGeo.TryLatLon(kv.Value.sx / kv.Value.n, kv.Value.sz / kv.Value.n, out double la, out double lo)) continue;
                    string name = CleanMapStation(FixTdbName(kv.Key.Trim(), good));
                    if (name.Length == 0) continue;
                    groups.TryGetValue(name, out var g0);
                    groups[name] = (g0.name ?? name, g0.sLat + la, g0.sLon + lo, g0.n + 1, g0.w + kv.Value.n);
                }
            var st = groups.Values.Select(g => (g.name, g.sLat / g.n, g.sLon / g.n, g.w)).ToList();   // w: andenes (las grandes, primero)
            return RouteMapData.Build(segLat, segLon, st);
        }

        // «Estación Sur Vía 2» → «Estación Sur» (los andenes de una estación se juntan en un solo nombre).
        static string CleanMapStation(string n)
        {
            n = Regex.Replace(n ?? "", @"\s*(v[íi]a|and[ée]n|platform|track|plataforma)\s*\d+\s*$", "", RegexOptions.IgnoreCase);
            return Regex.Replace(n, @"\s+\d+\s*$", "").Trim();
        }

        // Los .tdb en ANSI llegan con «�» en las tildes: se busca el nombre bien escrito leyendo el archivo.
        static HashSet<string> TdbNames(string routeDir)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                var tdb = Directory.GetFiles(routeDir, "*.tdb").FirstOrDefault();
                if (tdb == null) return set;
                var raw = File.ReadAllBytes(tdb);
                string text;
                if (raw.Length >= 2 && ((raw[0] == 0xFF && raw[1] == 0xFE) || (raw[0] == 0xFE && raw[1] == 0xFF))) text = Encoding.Unicode.GetString(raw);
                else
                {
                    try { text = new UTF8Encoding(false, true).GetString(raw); }
                    catch { text = Encoding.Latin1.GetString(raw); }
                }
                foreach (Match m in Regex.Matches(text, @"(?:Platform|Station|Siding)Name\s*\(\s*(?:""([^""]*)""|([^"")\s][^)]*?))\s*\)", RegexOptions.IgnoreCase))
                    set.Add((m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Trim());
            }
            catch { }
            return set;
        }

        static string FixTdbName(string n, HashSet<string> good)
        {
            if (n.IndexOf('�') < 0 || good.Count == 0) return n;
            var pat = new Regex("^" + string.Concat(n.Select(c => c == '�' ? "." : Regex.Escape(c.ToString()))) + "$");
            string hit = null; int count = 0;
            foreach (var g in good) if (pat.IsMatch(g)) { hit = g; if (++count > 1) break; }
            return count == 1 ? hit : n.Replace('�', '?');
        }

        // Douglas-Peucker en metros (coordenadas de mundo).
        static List<PointF> Rdp(PointF[] pts, double eps)
        {
            var keep = new bool[pts.Length]; keep[0] = keep[^1] = true;
            var stack = new Stack<(int, int)>(); stack.Push((0, pts.Length - 1));
            while (stack.Count > 0)
            {
                var (a, b) = stack.Pop();
                double best = 0; int bi = -1;
                double ax = pts[a].X, az = pts[a].Y, dx = pts[b].X - ax, dz = pts[b].Y - az, len2 = dx * dx + dz * dz;
                for (int i = a + 1; i < b; i++)
                {
                    double px = pts[i].X - ax, pz = pts[i].Y - az;
                    double t = len2 > 0 ? Math.Max(0, Math.Min(1, (px * dx + pz * dz) / len2)) : 0;
                    double ex = px - t * dx, ez = pz - t * dz, d = ex * ex + ez * ez;
                    if (d > best) { best = d; bi = i; }
                }
                if (bi >= 0 && best > eps * eps) { keep[bi] = true; stack.Push((a, bi)); stack.Push((bi, b)); }
            }
            var r = new List<PointF>();
            for (int i = 0; i < pts.Length; i++) if (keep[i]) r.Add(pts[i]);
            return r;
        }
    }
}
