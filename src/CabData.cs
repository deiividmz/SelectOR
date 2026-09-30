// Datos del pupitre (indicadores del tren) leídos del servidor web de Open Rails.
// Réplica en C# de la lógica de Cockpit-SF (or_api_client.py):
//  · /API/CABCONTROLS: cada mando trae TypeName, MinValue, MaxValue y RangeFraction (0..1).
//    El valor real es min + fracción × (max − min). Es la fuente PRINCIPAL porque no depende
//    del idioma del simulador.
//  · /API/TRACKMONITORDISPLAY: las filas del Track Monitor de OR; de ahí sale «Limit», la
//    velocidad permitida que marca el triángulo amarillo del velocímetro.
//  · /API/HUD/1, 2, 5 y 7: las páginas del HUD de OR como tablas etiqueta/valor (en inglés).
//    Dan lo que los mandos de cabina no siempre traen: velocidad, presiones del freno de tren,
//    potencia, esfuerzo, depósito principal, límite de vía…
//  · PRESIONES: el HUD de OR las da CON SU UNIDAD (bar, psi, kPa, inHg… según las opciones del
//    simulador y la máquina) y aquí se pasan todas a bar. Los mandos de cabina (MAIN_RES,
//    BRAKE_PIPE, BRAKE_CYL…) vienen en la unidad que diga el .cvf de esa cabina y SIN decirla:
//    solo se usan si el HUD no da el dato, deduciendo la unidad por el fondo de escala del
//    manómetro. Antes mandaban siempre, y en cabinas en psi o kPa las agujas se salían.
//  · Suavizado idéntico al de Cockpit-SF: lectura cada 100 ms y media exponencial con α = 0,30
//    en los valores continuos (el pupitre añade luego la animación de las agujas, como Cockpit).
// Cockpit pedía 7 recursos cada 100 ms (unas 70 peticiones por segundo); aquí la página 1 y los
// mandos se piden cada 100 ms y las demás páginas cada segundo, para quitarle carga al simulador.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SelectOR
{
    public sealed class CabValues
    {
        public bool Connected;
        public double SpeedKmh, LimitKmh, Gradient, OdoKm;
        public double SpeedoMax;   // fondo de escala del velocímetro de la cabina (.cvf, ScaleRange); 0 = sin dato
        public bool SpeedFromCab, LimitFromCab;   // leídos del mando de la cabina: en SUS unidades (km/h o mph)
        public double Throttle, TrainBrake, EngineBrake, DynBrake;       // %
        public double MrBar, BpBar, EqBar, BcBar;                         // bar
        public bool Compressor;
        public double LineKv, Ammeter, PowerKw, EffortKn, Rpm, Fuel;
        public double Reverser;                                           // −1 … +1
        public char Direction = 'N';                                      // F / N / R
        public bool Panto, Breaker, Battery, MasterKey, Horn, Bell, Sand, Wipers,
                    Alerter, Wheelslip, Emergency, Headlight, Overspeed,
                    DoorLeft, DoorRight;
        public int HeadlightLevel;                                        // 0 apagados, 1 cortos, 2 largos
        public bool[] PantoCmd;                                           // por pantógrafo: mandado subir (Up o Raising)
        public long StampTicks;                                           // cuándo respondió OR (Stopwatch.GetTimestamp)
        public double SpeedTarget;                                        // velocidad objetivo del regulador (km/h)
        public string CruiseStatus = "";                                  // «Auto», «Manual»… (HUD de OR)
        // Límites de velocidad POR DELANTE del tren según el Track Monitor: (distancia m, km/h), de
        // cerca a lejos. RowStepM = metros que representa cada fila del gráfico (su resolución).
        public List<(double distM, double limitKmh)> LimitsAhead;
        public double RowStepM;
        public string Time = "", Mode = "";

        // Qué datos ha dado realmente el simulador para esta locomotora.
        public readonly HashSet<string> Av = new HashSet<string>(StringComparer.Ordinal);
        public bool Has(string k) => Av.Contains(k);
    }

    public sealed class CabPoller : IDisposable
    {
        readonly HttpClient _http;
        readonly Action<CabValues> _onData;
        CancellationTokenSource _cts;
        int _tick;
        // Páginas lentas: se refrescan cada segundo y se reutiliza lo último leído.
        List<(string label, string value)> _p2 = new(), _p5 = new(), _p7 = new();
        // Diagnóstico (solo con SELECTOR_CABLOG=1): cada 3 s se guarda en %AppData%\Open Rails\SelectOR        // pupitre-diagnostico.json lo que envía OR tal cual y lo que el pupitre ha entendido, para revisar
        // trenes que se vean mal (se sobrescribe: vale la última lectura). Sin la variable no se escribe nada.
        string _raw5, _rawTm; long _diagAt = -100000;
        public string DiagTrain = "";
        readonly Dictionary<string, double> _ema = new(StringComparer.Ordinal);
        double _tmLimit = -1;   // «Limit» del Track Monitor (km/h); −1 = no lo da
        List<(double distM, double limitKmh)> _tmAhead; double _tmStep;
        const double Alpha = 0.30;   // el de Cockpit-SF, con muestras cada 100 ms

        public CabPoller(int port, Action<CabValues> onData)
        {
            _onData = onData;
            _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromMilliseconds(1200) };
        }

        public void Start()
        {
            Stop();
            _cts = new CancellationTokenSource();
            var tok = _cts.Token;
            Task.Run(async () =>
            {
                while (!tok.IsCancellationRequested)
                {
                    // Con una orden en marcha no se pregunta nada al simulador (ver OrControl.Quiet).
                    if (OrControl.Quiet) { try { await Task.Delay(50, tok); } catch { break; } continue; }
                    CabValues v;
                    try { v = await Read(); } catch { v = new CabValues(); }
                    try { _onData?.Invoke(v); } catch { }
                    try { await Task.Delay(100, tok); } catch { break; }
                }
            }, tok);
        }

        public void Stop()
        {
            try { _cts?.Cancel(); } catch { }
            _cts = null;
        }

        public void Dispose() { Stop(); try { _http.Dispose(); } catch { } }

        async Task<string> Get(string path)
        {
            try { return await _http.GetStringAsync(path); } catch { return null; }
        }

        async Task<CabValues> Read()
        {
            var v = new CabValues();
            bool lentas = (_tick % 10) == 0;     // páginas 2 y 7: una vez por segundo
            bool frenos = (_tick++ % 3) == 0;     // página 5 (presiones de freno): cada 300 ms
            // Las peticiones van DE UNA EN UNA (antes salían hasta 6 a la vez): el servidor web de OR
            // las atiende en hilos aparte mientras el simulador cambia el estado del tren, y varias
            // lecturas simultáneas multiplican las opciones de pillarlo a medio cambiar.
            string j1 = await Get("/API/HUD/1");
            v.StampTicks = System.Diagnostics.Stopwatch.GetTimestamp();   // la velocidad sale de aquí
            string jc = await Get("/API/CABCONTROLS");
            if ((_tick % 2) == 0) { string jt = await Get("/API/TRACKMONITORDISPLAY"); if (jt != null) { _tmLimit = TrackMonitorLimit(jt); _rawTm = jt; (_tmAhead, _tmStep) = TrackMonitorAhead(jt); } }   // cada 200 ms
            if (lentas)
            {
                string j2 = await Get("/API/HUD/2");
                if (j2 != null) _p2 = ParseHudPage(j2);
                string j7 = await Get("/API/HUD/7");
                if (j7 != null) _p7 = ParseHudPage(j7);
            }
            if (frenos)
            {
                string j5 = await Get("/API/HUD/5");
                if (j5 != null) { _p5 = ParseHudPage(j5); _raw5 = j5; }
            }

            v.Connected = j1 != null || jc != null;
            if (!v.Connected) { _ema.Clear(); return v; }

            if (j1 != null) FromPage1(v, ParseHudPage(j1));
            FromPage2(v, _p2);
            FromPage5(v, _p5);
            FromPage7(v, _p7);
            if (jc != null) FromCabControls(v, jc);
            if (_tmLimit >= 0) { v.LimitKmh = _tmLimit; v.Av.Add("limit"); v.LimitFromCab = false; }   // manda el Track Monitor
            else if (_tmLimit == -2) { v.LimitKmh = 0; v.Av.Remove("limit"); }
            v.LimitsAhead = _tmAhead; v.RowStepM = _tmStep;

            // Solo dirección: el inversor se deduce de ella.
            if (v.Has("dir") && !v.Has("rev"))
            { v.Reverser = v.Direction == 'F' ? 1 : v.Direction == 'R' ? -1 : 0; v.Av.Add("rev"); }

            if (DiagOn && j1 != null && jc != null && _raw5 != null && Environment.TickCount64 - _diagAt > 3000)
            {
                _diagAt = Environment.TickCount64;
                SaveDiag(j1, _raw5, jc, v);
            }

            Smooth(v);
            return v;
        }

        static readonly bool DiagOn = AppDataTidy.Flag("SELECTOR_CABLOG");

        void SaveDiag(string hud1, string hud5, string cab, CabValues v)
        {
            try
            {
                string dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Open Rails", "SelectOR");
                System.IO.Directory.CreateDirectory(dir);
                var leido = new
                {
                    v.MrBar, v.BpBar, v.EqBar, v.BcBar, v.SpeedKmh, v.LimitKmh,
                    v.Panto, v.Breaker, v.HeadlightLevel, v.Sand, v.Wipers, v.Bell, v.Horn, v.DoorLeft, v.DoorRight,
                    Datos = string.Join(",", v.Av),
                };
                string txt = "{\n\"tren\": " + JsonSerializer.Serialize(DiagTrain ?? "") + ",\n\"fecha\": " + JsonSerializer.Serialize(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))
                             + ",\n\"entendido\": " + JsonSerializer.Serialize(leido)
                             + ",\n\"hud1\": " + hud1 + ",\n\"hud5\": " + hud5 + ",\n\"cabcontrols\": " + cab
                             + ",\n\"trackmonitor\": " + (string.IsNullOrWhiteSpace(_rawTm) ? "null" : _rawTm) + "\n}";
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "pupitre-diagnostico.json"), txt);
            }
            catch { }
        }

        // ---------------- presiones: todo a bar ----------------
        // Número y unidad tal como los escribe OR («5,0 bar», «72 psi», «500 kPa», «21 inHg»).
        static double ToBar(double x, string text)
        {
            string u = (text ?? "").ToLowerInvariant();
            if (u.Contains("psi")) return x * 0.0689476;
            if (u.Contains("kpa")) return x / 100.0;
            if (u.Contains("inhg")) return x * 0.0338639;
            if (u.Contains("kgf")) return x * 0.980665;
            if (u.Contains("mmhg")) return x * 0.00133322;
            return x;   // bar (o sin unidad)
        }

        // Mandos de cabina: OR da el valor en la unidad del .cvf pero no dice cuál. Se deduce por el
        // fondo de escala del manómetro: hasta 20 → bar (o kgf/cm²); hasta 40 → inHg (vacío);
        // hasta 300 → psi; más → kPa.
        static double CabBar(double real, double max)
        {
            max = Math.Abs(max);
            if (max <= 20) return real;
            if (max <= 40) return real * 0.0338639;
            if (max <= 300) return real * 0.0689476;
            return real / 100.0;
        }

        // ---------------- suavizado ----------------
        void Smooth(CabValues v)
        {
            double S(string k, double x)
            {
                if (!v.Has(k)) { _ema.Remove(k); return x; }
                if (!_ema.TryGetValue(k, out var prev)) { _ema[k] = x; return x; }
                double n = prev + Alpha * (x - prev);
                _ema[k] = n; return n;
            }
            // (la velocidad NO: el pupitre la sigue con su propia estimación de la tendencia)
            v.MrBar = S("mr", v.MrBar); v.BpBar = S("bp", v.BpBar); v.EqBar = S("eq", v.EqBar); v.BcBar = S("bc", v.BcBar);
            v.Ammeter = S("amp", v.Ammeter); v.LineKv = S("linev", v.LineKv);
            v.PowerKw = S("power", v.PowerKw); v.Rpm = S("rpm", v.Rpm); v.Fuel = S("fuel", v.Fuel);
            v.EffortKn = S("effort", v.EffortKn);
            v.Throttle = S("throttle", v.Throttle); v.TrainBrake = S("tbrake", v.TrainBrake);
            v.EngineBrake = S("ebrake", v.EngineBrake); v.DynBrake = S("dyn", v.DynBrake);
        }

        // ---------------- utilidades ----------------
        // Primer número de un texto tipo «45,2 km/h», «9,0 bar» o «-3 %» (coma o punto decimal).
        static double Num(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return 0;
            var m = Regex.Match(s.Replace(",", "."), @"-?\d+(?:\.\d+)?");
            return m.Success && double.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
        }

        // Tabla de OR: array plano de celdas por filas. Con ≤3 columnas es etiqueta/valor; con más,
        // es una hoja con título de sección, fila de cabecera y filas de datos → «SECCIÓN.Cabecera».
        static List<(string label, string value)> ParseTable(JsonElement t)
        {
            var rows = new List<(string, string)>();
            if (t.ValueKind != JsonValueKind.Object) return rows;
            int nr = t.TryGetProperty("nRows", out var a) && a.TryGetInt32(out var ai) ? ai : 0;
            int nc = t.TryGetProperty("nCols", out var b) && b.TryGetInt32(out var bi) ? bi : 0;
            if (nr <= 0 || nc <= 0 || !t.TryGetProperty("values", out var vals) || vals.ValueKind != JsonValueKind.Array) return rows;
            var cells = new List<string>();
            foreach (var c in vals.EnumerateArray()) cells.Add(c.ValueKind == JsonValueKind.String ? (c.GetString() ?? "").Trim() : "");

            if (nc <= 3)
            {
                for (int r = 0; r < nr; r++)
                {
                    string label = null; var parts = new List<string>();
                    for (int k = 0; k < nc; k++)
                    {
                        int i = r * nc + k; if (i >= cells.Count) break;
                        string s = cells[i]; if (s.Length == 0) continue;
                        if (label == null) label = s; else parts.Add(s);
                    }
                    if (label != null) rows.Add((label, string.Join(" ", parts)));
                }
                return rows;
            }

            string section = ""; string[] header = null;
            for (int r = 0; r < nr; r++)
            {
                var row = new string[nc];
                var llenas = new List<string>();
                for (int k = 0; k < nc; k++)
                {
                    int i = r * nc + k;
                    row[k] = i < cells.Count ? cells[i] : "";
                    if (row[k].Length > 0) llenas.Add(row[k]);
                }
                if (llenas.Count == 0) continue;
                if (llenas.Count == 1)
                {
                    string solo = llenas[0];
                    if (solo == solo.ToUpperInvariant() || solo.ToUpperInvariant().Contains("INFORMATION") || solo.Contains(":"))
                    { section = solo; header = null; }
                    continue;
                }
                bool esCabecera = header == null && llenas.Count >= 2
                                  && llenas.TrueForAll(s => s.Length <= 18 && s.Split(' ').Length <= 2)
                                  && !llenas.Exists(s => Regex.IsMatch(s, @"\d"));
                if (esCabecera) { header = row; continue; }
                if (header != null)
                {
                    string pref = section.Length > 0 ? section.Split(' ')[0].TrimEnd(':').ToUpperInvariant() + "." : "";
                    for (int k = 0; k < nc; k++)
                        if (row[k].Length > 0 && k < header.Length && header[k].Length > 0)
                            rows.Add((pref + header[k], row[k]));
                }
                else
                {
                    // «PlayerLoco | Main reservoir | 10,0 bar | Compressor | off | …»: el primero es
                    // de quién es la fila y detrás van parejas etiqueta/valor.
                    if (llenas.Count % 2 == 1 && llenas[0].Equals("PlayerLoco", StringComparison.OrdinalIgnoreCase))
                        for (int k = 1; k + 1 < llenas.Count; k += 2) rows.Add(("PLAYERLOCO." + llenas[k], llenas[k + 1]));
                    else
                        for (int k = 0; k + 1 < llenas.Count; k += 2) rows.Add((llenas[k], llenas[k + 1]));
                }
            }
            return rows;
        }

        static List<(string label, string value)> ParseHudPage(string json)
        {
            var rows = new List<(string, string)>();
            try
            {
                using var d = JsonDocument.Parse(json);
                if (d.RootElement.TryGetProperty("commonTable", out var c)) rows.AddRange(ParseTable(c));
                if (d.RootElement.TryGetProperty("extraTable", out var e)) rows.AddRange(ParseTable(e));
            }
            catch { }
            return rows;
        }

        // ---------------- Track Monitor: «Limit» ----------------
        // Lista de filas {FirstCol, TrackColLeft, TrackCol, TrackColRight, LimitCol, SignalCol,
        // DistCol}; cada celda puede acabar en un código de color de 3 signos (como «???» o «%$$»).
        // Devuelve el límite en km/h, −2 si la fila existe sin valor y −1 si no hay tal fila.
        static readonly string[] CodigosColor = { "???", "??!", "?!?", "?!!", "!??", "!!?", "!!!", "%%%", "%$$", "%%$", "$%$", "$$$" };

        static string SinColor(string s)
        {
            s = (s ?? "").Trim();
            if (s.Length >= 3 && Array.IndexOf(CodigosColor, s.Substring(s.Length - 3)) >= 0) s = s.Substring(0, s.Length - 3);
            return s.Trim();
        }

        static double TrackMonitorLimit(string json)
        {
            try
            {
                using var d = JsonDocument.Parse(json);
                var arr = d.RootElement;
                if (arr.ValueKind != JsonValueKind.Array) return -1;
                foreach (var row in arr.EnumerateArray())
                {
                    if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("FirstCol", out var fc)) continue;
                    string nombre = SinColor(fc.GetString()).TrimEnd(':').ToLowerInvariant();
                    if (nombre != "limit" && nombre != "límite" && nombre != "limite") continue;
                    foreach (var cell in row.EnumerateObject())
                    {
                        if (cell.Name == "FirstCol" || cell.Value.ValueKind != JsonValueKind.String) continue;
                        string val = SinColor(cell.Value.GetString());
                        if (!Regex.IsMatch(val, @"\d")) continue;
                        double x = Num(val);
                        return val.ToLowerInvariant().Contains("mph") ? x * 1.60934 : x;
                    }
                    return -2;
                }
            }
            catch { }
            return -1;
        }

        // ---------------- Track Monitor: límites por delante ----------------
        // El Track Monitor de OR es un GRÁFICO en filas: bajo la cabecera «Milepost | Limit | Dist»
        // están, de lejos a cerca, las filas por delante del tren; luego la fila del propio tren
        // («⧯», con «▲»/«▼» según el sentido) y después lo que queda detrás. Cada fila lleva en
        // LimitCol el límite de ese tramo, y algunas llevan en DistCol una MARCA de distancia
        // («1,5 km», con espacio; sin espacio, «0,4km», es la distancia a un objeto concreto y no
        // sirve de escala). Con las marcas se saca la distancia de cada fila (escala lineal).
        static readonly Regex MarcaDist = new(@"^\s*(\d+(?:[.,]\d+)?)\s+(km|m|mi|yd|ft)\s*$", RegexOptions.IgnoreCase);

        public static (List<(double distM, double limitKmh)> ahead, double step) TrackMonitorAhead(string json)
        {
            try
            {
                using var d = JsonDocument.Parse(json);
                if (d.RootElement.ValueKind != JsonValueKind.Array) return (null, 0);
                var rows = new List<(string first, string left, string track, string limit, string dist)>();
                foreach (var r in d.RootElement.EnumerateArray())
                {
                    string Raw(string k) => r.TryGetProperty(k, out var e) && e.ValueKind == JsonValueKind.String ? (e.GetString() ?? "") : "";
                    string C(string k) => SinColor(Raw(k));
                    // Límites en AMARILLO (código «???» del Track Monitor): no cuentan para la curva.
                    string lim = Raw("LimitCol").TrimEnd().EndsWith("???") ? "" : C("LimitCol");
                    rows.Add((C("FirstCol"), C("TrackColLeft"), C("TrackCol"), lim, C("DistCol")));
                }
                int start = rows.FindIndex(x => x.first.Equals("Milepost", StringComparison.OrdinalIgnoreCase));
                if (start < 0) return (null, 0);
                int tren = -1;
                for (int i = start + 1; i < rows.Count; i++) if (rows[i].track.Contains("⧯")) { tren = i; break; }
                if (tren < 0) return (null, 0);
                bool haciaAbajo = rows[tren].left.Contains("▼");

                // Filas por delante, de CERCA a LEJOS (sin separadores).
                var delante = new List<(string limit, string dist)>();
                if (!haciaAbajo) { for (int i = tren - 1; i > start; i--) if (!rows[i].first.StartsWith("Sprtr")) delante.Add((rows[i].limit, rows[i].dist)); }
                else { for (int i = tren + 1; i < rows.Count; i++) if (!rows[i].first.StartsWith("Sprtr")) delante.Add((rows[i].limit, rows[i].dist)); }
                if (delante.Count == 0) return (null, 0);

                // Escala: marcas (posición 1..n, metros) → recta d = a + b·pos por mínimos cuadrados.
                var px = new List<double>(); var py = new List<double>();
                for (int k = 0; k < delante.Count; k++)
                {
                    var m = MarcaDist.Match(delante[k].dist);
                    if (!m.Success) continue;
                    double x = Num(m.Groups[1].Value);
                    string u = m.Groups[2].Value.ToLowerInvariant();
                    double metros = u == "km" ? x * 1000 : u == "mi" ? x * 1609.344 : u == "yd" ? x * 0.9144 : u == "ft" ? x * 0.3048 : x;
                    px.Add(k + 1); py.Add(metros);
                }
                double a, b;
                if (px.Count >= 2)
                {
                    double mx = 0, my = 0; for (int i = 0; i < px.Count; i++) { mx += px[i]; my += py[i]; }
                    mx /= px.Count; my /= px.Count;
                    double sxx = 0, sxy = 0; for (int i = 0; i < px.Count; i++) { sxx += (px[i] - mx) * (px[i] - mx); sxy += (px[i] - mx) * (py[i] - my); }
                    if (sxx <= 0) return (null, 0);
                    b = sxy / sxx; a = my - b * mx;
                }
                else if (px.Count == 1) { a = 0; b = py[0] / px[0]; }
                else return (null, 0);
                if (b <= 0) return (null, 0);

                var lista = new List<(double, double)>();
                for (int k = 0; k < delante.Count; k++)
                {
                    if (!Regex.IsMatch(delante[k].limit, @"^\s*\d+(?:[.,]\d+)?\s*$")) continue;
                    double lim = Num(delante[k].limit);
                    if (lim > 0) lista.Add((Math.Max(0, a + b * (k + 1)), lim));
                }
                return (lista, b);
            }
            catch { return (null, 0); }
        }

        // ---------------- HUD página 1: datos básicos ----------------
        static void FromPage1(CabValues v, List<(string label, string value)> rows)
        {
            foreach (var (label0, val) in rows)
            {
                string label = label0.ToLowerInvariant(); if (val.Length == 0) continue;
                if (label == "time" && val.Contains(":")) { v.Time = val; v.Av.Add("time"); }
                else if (label == "speed" && !v.Has("speed"))
                {
                    double x = Num(val);
                    v.SpeedKmh = val.ToLowerInvariant().Contains("mph") ? x * 1.60934 : x;
                    v.Av.Add("speed");
                }
                else if (label == "gradient" || label == "grade") { v.Gradient = Num(val); v.Av.Add("grad"); }
                else if (label == "direction")
                {
                    string l = val.ToLowerInvariant();
                    v.Direction = l.Contains("forward") ? 'F' : l.Contains("reverse") ? 'R' : 'N';
                    v.Av.Add("dir");
                }
                else if (label == "throttle") { v.Throttle = Num(val); v.Av.Add("throttle"); }
                else if (label.Contains("train brake"))
                {
                    // «Service 20% EQ 6,6 bar BC 2,6 bar BP 6,6 bar Flow 0 L/s»
                    string s = val.Replace(",", ".");
                    var mp = Regex.Match(s, @"(\d+(?:\.\d+)?)\s*%"); if (mp.Success) { v.TrainBrake = Num(mp.Value); v.Av.Add("tbrake"); }
                    const string U = @"\s*([a-zA-Z/²]+)?";   // unidad detrás del número
                    var me = Regex.Match(s, @"\bEQ\s+(-?\d+(?:\.\d+)?)" + U, RegexOptions.IgnoreCase); if (me.Success) { v.EqBar = ToBar(Num(me.Groups[1].Value), me.Groups[2].Value); v.Av.Add("eq"); }
                    var mb = Regex.Match(s, @"\bBC\s+(-?\d+(?:\.\d+)?)" + U, RegexOptions.IgnoreCase); if (mb.Success) { v.BcBar = ToBar(Num(mb.Groups[1].Value), mb.Groups[2].Value); v.Av.Add("bc"); }
                    var mt = Regex.Match(s, @"\bBP\s+(-?\d+(?:\.\d+)?)" + U, RegexOptions.IgnoreCase); if (mt.Success) { v.BpBar = ToBar(Num(mt.Groups[1].Value), mt.Groups[2].Value); v.Av.Add("bp"); }
                }
                else if (label.Contains("engine brake")) { v.EngineBrake = Num(val); v.Av.Add("ebrake"); }
                else if (label.Contains("dynamic brake")) { v.DynBrake = Num(val); v.Av.Add("dyn"); }
                else if (label == "pantographs" || label == "pantograph")
                {
                    // Un estado por pantógrafo de la máquina («Raising Down Down Down»): cuenta como
                    // subido si ALGUNO está «Up» (subiendo o bajando todavía no toca la catenaria).
                    v.Panto |= PantoUp(val); v.Av.Add("panto"); v.Av.Add("pantohud");
                    var est = Regex.Matches(val, @"\b(up|down|raising|lowering)\b", RegexOptions.IgnoreCase);
                    v.PantoCmd = new bool[est.Count];
                    for (int i = 0; i < est.Count; i++)
                    {
                        string e = est[i].Value.ToLowerInvariant();
                        v.PantoCmd[i] = e == "up" || e == "raising";
                    }
                }
                else if (label == "battery switch" || label == "battery") { v.Battery = val.ToLowerInvariant().Contains("on"); v.Av.Add("battery"); }
                else if (label == "master key") { v.MasterKey = val.ToLowerInvariant().Contains("on"); v.Av.Add("mkey"); }
                else if (label == "speed target")
                {
                    double x = Num(val);
                    v.SpeedTarget = val.ToLowerInvariant().Contains("mph") ? x * 1.60934 : x;
                    v.Av.Add("starget");
                }
                else if (label == "cruise control status") v.CruiseStatus = val.Trim();
                else if (label == "horn") { v.Horn = !val.ToLowerInvariant().Contains("off"); v.Av.Add("horn"); }
                else if (label == "bell") { v.Bell = !val.ToLowerInvariant().Contains("off"); v.Av.Add("bell"); }
                else if (label.StartsWith("sander") || label == "sanding") { v.Sand |= !val.ToLowerInvariant().Contains("off"); v.Av.Add("sand"); }
                else if (label.StartsWith("wiper")) { v.Wipers |= !val.ToLowerInvariant().Contains("off"); v.Av.Add("wipers"); }
                else if (label.StartsWith("doors open") || label.StartsWith("door open"))
                {
                    // Solo sale mientras hay puertas abiertas: «Left», «Right» o «Both».
                    string d = val.ToLowerInvariant();
                    bool l = d.Contains("left") || d.Contains("both"), r = d.Contains("right") || d.Contains("both");
                    if (!l && !r) l = r = true;
                    v.DoorLeft |= l; v.DoorRight |= r; v.Av.Add("doorhud");
                }
                else if (label == "circuit breaker") { v.Breaker = val.ToLowerInvariant().Contains("closed"); v.Av.Add("breaker"); }
                else if (label == "power" && val.ToLowerInvariant().Contains("kw")) { v.PowerKw = Num(val); v.Av.Add("power"); }
            }
        }

        static bool PantoUp(string val) => Regex.IsMatch(val ?? "", @"\bup\b", RegexOptions.IgnoreCase);

        // ---------------- HUD página 2: locomotora ----------------
        static void FromPage2(CabValues v, List<(string label, string value)> rows)
        {
            foreach (var (label0, val) in rows)
            {
                string label = label0.ToLowerInvariant(), lv = val.ToLowerInvariant();
                if (label.Contains("reverser") && !v.Has("rev"))
                {
                    var m = Regex.Match(label + " " + val, @"(\d+(?:[.,]\d+)?)\s*%");
                    if (m.Success) { double f = Num(m.Value) / 100.0; v.Reverser = v.Direction == 'R' ? -f : f; v.Av.Add("rev"); }
                }
                if (label.Contains("panto") && PantoUp(val)) { v.Panto = true; v.Av.Add("panto"); v.Av.Add("pantohud"); }
                if (label.EndsWith("power") && lv.Contains("kw")) { v.PowerKw = Num(val); v.Av.Add("power"); }
                if (label.EndsWith("force") && lv.Contains("kn")) { v.EffortKn = Num(val); v.Av.Add("effort"); }
                if (label.EndsWith("speed") && lv.Contains("km/h") && !v.Has("speed")) { v.SpeedKmh = Num(val); v.Av.Add("speed"); }
            }
        }

        // ---------------- HUD página 5: frenos ----------------
        static void FromPage5(CabValues v, List<(string label, string value)> rows)
        {
            foreach (var (label0, val) in rows)
            {
                string label = label0.ToLowerInvariant(), lv = val.ToLowerInvariant();
                string sv = SinColor(val);
                bool numero = Regex.IsMatch(sv, @"\d");
                if (label.Contains("main reservoir") && numero && !v.Has("mr")) { v.MrBar = ToBar(Num(sv), sv); v.Av.Add("mr"); }
                // Tabla de coches: la primera fila es el primer vehículo (la cabina desde la que vas).
                else if (label.EndsWith(".brkcyl") && numero && !v.Has("bc")) { v.BcBar = ToBar(Num(sv), sv); v.Av.Add("bc"); }
                else if (label.EndsWith(".brkpipe") && numero && !v.Has("bp")) { v.BpBar = ToBar(Num(sv), sv); v.Av.Add("bp"); }
                if (label.Contains("compressor")) { v.Compressor = lv.Contains("on") && !lv.Contains("off"); v.Av.Add("compressor"); }
            }
        }

        // ---------------- HUD página 7: despachador (límite y recorrido) ----------------
        static void FromPage7(CabValues v, List<(string label, string value)> rows)
        {
            foreach (var (label0, val) in rows)
            {
                string label = label0.ToLowerInvariant(), lv = val.ToLowerInvariant();
                if ((label.EndsWith(".max") || label == "max") && lv.Contains("km/h") && !v.Has("limit"))
                { double x = Num(val); if (x > 0) { v.LimitKmh = x; v.Av.Add("limit"); } }
                if ((label.EndsWith(".travelled") || label == "travelled") && lv.Contains("km")) { v.OdoKm = Num(val); v.Av.Add("odo"); }
            }
        }

        // ---------------- /API/CABCONTROLS ----------------
        // Orden: los más específicos primero (igual que CONTROL_KEYWORDS de Cockpit-SF).
        static readonly (string[] kw, string key)[] Clases =
        {
            (new[] { "DYNAMIC_BRAKE" }, "dyn"), (new[] { "ENGINE_BRAKE" }, "ebrake"), (new[] { "TRAIN_BRAKE" }, "tbrake"),
            (new[] { "EMERGENCY_BRAKE", "EMERGENCY" }, "emerg"), (new[] { "ORTS_SIGNED_TRACTION_BRAKING" }, "signed"),
            (new[] { "THROTTLE" }, "throttle"), (new[] { "REVERSER", "DIRECTION_DISPLAY", "DIRECTION" }, "rev"),
            (new[] { "MAIN_RES" }, "mr"), (new[] { "BRAKE_PIPE" }, "bp"), (new[] { "EQ_RES", "EQUALIZING" }, "eq"),
            (new[] { "BRAKE_CYL" }, "bc"),
            (new[] { "LINE_VOLTAGE" }, "linev"), (new[] { "AMMETER", "LOAD_METER" }, "amp"),
            (new[] { "TRACTIVE_EFFORT" }, "effort"), (new[] { "RPM" }, "rpm"),
            (new[] { "FUEL_GAUGE", "FUEL" }, "fuel"),
            (new[] { "SPEEDLIM_DISPLAY" }, "limit"), (new[] { "SPEEDOMETER" }, "speed"),
            (new[] { "OVERSPEED" }, "overspeed"), (new[] { "WHEELSLIP" }, "wslip"),
            (new[] { "ALERTER_DISPLAY", "ALERTER" }, "alerter"), (new[] { "PANTO_DISPLAY", "PANTOGRAPH" }, "panto"),
            (new[] { "CIRCUIT_BREAKER" }, "breaker"), (new[] { "MASTER_KEY" }, "mkey"), (new[] { "BATTERY" }, "battery"),
            (new[] { "ORTS_LEFTDOOR", "LEFTDOOR", "LEFT_DOOR" }, "doorl"),
            (new[] { "ORTS_RIGHTDOOR", "RIGHTDOOR", "RIGHT_DOOR" }, "doorr"),
            (new[] { "DOORS" }, "doors"),
            (new[] { "HORN" }, "horn"), (new[] { "BELL" }, "bell"), (new[] { "SANDING", "SANDERS" }, "sand"),
            (new[] { "WIPERS" }, "wipers"), (new[] { "FRONT_HLIGHT", "HEADLIGHT" }, "headl"),
        };

        static string Clase(string type)
        {
            string u = (type ?? "").ToUpperInvariant();
            // Botones y permisos del disyuntor (…_DRIVER_CLOSING_ORDER, …_OPENING_ORDER,
            // …_AUTHORIZATION): dicen si el botón está pulsado, no si está cerrado. Solo valen
            // …_CLOSED y …_STATE.
            if (u.Contains("CIRCUIT_BREAKER") && (u.Contains("ORDER") || u.Contains("AUTHORIZATION") || u.Contains("BUTTON")))
                return null;
            foreach (var (kw, key) in Clases)
                foreach (var k in kw) if (u.Contains(k)) return key;
            return null;
        }

        static void FromCabControls(CabValues v, string json)
        {
            var visto = new HashSet<string>(StringComparer.Ordinal);
            bool? puertasGen = null;   // indicador general de puertas (DOORS_DISPLAY), sin lado
            try
            {
                using var d = JsonDocument.Parse(json);
                var arr = d.RootElement;
                if (arr.ValueKind == JsonValueKind.Object && arr.TryGetProperty("controls", out var cs)) arr = cs;
                if (arr.ValueKind != JsonValueKind.Array) return;
                foreach (var e in arr.EnumerateArray())
                {
                    string type = e.TryGetProperty("TypeName", out var tn) ? tn.GetString() ?? "" : "";
                    string key = Clase(type); if (key == null || (visto.Contains(key) && key != "panto")) continue;
                    double min = e.TryGetProperty("MinValue", out var mi) && mi.TryGetDouble(out var a) ? a : 0;
                    double max = e.TryGetProperty("MaxValue", out var ma) && ma.TryGetDouble(out var b) ? b : 1;
                    if (max == min) max = min + 1;
                    double fr = e.TryGetProperty("RangeFraction", out var rf) && rf.TryGetDouble(out var f) ? f : 0;
                    double real = min + fr * (max - min);
                    string tu = type.ToUpperInvariant();

                    switch (key)
                    {
                        case "horn": v.Horn |= fr > 0.5; break;
                        case "bell": v.Bell |= fr > 0.5; break;
                        case "sand": v.Sand |= fr > 0.5; break;
                        case "wipers": v.Wipers |= fr > 0.5; break;
                        case "panto": if (!v.Has("pantohud")) v.Panto |= fr > 0.5; break;   // alguno subido
                        case "breaker": if (!v.Has("breaker")) v.Breaker = fr > 0.5; break;   // manda el HUD («Closed»/«Open»)
                        case "mkey": v.MasterKey = fr > 0.5; break;
                        case "battery": v.Battery = fr > 0.5; break;
                        case "alerter": v.Alerter = fr > 0.5; break;
                        case "emerg": v.Emergency = fr > 0.5; break;
                        case "overspeed": v.Overspeed = fr > 0.5; break;
                        case "wslip": v.Wheelslip = fr > 0.5; break;
                        case "doorl": v.DoorLeft |= fr > 0.5; break;
                        case "doorr": v.DoorRight |= fr > 0.5; break;
                        case "doors":
                            // Indicador GENERAL («hay alguna puerta abierta»), sin lado. Se guarda y solo
                            // se usa al final si la cabina no tiene mandos de puerta izquierda/derecha:
                            // antes, si salía antes que ellos, encendía LAS DOS puertas al abrir una.
                            puertasGen ??= fr > 0.5;
                            visto.Add(key); continue;
                        case "headl":
                            // FRONT_HLIGHT va de 0 a 2 (apagados, cortos, largos): fracción 0 / 0,5 / 1.
                            // Si la cabina no da el rango (0-0), solo se sabe si están encendidos.
                            v.HeadlightLevel = max - min >= 1.5 ? (fr < 0.25 ? 0 : fr < 0.75 ? 1 : 2) : (fr > 0.5 ? 1 : 0);
                            v.Headlight = v.HeadlightLevel > 0;
                            break;
                        case "signed":
                            // Ya viene en kN (−180 … 320 típicamente): esfuerzo con signo.
                            v.EffortKn = real; v.Av.Add("effort"); visto.Add(key); continue;
                        case "throttle": v.Throttle = fr * 100; break;
                        case "tbrake": v.TrainBrake = fr * 100; break;
                        case "ebrake": v.EngineBrake = fr * 100; break;
                        case "dyn": v.DynBrake = fr * 100; break;
                        case "rev":
                            if (tu.Contains("DIRECTION_DISPLAY")) continue;     // solo dice si hay «F» visible
                            v.Reverser = tu.Contains("DIRECTION") && Math.Abs((max - min) - 2) < 0.01 ? real - 1 : 2 * fr - 1;
                            v.Direction = v.Reverser > 0.3 ? 'F' : v.Reverser < -0.3 ? 'R' : 'N';
                            v.Av.Add("dir");
                            break;
                        case "speed":
                            if (tu == "SPEEDOMETER" && max > 0) v.SpeedoMax = max;   // rango de la esfera de la cabina
                            if (!v.Has("speed") || v.SpeedKmh == 0) { v.SpeedKmh = Math.Abs(real); v.Av.Add("speed"); v.SpeedFromCab = true; }
                            visto.Add(key); continue;
                        case "limit":
                            if (real > 0) { v.LimitKmh = real; v.Av.Add("limit"); v.LimitFromCab = true; }
                            visto.Add(key); continue;
                        case "linev":
                            // Algunas cabinas lo dan por NIVELES (0..4); otras en voltios.
                            v.LineKv = real <= 4.5 ? (real > 0 ? real * (25.0 / 3.3) : 0) : real / 1000.0;
                            break;
                        case "amp": v.Ammeter = real; break;
                        case "effort": v.EffortKn = Math.Abs(real) > 1000 ? real / 1000.0 : real; break;
                        case "rpm": v.Rpm = real; break;
                        case "fuel": v.Fuel = real; break;
                        // Presiones: solo si el HUD (que da la unidad) no las ha dado ya.
                        case "mr": if (v.Has("mr")) { visto.Add(key); continue; } v.MrBar = CabBar(real, max); break;
                        case "bp": if (v.Has("bp")) { visto.Add(key); continue; } v.BpBar = CabBar(real, max); break;
                        case "eq": if (v.Has("eq")) { visto.Add(key); continue; } v.EqBar = CabBar(real, max); break;
                        case "bc": if (v.Has("bc")) { visto.Add(key); continue; } v.BcBar = CabBar(real, max); break;
                    }
                    v.Av.Add(key);
                    visto.Add(key);
                }
                if (puertasGen.HasValue && !visto.Contains("doorl") && !visto.Contains("doorr"))
                {
                    v.DoorLeft |= puertasGen.Value; v.DoorRight |= puertasGen.Value;
                    v.Av.Add("doorl"); v.Av.Add("doorr");
                }
            }
            catch { }
        }
    }
}
