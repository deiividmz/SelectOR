// Megafonía mientras se conduce: lo que oye el maquinista.
//  · Al arrancar la conducción se descarga de una vez lo que hace falta (pa_bundle) y se deja en
//    el caché local; si ya estaba, no se gasta ni una petición.
//  · El maquinista elige LÍNEA en el HUD: cada estación suena con la voz propia de esa línea y,
//    si no la tiene, con la base de la ruta. Sin línea elegida, siempre la base.
//  · El aviso se dispara desde el mismo sondeo que ya mueve a los viajeros (PollPax), sin
//    temporizadores nuevos, al acercarse a la estación (por metros o por segundos de antelación).
//  · Vale en servicio de empresa, en Horarios y en conducción libre. Fuera de un servicio, los
//    audios son los de la empresa que tengas seleccionada en Empresas.

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

namespace SelectOR
{
    public partial class MainMenuForm
    {
        string _paDriveCompany, _paDriveRoute, _paDriveLine;   // empresa, ruta y línea en uso
        bool _paDriveReady;                                    // hay megafonía que suene
        bool _paLineChosen;                                    // el maquinista ya ha elegido megafonía en el HUD
        readonly Dictionary<string, (int radius, int lead)> _paDriveSt =
            new Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase);
        // Aviso propio de una línea en una estación («línea|estación»): manda sobre el de la estación.
        readonly Dictionary<string, (int radius, int lead)> _paDriveLineCfg =
            new Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, (string path, string stamp)> _paDriveAudio =
            new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        readonly List<(string id, string name, List<string> stops)> _paDriveLines =
            new List<(string, string, List<string>)>();
        readonly HashSet<string> _paSaidNext = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // preaviso ya dado
        double _paPrevLat, _paPrevLon, _paSpeedMs; DateTime _paPrevUtc;
        bool _paSpeaking;
        // Origen del escenario: al entrar, el tren suele aparecer PARADO dentro de una estación, y
        // esa estación no se anuncia. Se reconoce por su ANDÉN MÁS CERCANO (cualquiera, esté delante
        // o detrás: en una estación grande los que quedan por delante pueden estar lejos) mientras
        // el tren no haya recorrido nada, y sigue en silencio hasta que el tren se aleja de ella.
        readonly HashSet<string> _paOrigin = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        double _paLastLat, _paLastLon, _paTravel; bool _paHaveLast;
        const double PaOriginM = 600;   // andén a esta distancia al empezar → estación de origen
        const double PaMovedM = 150;    // recorrido a partir del cual ya no se buscan orígenes
        const double PaJumpM = 500;     // más que esto entre dos sondeos (~1,5 s) es un salto, no marcha
        const double PaMaxSpeedMs = 100;   // 360 km/h: por encima, la lectura es un salto de posición
        double _paLat, _paLon;          // última posición conocida del tren
        // Cono de aproximación: la estación tiene que quedar por delante y poco desviada del rumbo.
        // Así no se cuela una estación de una vía PARALELA, que puede estar a cien metros al costado.
        const double PaSideBase = 150;  // desvío lateral admitido de cerca (m)
        const double PaSideFactor = 0.30;   // y cuánto se ensancha con la distancia
        // Nombres con los que Open Rails puede llamar a una estación → nombre con el que está
        // guardada. Hace falta porque el editor agrupa por PlatformItem.Station del .tdb y la API
        // del simulador reporta el nombre del ANDÉN, que a menudo es otro texto.
        Dictionary<string, string> _paAlias = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public bool PaOn => _prefs != null && _prefs.PaOn;

        // ---------------- registro de diagnóstico ----------------
        // Con la variable de entorno SELECTOR_PALOG=1, la megafonía deja en
        // %AppData%\Open Rails\SelectOR\megafonia.log una línea por decisión: qué estación ve,
        // con qué nombre la encuentra, a qué distancia salta y, si no suena, POR QUÉ no.
        static readonly bool PaLogOn = Environment.GetEnvironmentVariable("SELECTOR_PALOG") == "1";
        string _paLogLast;

