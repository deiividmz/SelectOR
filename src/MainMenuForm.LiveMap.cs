// Mapa en vivo (mapa-en-vivo.sql): mientras conduces (de servicio o en conducción libre), cada 5 s se
// envía tu posición en la ruta y, en la misma llamada, se reciben los demás usuarios de la comunidad
// SelectOR que conducen en la misma ruta; el minimapa y el mapa grande los dibujan (LiveMap.cs).
// Se desactiva en Empresas → Mi perfil («Compartir mi posición en el mapa»): entonces ni se envía
// la posición ni se ve a los demás.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SelectOR
{
    public partial class MainMenuForm
    {
        readonly LiveMates _liveMates = new();
        Timer _liveTimer;
        bool _liveBusy, _liveSent, _liveNoServer;   // _liveNoServer: falta mapa-en-vivo.sql en el servidor
        string _liveRoute, _liveTrain;
        double _liveLastM; DateTime _liveLastUtc;
        const int LiveIntervalMs = 5000;
        Control _liveShareBox; ThemeCheck _liveShareChk;

        bool LiveWanted => _prefs != null && _prefs.ShareLivePosition && Supa.IsConfigured && Supa.IsLoggedIn && !_liveNoServer;

        // Al empezar a conducir (StartKmTracking) o al volver a activar la casilla con OR abierto.
        void StartLiveMap()
        {
            StopLiveTimer();
            _liveMates.Reset();
            if (!LiveWanted || _kmTimer == null) return;
            _liveRoute = _curRoute?.Name ?? "";
            _liveTrain = _drivenConsist?.Name;
            if (string.IsNullOrWhiteSpace(_liveTrain)) _liveTrain = CurrentConsistLabel();
            if (string.IsNullOrWhiteSpace(_liveRoute)) return;
            _liveLastM = _trackedMeters; _liveLastUtc = DateTime.UtcNow;
            _liveTimer = new Timer { Interval = LiveIntervalMs };
            _liveTimer.Tick += async (s, e) => await LiveTick();
            _liveTimer.Start();
        }

        // Al terminar de conducir o al desactivar la casilla: desapareces del mapa de los demás.
        void StopLiveMap()
        {
            StopLiveTimer();
            _liveMates.Reset();
            if (_liveSent) { _liveSent = false; _ = Supa.RpcAsync("live_stop", new { }); }
        }

        void StopLiveTimer()
        {
            try { _liveTimer?.Stop(); _liveTimer?.Dispose(); } catch { }
            _liveTimer = null;
        }

        async Task LiveTick()
        {
            if (_liveBusy || _liveTimer == null || !_scenarioReady || !_tHave) return;
            _liveBusy = true;
            try
            {
                // Rumbo: entre los dos últimos puntos del rastro. Velocidad: metros medidos desde el envío anterior.
                double? hdg = null;
                var pts = _driveTrail.Points;
                if (pts.Count >= 2) { var a = pts[^2]; var b = pts[^1]; hdg = Math.Round(LiveMates.Bearing(a.lat, a.lon, b.lat, b.lon), 1); }
                double secs = Math.Max(0.5, (DateTime.UtcNow - _liveLastUtc).TotalSeconds);
                double kmh = TrainStoppedByPosition(2) ? 0 : Math.Max(0, (_trackedMeters - _liveLastM) / secs * 3.6);
                _liveLastM = _trackedMeters; _liveLastUtc = DateTime.UtcNow;
                string company = _pendingServiceId != null ? _empOnDutyCompany?.Id : null;   // de servicio → su empresa

                var (json, err) = await Supa.RpcAsync("live_update", new
                {
                    p_route = _liveRoute, p_lat = _tLat, p_lon = _tLon, p_heading = hdg,
                    p_speed = Math.Round(kmh, 1), p_train = _liveTrain, p_company = company
                });
                if (_liveTimer == null)
                {
                    // La conducción terminó mientras tanto: que no quede la posición recién enviada.
                    if (err == null) _ = Supa.RpcAsync("live_stop", new { });
                    return;
                }
                if (err != null)
                {
                    if (NoLeagueOnServer(err)) { _liveNoServer = true; StopLiveTimer(); _liveMates.Reset(); }   // sin el SQL: nada
                    else _liveMates.Expire();
                    return;
                }
                _liveSent = true;
                _liveMates.Update(ParseLive(json));
            }
            catch { _liveMates.Expire(); }
            finally { _liveBusy = false; }
        }

        static List<LiveRow> ParseLive(string json)
        {
            var list = new List<LiveRow>();
            if (string.IsNullOrWhiteSpace(json)) return list;
            using var d = JsonDocument.Parse(json);
            if (d.RootElement.ValueKind != JsonValueKind.Array) return list;
            static string S(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            static double? N(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : (double?)null;
            foreach (var e in d.RootElement.EnumerateArray())
            {
                var lat = N(e, "lat"); var lon = N(e, "lon");
                if (lat == null || lon == null) continue;
                list.Add(new LiveRow
                {
                    Id = S(e, "user_id"), Name = S(e, "username"), Train = S(e, "train"), Company = S(e, "company"),
                    Lat = lat.Value, Lon = lon.Value, Heading = N(e, "heading"), SpeedKmh = N(e, "speed_kmh") ?? 0
                });
            }
            return list;
        }

        // Para el HUD y el mapa grande: los demás usuarios (null si no hay nadie) y quién soy yo.
        List<LiveMarker> LiveMarkers() => _liveMates.Count > 0 ? _liveMates.Current() : null;
        (string name, string company) LiveMe() => (Supa.Username, _pendingServiceId != null ? _empOnDutyCompany?.Name : null);

        // Casilla de Mi perfil (a la derecha del título de la sección).
        Control BuildLiveShareBox()
        {
            string t1 = Tr("Compartir mi posición en el mapa");
            string t2 = Tr("Te ven en el mapa los usuarios de la comunidad SelectOR que conduzcan en tu misma ruta.");
            var fChk = Theme.Font(9.5f, FontStyle.Bold);
            var fSub = Theme.Font(8f);
            int w = Math.Max(TextRenderer.MeasureText(t1, fChk).Width + 30, TextRenderer.MeasureText(t2, fSub).Width) + 70;
            var wrap = new Panel { Dock = DockStyle.Right, Width = w, BackColor = Theme.Bg, Padding = new Padding(0, 0, 14, 0), Visible = false };
            var card = new Card { Dock = DockStyle.Fill, Fill = Theme.Surface, BackColor = Theme.Surface, BorderColor = Theme.Border, Radius = 8 };
            _liveShareChk = new ThemeCheck
            {
                Text = t1, Font = fChk, ForeColor = Theme.Text, Height = 20, Width = w - 34, Location = new Point(10, 2),
                Checked = _prefs?.ShareLivePosition ?? true
            };
            var sub = new Label { Text = t2, AutoSize = true, Font = fSub, ForeColor = Theme.Subtle, BackColor = Theme.Surface, Location = new Point(36, 21), UseMnemonic = false };
            _liveShareChk.CheckedChanged += (s, e) =>
            {
                if (_prefs == null) return;
                _prefs.ShareLivePosition = _liveShareChk.Checked;
                try { _prefs.Save(); } catch { }
                if (!_liveShareChk.Checked) StopLiveMap();
                else if (_kmTimer != null) StartLiveMap();   // conduciendo ahora mismo: empieza ya
            };
            card.Controls.Add(_liveShareChk); card.Controls.Add(sub);
            wrap.Controls.Add(card);
            return wrap;
        }
    }
}