        void PaLog(string texto, bool repetible = false)
        {
            if (!PaLogOn) return;
            if (!repetible && texto == _paLogLast) return;   // no repetir la misma línea cada sondeo
            _paLogLast = texto;
            try
            {
                string dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Open Rails", "SelectOR");
                System.IO.Directory.CreateDirectory(dir);
                System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "megafonia.log"),
                    DateTime.Now.ToString("HH:mm:ss") + "  " + texto + Environment.NewLine);
            }
            catch { }
        }

        // ---------------- arranque y parada ----------------

        // Se llama al empezar a conducir (servicio, horario o libre). No bloquea: si no hay
        // megafonía para esa empresa y ruta, simplemente no se activa nada.
        async void PaDriveStart(string route)
        {
            PaDriveStop();
            if (_prefs == null || string.IsNullOrWhiteSpace(route) || !Supa.IsLoggedIn) return;
            var company = _empOnDutyCompany ?? _empSel;     // en servicio, la del servicio; si no, la elegida
            if (company == null || !company.PaEnabled) return;

            _paDriveCompany = company.Id; _paDriveRoute = route;
            var (json, err) = await Supa.RpcAsync("pa_bundle", new { p_company = company.Id, p_route = route });
            if (err != null || string.IsNullOrWhiteSpace(json)) return;
            ParsePaDriveBundle(json);
            // Solo se considera «con megafonía» si hay algún aviso de estación grabado.
            _paDriveReady = false;
            foreach (var kv in _paDriveAudio)
                if (kv.Key.Contains("|nombre|")) { _paDriveReady = true; break; }
            if (!_paDriveReady) return;

            // Cada conducción empieza SIN megafonía elegida: el HUD muestra «Selecciona megafonía» y
            // no suena nada hasta que el maquinista elige una línea (o la voz base) en la lista.
            _paDriveLine = null;
            _paLineChosen = false;

            string dir = _curRoute?.Path;
            _paAlias = await Task.Run(() => PaAliasMap(dir));   // andén → estación, del .tdb

            int voces = 0;
            foreach (var kv in _paDriveAudio) if (kv.Key.Contains("|nombre|")) voces++;
            PaLog($"INICIO empresa={company.Name} ruta=[{route}] voces={voces} lineas={_paDriveLines.Count} "
                  + $"linea=[{PaHudLineName()}] alias={_paAlias.Count} tdb=[{dir}]", true);

            Audio.SetVolume(_prefs.PaVolume);
            _serviceHud?.NotifyPaChanged();   // la fila del HUD aparece y la ventana crece
            await PaPrecacheAsync();
        }

        // ¿Hay algún aviso base (sin línea) en esta ruta?
        bool PaHasBaseVoices()
        {
            foreach (var kv in _paDriveAudio)
            {
                var p = kv.Key.Split('|');
                if (p.Length == 3 && p[1] == "nombre" && p[2].Length == 0 && p[0] != "*") return true;
            }
            return false;
        }

        int PaLineVoices(string lineId)
        {
            int n = 0;
            foreach (var kv in _paDriveAudio)
            {
                var p = kv.Key.Split('|');
                if (p.Length == 3 && p[1] == "nombre" && string.Equals(p[2], lineId, StringComparison.OrdinalIgnoreCase)) n++;
            }
            return n;
        }

        void PaDriveStop()
        {
            try { Audio.Stop(); } catch { }
            _paDriveReady = false; _paDriveCompany = null; _paDriveRoute = null;
            _paDriveSt.Clear(); _paDriveAudio.Clear(); _paDriveLines.Clear();
            _paSaidNext.Clear(); _paSpeaking = false; _paAlias.Clear();
            _paSpeedMs = 0; _paPrevUtc = DateTime.MinValue;
            _paOrigin.Clear(); _paHaveLast = false; _paTravel = 0;
            _paLineChosen = false;
        }

        void ParsePaDriveBundle(string json)
        {
            _paDriveSt.Clear(); _paDriveAudio.Clear(); _paDriveLines.Clear(); _paDriveLineCfg.Clear();
            try
            {
                using var d = JsonDocument.Parse(json);
                var root = d.RootElement;
                if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0) root = root[0];

                if (root.TryGetProperty("stations", out var sts) && sts.ValueKind == JsonValueKind.Array)
                    foreach (var e in sts.EnumerateArray())
                    {
                        string norm = Str(e, "station"); if (norm.Length == 0) continue;
                        int radius = (int)Num(e, "radius_m"); if (radius <= 0) radius = 800;
                        _paDriveSt[norm] = (radius, (int)Num(e, "lead_s"));
                    }

                if (root.TryGetProperty("line_cfg", out var lcs) && lcs.ValueKind == JsonValueKind.Array)
                    foreach (var e in lcs.EnumerateArray())
                    {
                        string line = Str(e, "line"), st = Str(e, "station");
                        if (line.Length == 0 || st.Length == 0) continue;
                        int radius = (int)Num(e, "radius_m"); if (radius <= 0) radius = 800;
                        _paDriveLineCfg[line + "|" + st] = (radius, (int)Num(e, "lead_s"));
                    }

                if (root.TryGetProperty("lines", out var lns) && lns.ValueKind == JsonValueKind.Array)
                    foreach (var e in lns.EnumerateArray())
                    {
                        string id = Str(e, "id"); if (id.Length == 0) continue;
                        var stops = new List<string>();
                        if (e.TryGetProperty("stops", out var ss) && ss.ValueKind == JsonValueKind.Array)
                            foreach (var st in ss.EnumerateArray())
                                if (st.ValueKind == JsonValueKind.String) stops.Add(st.GetString() ?? "");
                        _paDriveLines.Add((id, Str(e, "name"), stops));
                    }

                if (root.TryGetProperty("audio", out var au) && au.ValueKind == JsonValueKind.Array)
                    foreach (var e in au.EnumerateArray())
                    {
                        string path = Str(e, "path"); if (path.Length == 0) continue;
                        _paDriveAudio[AudioKey(Str(e, "station"), Str(e, "kind"), Str(e, "line"))] =
                            (path, Str(e, "updated_at"));
                    }
            }
            catch { }
        }

        // Deja en el caché los avisos que se van a usar: los de la línea elegida y los base.
        // Son unos pocos KB cada uno, así en marcha no se descarga nada.
        async Task PaPrecacheAsync()
        {
            var pend = new List<(string path, string stamp)>();
            foreach (var kv in _paDriveAudio)
            {
                string[] p = kv.Key.Split('|');
                if (p.Length < 3) continue;
                string station = p[0], line = p[2];
                if (station != "*" && line.Length > 0 && line != _paDriveLine) continue;   // de otra línea: no hace falta
                pend.Add(kv.Value);
            }
            foreach (var (path, stamp) in pend)
            {
                try { await Megafonia.EnsureLocalAsync(path, stamp); } catch { }
            }
        }

        // Clave «suelta»: sin acentos, sin signos y sin espacios. Así «Estación Norte» y
        // «ESTACION-NORTE» son la misma, que es donde fallaban muchos emparejamientos.
        static string PaLoose(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var norm = s.Normalize(System.Text.NormalizationForm.FormD);
            var sb = new System.Text.StringBuilder(norm.Length);
            foreach (char c in norm)
            {
                if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        // Del .tdb: por cada andén, su nombre de estación Y su nombre de andén apuntan a la misma
        // estación. Con eso, llame Open Rails a la estación como la llame, se encuentra su aviso.
        static Dictionary<string, string> PaAliasMap(string routeDir)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (string.IsNullOrEmpty(routeDir) || !System.IO.Directory.Exists(routeDir)) return map;
                var tdb = System.IO.Directory.GetFiles(routeDir, "*.tdb");
                if (tdb.Length == 0) return map;
                var db = new Orts.Formats.Msts.TrackDatabaseFile(tdb[0]);
                if (db.TrackDB.TrItemTable == null) return map;
                foreach (var it in db.TrackDB.TrItemTable)
                {
                    if (!(it is Orts.Formats.Msts.PlatformItem pl) || string.IsNullOrWhiteSpace(pl.Station)) continue;
                    string estacion = NormStation(pl.Station);
                    if (estacion.Length == 0) continue;
                    foreach (string alias in new[] { pl.Station, pl.ItemName })
                    {
                        if (string.IsNullOrWhiteSpace(alias)) continue;
                        string k1 = PaLoose(NormStation(alias)), k2 = PaLoose(alias);
                        if (k1.Length > 0 && !map.ContainsKey(k1)) map[k1] = estacion;
                        if (k2.Length > 0 && !map.ContainsKey(k2)) map[k2] = estacion;
                    }
                }
            }
            catch { }
            return map;
        }

        // Nombre con el que está guardada esta estación, partiendo del que da Open Rails.
        string PaResolve(string norm)
        {
            if (string.IsNullOrEmpty(norm)) return norm;
            if (_paDriveSt.ContainsKey(norm) || PaClip(norm, "nombre").path != null) return norm;   // ya casa
            string loose = PaLoose(norm);
            if (_paAlias.TryGetValue(loose, out var canon)) return canon;
            // Último recurso: comparar sueltas con lo que hay guardado (acentos, guiones, etc.).
            foreach (var kv in _paDriveAudio)
            {
                var p = kv.Key.Split('|');
                if (p.Length != 3 || p[1] != "nombre" || p[0] == "*") continue;
                if (PaLoose(p[0]) == loose) return p[0];
            }
            return norm;
        }

        // ---------------- elección de línea (HUD) ----------------

        // Solo en trenes con plazas de viajeros: la megafonía se dispara desde el mismo sondeo que
        // mueve a los viajeros, así que en un mercancías o una locomotora sola no sonaría nada y la
        // fila del HUD no pinta nada.
        public bool PaAvailable => _paDriveReady && _paxCapacity > 0;

        public List<(string id, string name)> PaHudLines()
        {
            var l = new List<(string, string)>();
            foreach (var ln in _paDriveLines) l.Add((ln.id, ln.name));
            return l;
        }

        public string PaHudLineName()
        {
            if (!_paLineChosen) return I18n.T("Selecciona megafonía");
            foreach (var ln in _paDriveLines) if (ln.id == _paDriveLine) return ln.name;
            return I18n.T("sin línea");
        }

        public void PaHudPickLine(string id)
        {
            _paDriveLine = string.IsNullOrEmpty(id) ? null : id;
            _paLineChosen = true;
            if (_prefs != null && !string.IsNullOrEmpty(_paDriveRoute))
            {
                _prefs.PaLastLine ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                _prefs.PaLastLine[_paDriveRoute] = _paDriveLine ?? "";
                try { _prefs.Save(); } catch { }
            }
            _paSaidNext.Clear();
            _serviceHud?.NotifyPaChanged();
            _ = PaPrecacheAsync();
        }

        public void PaHudToggle()
        {
            if (_prefs == null) return;
            _prefs.PaOn = !_prefs.PaOn;
            try { _prefs.Save(); } catch { }
            if (!_prefs.PaOn) { try { Audio.Stop(); } catch { } _paSpeaking = false; }
            _serviceHud?.Invalidate();
        }

        // ---------------- disparadores ----------------

        // ¿Esta estación se anuncia con la línea elegida? Sin línea, todas; con línea, solo sus paradas.
        bool PaServes(string norm)
        {
            if (string.IsNullOrEmpty(_paDriveLine)) return true;
            foreach (var ln in _paDriveLines)
                if (ln.id == _paDriveLine)
                {
                    foreach (string st in ln.stops)
                        if (string.Equals(st, norm, StringComparison.OrdinalIgnoreCase)) return true;
                    return false;
                }
            return true;
        }

        (string path, string stamp) PaClip(string station, string kind)
        {
            if (!string.IsNullOrEmpty(_paDriveLine)
                && _paDriveAudio.TryGetValue(AudioKey(station, kind, _paDriveLine), out var a)) return a;
            return _paDriveAudio.TryGetValue(AudioKey(station, kind, null), out var b) ? b : (null, null);
        }

        // Velocidad estimada con las dos últimas posiciones (el sondeo va cada ~1,5 s): así no hay
        // que preguntarle a Open Rails y el preaviso puede darse «X segundos antes» y no solo a X metros.
        void PaUpdateSpeed(double lat, double lon)
        {
            _paLat = lat; _paLon = lon;
            // Recorrido real desde que empieza el escenario. Un salto de posición (Open Rails coloca
            // el tren al terminar de cargar, o se cambia de tren) no es marcha: se vuelve a empezar
            // desde el sitio nuevo, que es donde está de verdad la estación de origen.
            bool salto = false;
            if (_paHaveLast)
            {
                double d = Haversine(_paLastLat, _paLastLon, lat, lon);
                if (d > PaJumpM) { salto = true; _paTravel = 0; _paOrigin.Clear(); }
                else _paTravel += d;
            }
            _paLastLat = lat; _paLastLon = lon; _paHaveLast = true;
            PaTrackOrigin(lat, lon);

            var now = DateTime.UtcNow;
            if (salto) { _paSpeedMs = 0; _paPrevLat = lat; _paPrevLon = lon; _paPrevUtc = now; return; }
            if (_paPrevUtc != DateTime.MinValue)
            {
                double dt = (now - _paPrevUtc).TotalSeconds;
                if (dt > 0.3)
                {
                    double d = Haversine(_paPrevLat, _paPrevLon, lat, lon);
                    double v = d / dt;
                    if (v > PaMaxSpeedMs) v = 0;   // lectura imposible: no infla la antelación en segundos
                    _paSpeedMs = _paSpeedMs <= 0 ? v : _paSpeedMs * 0.6 + v * 0.4;   // suavizado
                    _paPrevLat = lat; _paPrevLon = lon; _paPrevUtc = now;
                }
            }
            else { _paPrevLat = lat; _paPrevLon = lon; _paPrevUtc = now; }
        }

        // Estación de origen: mientras el tren no haya recorrido nada, la del andén más cercano (a
        // PaOriginM o menos). Deja de serlo cuando el tren se ha alejado de todos sus andenes lo
        // mismo que usan los viajeros para volver a embarcar (1,5 km): si vuelves, suena normal.
        void PaTrackOrigin(double lat, double lon)
        {
            if (_paTravel < PaMovedM)
            {
                string cerca = null; double best = PaOriginM;
                foreach (var (station, sLat, sLon) in _paxStations)
                {
                    double d = Haversine(lat, lon, sLat, sLon);
                    if (d <= best) { best = d; cerca = NormStation(station); }
                }
                if (!string.IsNullOrEmpty(cerca) && _paOrigin.Add(cerca))
                    PaLog($"[{cerca}] andén a {best:F0} m al empezar · es la estación de ORIGEN: no se anuncia", true);
            }
            if (_paOrigin.Count == 0) return;
            foreach (var o in new List<string>(_paOrigin))
            {
                double min = double.MaxValue;
                foreach (var (station, sLat, sLon) in _paxStations)
                    if (string.Equals(NormStation(station), o, StringComparison.OrdinalIgnoreCase))
                        min = Math.Min(min, Haversine(lat, lon, sLat, sLon));
                if (min > PaxCfg.ReboardM) { _paOrigin.Remove(o); PaLog($"[{o}] ya a {min:F0} m: deja de ser la estación de origen", true); }
            }
        }

        // Aviso de la estación al entrar en su antelación (la mayor entre sus metros y sus segundos
        // a la velocidad actual). Una vez por visita: vuelve a poder sonar si el tren se aleja.
        // ¿Ese punto queda por delante y dentro del cono? Devuelve también las componentes, que
        // son las que explican por qué se descarta algo en el registro.
        bool PaAhead(double lat, double lon, out double along, out double lateral)
        {
            double vy = (lat - _paLat) * 111320.0;
            double vx = (lon - _paLon) * 111320.0 * Math.Cos(_paLat * Math.PI / 180.0);
            along = vx * _pDirX + vy * _pDirY;                      // por delante (+) o por detrás (−)
            lateral = Math.Abs(vx * -_pDirY + vy * _pDirX);         // desviación al costado
            return along > 0 && lateral <= PaSideBase + PaSideFactor * along;
        }

        // Barrido de TODOS los andenes de la ruta: se mide al ANDÉN más cercano de cada estación,
        // no al centro de la estación (que con andenes repartidos o vías en varias ramas cae lejos
        // de la vía por la que pasas), y se elige la mejor candidata de todas. Antes venía dada una
        // sola estación —la que elegía el modelo de viajeros— y si esa era mala, la buena ni se
        // llegaba a evaluar.
        async Task PaScan(double lat, double lon)
        {
            if (!_paDriveReady || !PaOn || _paSpeaking || !_paLineChosen) return;   // sin elegir megafonía, en silencio
            if (!_pHaveDir) return;                      // sin rumbo (parado) no se anuncia nada
            // Candidatas: el andén más cercano de cada estación que quede por delante y dentro del
            // cono. Se evalúan EN ORDEN de cercanía hasta que una suene: si la primera se descarta
            // —por no ser parada de la línea, por no tener audio o por estar ya dicha—, la siguiente
            // se mira en el mismo sondeo, sin esperar al próximo.
            var candidatas = new List<(string api, double dist, double along, double lateral)>();
            string cercaApi = null; double cercaDist = double.MaxValue, cercaAlong = 0, cercaLat = 0;
            foreach (var (station, sLat, sLon) in _paxStations)
            {
                double d = Haversine(lat, lon, sLat, sLon);
                if (d > 8000) continue;
                string api = NormStation(station);
                if (api.Length == 0) continue;
                if (PaAhead(sLat, sLon, out double along, out double lateral))
                {
                    int ya = candidatas.FindIndex(c => string.Equals(c.api, api, StringComparison.OrdinalIgnoreCase));
                    if (ya < 0) candidatas.Add((api, d, along, lateral));
                    else if (d < candidatas[ya].dist) candidatas[ya] = (api, d, along, lateral);   // andén más cercano
                }
                else if (d < cercaDist) { cercaDist = d; cercaApi = api; cercaAlong = along; cercaLat = lateral; }
            }
            if (candidatas.Count == 0)
            {
                if (cercaApi != null)
                    PaLog($"[{cercaApi}] {cercaDist:F0} m · descartada: por delante {cercaAlong:F0} m, al costado {cercaLat:F0} m");
                return;
            }
            candidatas.Sort((a, b) => a.dist.CompareTo(b.dist));
            foreach (var c in candidatas)
                if (await PaCheckNext(c.api, c.dist, c.along, c.lateral)) return;   // ha sonado
        }

        // Devuelve true si ha disparado el aviso: entonces no se miran más candidatas este sondeo.
        async Task<bool> PaCheckNext(string api, double distM, double along, double lateral)
        {
            if (!_paDriveReady || !PaOn || _paSpeaking || string.IsNullOrEmpty(api)) return false;
            // La estación en la que empieza el escenario NO se anuncia (ni al elegir la megafonía
            // con el tren parado en ella, ni al salir). Vuelve a poder sonar al alejarse 1,5 km.
            if (_paOrigin.Contains(api)) { PaLog($"[{api}] {distM:F0} m · es la estación de ORIGEN: no se anuncia"); return false; }
            string norm = PaResolve(api);           // el nombre que da OR puede no ser el guardado
            if (!string.Equals(api, norm, StringComparison.OrdinalIgnoreCase))
                PaLog($"[{api}] se reconoce como «{norm}» (alias del .tdb)");
            string donde = $"{distM:F0} m (por delante {along:F0}, al costado {lateral:F0})";
            if (_paSaidNext.Contains(norm)) { PaLog($"[{norm}] ya anunciada en esta visita"); return false; }
            if (!PaServes(norm)) { PaLog($"[{norm}] {donde} · no es parada de la línea [{PaHudLineName()}]"); return false; }

            int radius = 800, lead = 20;
            if (_paDriveSt.TryGetValue(norm, out var cfg)) { radius = cfg.radius; lead = cfg.lead; }
            // La línea elegida puede tener su propio aviso en esta estación.
            if (!string.IsNullOrEmpty(_paDriveLine) && _paDriveLineCfg.TryGetValue(_paDriveLine + "|" + norm, out var lcfg))
            { radius = lcfg.radius; lead = lcfg.lead; }
            double umbral = Math.Max(radius, lead * Math.Max(0, _paSpeedMs));
            if (distM > umbral)
            { PaLog($"[{norm}] {distM:F0} m (por delante {along:F0}, al costado {lateral:F0}) · aún lejos (umbral {umbral:F0} m = máx({radius} m, {lead} s × {_paSpeedMs * 3.6:F0} km/h))"); return false; }

            var clip = PaClip(norm, "nombre");
            _paSaidNext.Add(norm);                       // aunque no haya audio: no se reintenta cada sondeo
            if (clip.path == null)
            { PaLog($"[{norm}] {donde} · SIN AUDIO grabado para esta estación (ni de línea ni base)", true); return false; }
            var (file, err) = await Megafonia.EnsureLocalAsync(clip.path, clip.stamp);
            if (file == null) { PaLog($"[{norm}] no se pudo descargar el audio: {err}", true); return false; }
            PaLog($"[{norm}] {donde} · SUENA ({System.IO.Path.GetFileName(file)})", true);
            _paSpeaking = true;
            Audio.SetVolume(_prefs?.PaVolume ?? 90);
            Audio.Play(file, () => _paSpeaking = false);
            return true;
        }
    }
}
