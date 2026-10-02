// Carné por puntos del maquinista (carne-por-puntos.sql).
//  · Durante el servicio SelectOR vigila tres cosas que la API de Open Rails deja ver:
//      A1 salto de posición (el tren aparece a más de 1 km en un sondeo, a más de 400 km/h),
//      A3 tiempo acelerado (la hora del juego corre más de 1,5 veces más rápido que la real durante 60 s),
//      B6/B7 exceso de velocidad sobre el límite del Track Monitor durante 30 s
//            (leve: más de un 10 % y más de 5 km/h por encima; grave: más de un 30 %).
//    A2 (velocidad media imposible) la decide el servidor con su propio reloj al cerrar el servicio.
//  · Al registrar el servicio se envía lo detectado (report_infractions). El SERVIDOR aplica las reglas:
//    puntos, tope de 6 por servicio, suspensión… A1 y A3 anulan el servicio.
//  · Todas restan los puntos al momento. B6 y B7 quedan pendientes de revisión: el superadministrador
//    las confirma o las anula (se devuelven los puntos) en «Revisión», con las de todas las empresas.
//    En los viajes cortos (menos de 3 km o de 5 minutos) no cuenta ninguna. Mi perfil muestra el carné.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SelectOR
{
    // Textos y colores del carné (los usa también la ventana de resultado del servicio).
    public static class Carne
    {
        public const int MaxPoints = 15;
        public const int WarnBelow = 10;       // por debajo: aviso
        public const int PenaltyBelow = 6;     // por debajo: rango congelado y sin liga ni ranking
        public const int CleanForPoint = 5;    // servicios limpios seguidos para recuperar un punto

        public static readonly Color Green = Color.FromArgb(129, 199, 132);
        public static readonly Color Gold = Color.FromArgb(245, 197, 66);
        public static readonly Color Orange = Color.FromArgb(251, 146, 60);
        public static readonly Color Red = Color.FromArgb(229, 115, 115);
        public static readonly Color Blue = Color.FromArgb(96, 165, 250);

        public static string Code(string code) => code switch
        {
            "jump" => "A1", "speed_avg" => "A2", "time_accel" => "A3",
            "overspeed" => "B6", "overspeed_grave" => "B7", "recovery" => "+1", _ => "—"
        };

        public static string Name(string code) => I18n.T(code switch
        {
            "jump" => "Salto de posición",
            "speed_avg" => "Velocidad media imposible",
            "time_accel" => "Tiempo acelerado",
            "overspeed" => "Exceso de velocidad",
            "overspeed_grave" => "Exceso de velocidad grave",
            "recovery" => "Recuperación de un punto",
            _ => "Infracción"
        });

        public static string Label(string code) => Code(code) + " · " + Name(code);

        public static string StatusName(string status) => I18n.T(status switch
        {
            "applied" => "Aplicada",
            "pending" => "Pendiente de revisión",
            "annulled" => "Anulada",
            _ => "—"
        });

        public static Color StatusColor(string status) => status switch
        {
            "applied" => Red, "pending" => Gold, "annulled" => Color.FromArgb(150, 156, 160), _ => Color.Gray
        };

        public static Color PointsColor(int pts) => pts >= WarnBelow ? Green : pts >= PenaltyBelow ? Gold : Red;

        public static string PointsText(int pts) =>
            (pts > 0 ? "+" : pts < 0 ? "−" : "") + Math.Abs(pts) + " " + I18n.T(Math.Abs(pts) == 1 ? "punto" : "puntos");

        // Detalle legible de lo que se detectó (lo que guarda el servidor en «detail»).
        public static string Detail(string code, JsonElement d)
        {
            if (d.ValueKind != JsonValueKind.Object) return "";
            double N(string k) => d.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : double.NaN;
            var es = CultureInfo.GetCultureInfo("es-ES");
            string F(double x, string fmt = "N0") => x.ToString(fmt, es);
            switch (code)
            {
                case "jump":
                    return double.IsNaN(N("dist_m")) ? "" : string.Format(I18n.T("{0} m en {1} s"), F(N("dist_m")), F(N("secs"), "N1"));
                case "speed_avg":
                    return double.IsNaN(N("avg_kmh")) ? "" : string.Format(I18n.T("media de {0} km/h ({1} km)"), F(N("avg_kmh")), F(N("km"), "N1"));
                case "time_accel":
                    return double.IsNaN(N("seconds")) ? "" : string.Format(I18n.T("{0} s acelerado (hasta ×{1})"), F(N("seconds")), F(N("factor"), "N1"));
                case "overspeed":
                case "overspeed_grave":
                    return double.IsNaN(N("max_kmh")) ? "" : string.Format(I18n.T("{0} km/h con límite {1} · {2} s"), F(N("max_kmh")), F(N("limit_kmh")), F(N("seconds")));
            }
            return "";
        }

        // Una infracción de un servicio, tal como la devuelve el servidor.
        public sealed class Item
        {
            public string Code = "", Status = "", Detail = "";
            public int Points;
        }

        public static List<Item> ParseItems(JsonElement arr)
        {
            var l = new List<Item>();
            if (arr.ValueKind != JsonValueKind.Array) return l;
            foreach (var e in arr.EnumerateArray())
            {
                string code = e.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : "";
                if (code == "recovery") continue;
                l.Add(new Item
                {
                    Code = code,
                    Status = e.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : "",
                    Points = e.TryGetProperty("points", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : 0,
                    Detail = e.TryGetProperty("detail", out var d) ? Detail(code, d) : ""
                });
            }
            return l;
        }

        // Carné («license»): puntos y fin de la suspensión. points = −1 si no viene.
        public static (int points, DateTime? until) ParseLicense(JsonElement lic)
        {
            if (lic.ValueKind != JsonValueKind.Object) return (-1, null);
            int pts = lic.TryGetProperty("points", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : -1;
            DateTime? until = null;
            if (lic.TryGetProperty("suspended_until", out var u) && u.ValueKind == JsonValueKind.String
                && DateTime.TryParse(u.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt)
                && dt > DateTime.UtcNow)
                until = dt;
            return (pts, until);
        }

        public static string FmtLocal(DateTime utc) => utc.ToLocalTime().ToString(I18n.English ? "dd/MM/yyyy HH:mm" : "dd-MM-yyyy HH:mm");

        // Barra del carné: 15 casillas, coloreadas según el nivel, con marcas en 6 y 10.
        public static void PaintBar(Graphics g, Rectangle r, int points)
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            const int gap = 3;
            float cw = (r.Width - gap * (MaxPoints - 1)) / (float)MaxPoints;
            if (cw < 2) return;
            var on = PointsColor(points);
            for (int i = 0; i < MaxPoints; i++)
            {
                var cell = new Rectangle((int)(r.X + i * (cw + gap)), r.Y, (int)Math.Ceiling(cw), r.Height);
                using var path = Theme.Round(cell, Math.Min(3, r.Height / 2));
                using var b = new SolidBrush(i < points ? on : Theme.Surface2);
                g.FillPath(b, path);
            }
            using var pen = new Pen(Color.FromArgb(150, 200, 205, 210), 1) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot };
            foreach (int mark in new[] { PenaltyBelow, WarnBelow })
            {
                int x = (int)(r.X + mark * (cw + gap) - gap / 2f);
                g.DrawLine(pen, x, r.Y - 4, x, r.Bottom + 4);
            }
        }
    }

    public partial class MainMenuForm
    {
        // ---------------------------------------------------------------- detección durante el servicio
        readonly List<(string code, Dictionary<string, object> detail)> _infrItems = new();
        bool _infrJumpDone, _infrAccelDone;
        double _infrAccelS, _infrAccelMax;
        DateTime? _infrLastFixUtc;
        // Episodio de exceso de velocidad en curso (tiempos en segundos de servicio, sin pausas).
        double _osStart = double.NaN, _osLast, _osGraveStart = double.NaN, _osGraveLast, _osGraveMax;
        double _osWorstV, _osWorstL, _osWorstRatio;
        bool _osBusy;
        bool _osToldB6, _osToldB7;   // episodio en curso: ya se ha avisado de B6 / B7

        const double JumpMinM = 1000, JumpMinKmh = 400;     // A1
        const double AccelFactor = 1.5, AccelMinS = 60;      // A3
        const double OverspeedS = 30, OverspeedGapS = 3;     // B6 / B7

        bool InfrActive => _pendingServiceId != null && _svcClockUtc != null;

        // Servicio nuevo: nada detectado todavía.
        void InfrReset()
        {
            _infrItems.Clear();
            _infrJumpDone = _infrAccelDone = false;
            _infrAccelS = _infrAccelMax = 0;
            _infrLastFixUtc = null;
            _osStart = double.NaN; _osGraveStart = double.NaN; _osGraveMax = 0; _osWorstRatio = 0;
        }

        // A1 · Salto de posición: entre dos sondeos el tren ha aparecido a más de 1 km, más deprisa de lo
        // que puede ir un tren. (No cuenta los primeros segundos del servicio, mientras se coloca el tren.)
        void InfrCheckJump(double dm)
        {
            var now = DateTime.UtcNow;
            double dt = _infrLastFixUtc == null ? 1.5 : Math.Max(0.5, (now - _infrLastFixUtc.Value).TotalSeconds);
            _infrLastFixUtc = now;
            if (!InfrActive || _infrJumpDone || ServiceSecondsExact() < 15) return;
            if (dm > JumpMinM && dm / dt * 3.6 > JumpMinKmh)
            {
                _infrJumpDone = true;
                _infrItems.Add(("jump", new Dictionary<string, object>
                {
                    ["dist_m"] = Math.Round(dm), ["secs"] = Math.Round(dt, 1), ["at_s"] = (int)ServiceSecondsExact()
                }));
                InfrNotify("jump", string.Format(Tr("El tren ha aparecido a {0} m en un instante."), dm.ToString("N0", EsEs)));
            }
        }

        // A3 · Tiempo acelerado: la hora del juego avanza más de 1,5 veces más rápido que la real.
        // Se suman los tramos acelerados; con 60 s en total, queda anotado.
        void InfrAccelTick(double realS, double gameS)
        {
            if (!InfrActive || _infrAccelDone || realS < 0.3 || realS > 10 || gameS <= 0) return;
            double f = gameS / realS;
            if (f <= AccelFactor || f > 1000) return;
            _infrAccelS += realS; _infrAccelMax = Math.Max(_infrAccelMax, f);
            if (_infrAccelS >= AccelMinS)
            {
                _infrAccelDone = true;
                _infrItems.Add(("time_accel", new Dictionary<string, object>
                {
                    ["seconds"] = (int)Math.Round(_infrAccelS), ["factor"] = Math.Round(_infrAccelMax, 1)
                }));
                InfrNotify("time_accel", string.Format(Tr("La hora del simulador ha ido hasta ×{0} más rápida durante {1} s."),
                    _infrAccelMax.ToString("0.#", EsEs), Math.Round(_infrAccelS).ToString("N0", EsEs)));
            }
        }

        // B6 / B7 · Velocidad y límite del Track Monitor de OR (cada sondeo del servicio).
        async Task InfrSpeedPoll()
        {
            if (_osBusy || !InfrActive || _kmHttp == null || _svcPaused) return;
            _osBusy = true;
            try
            {
                string jt = await _kmHttp.GetStringAsync("/API/TRACKMONITORDISPLAY");
                var (v, lim) = TmSpeedLimit(jt);
                bool ok = !double.IsNaN(v) && !double.IsNaN(lim) && lim > 0;
                InfrOverspeedSample(ok && v > lim * 1.1 && v > lim + 5, ok && v > lim * 1.3 && v > lim + 5, v, lim);
            }
            catch { }
            finally { _osBusy = false; }
        }

        void InfrOverspeedSample(bool leve, bool grave, double v, double lim)
        {
            if (!InfrActive) return;
            double t = ServiceSecondsExact();
            if (leve)
            {
                if (double.IsNaN(_osStart) || t - _osLast > OverspeedGapS)
                {
                    InfrOverspeedClose();
                    _osStart = t; _osWorstRatio = 0; _osGraveMax = 0; _osGraveStart = double.NaN;
                    _osToldB6 = _osToldB7 = false;
                }
                _osLast = t;
                if (v / lim > _osWorstRatio) { _osWorstRatio = v / lim; _osWorstV = v; _osWorstL = lim; }
                if (grave)
                {
                    if (double.IsNaN(_osGraveStart) || t - _osGraveLast > OverspeedGapS) _osGraveStart = t;
                    _osGraveLast = t;
                    _osGraveMax = Math.Max(_osGraveMax, t - _osGraveStart);
                }
                // Aviso en directo en cuanto el exceso cumple los 30 s (el grave, aunque ya se avisara el leve).
                string det = string.Format(Tr("{0} km/h con límite {1} durante más de 30 s."), Math.Round(_osWorstV).ToString("N0", EsEs), Math.Round(_osWorstL).ToString("N0", EsEs));
                if (!_osToldB7 && _osGraveMax >= OverspeedS) { _osToldB7 = _osToldB6 = true; InfrNotify("overspeed_grave", det); }
                else if (!_osToldB6 && _osLast - _osStart >= OverspeedS) { _osToldB6 = true; InfrNotify("overspeed", det); }
            }
            else if (!double.IsNaN(_osStart) && t - _osLast > OverspeedGapS) InfrOverspeedClose();
        }

        // Aviso en directo sobre Open Rails (abajo a la derecha, sin quitar el teclado al simulador): qué se ha
        // detectado y lo que supondrá al registrar el servicio. Un clic lo cierra (no abre SelectOR).
        void InfrNotify(string code, string what)
        {
            try
            {
                int pts = code switch { "jump" => 6, "time_accel" => 3, "overspeed" => 1, "overspeed_grave" => 3, _ => 0 };
                string pp = pts + " " + Tr(pts == 1 ? "punto" : "puntos");
                string then = code == "jump" || code == "time_accel"
                    ? string.Format(Tr("El servicio no se registrará y restará {0} del carné (salvo en un viaje de menos de 3 km o 5 minutos)."), pp)
                    : string.Format(Tr("Si el servicio se registra, restará {0} del carné, pendiente de revisión."), pp);
                var toast = new NotificationToast("⚠", Tr("Infracción") + " · " + Carne.Label(code), what + "\n" + then,
                                                  "SelectOR · " + Tr("Carné por puntos"), code == "overspeed" ? Carne.Gold : Carne.Red);
                EnqueueToast(toast);
            }
            catch { }
        }

        // Fin de un episodio: 30 s por encima → B6; 30 s por encima del 30 % → B7.
        void InfrOverspeedClose()
        {
            if (double.IsNaN(_osStart)) return;
            double dur = _osLast - _osStart;
            string code = _osGraveMax >= OverspeedS ? "overspeed_grave" : dur >= OverspeedS ? "overspeed" : null;
            if (code != null)
                _infrItems.Add((code, new Dictionary<string, object>
                {
                    ["max_kmh"] = Math.Round(_osWorstV), ["limit_kmh"] = Math.Round(_osWorstL),
                    ["seconds"] = (int)Math.Round(code == "overspeed_grave" ? _osGraveMax : dur), ["at_s"] = (int)_osStart
                }));
            _osStart = double.NaN; _osGraveStart = double.NaN; _osGraveMax = 0; _osWorstRatio = 0;
        }

        // Velocidad y límite (km/h) del Track Monitor. Las filas están traducidas al idioma de OR, pero
        // las tres primeras (sin separadores) son siempre Velocidad, Proyectada y Límite; el valor va en
        // TrackCol («73,3 km/h» con un código de color de 3 signos al final).
        static (double speed, double limit) TmSpeedLimit(string json)
        {
            try
            {
                using var d = JsonDocument.Parse(json);
                if (d.RootElement.ValueKind != JsonValueKind.Array) return (double.NaN, double.NaN);
                var vals = new List<string>();
                foreach (var r in d.RootElement.EnumerateArray())
                {
                    if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("FirstCol", out var fc) || fc.ValueKind != JsonValueKind.String) continue;
                    if ((fc.GetString() ?? "").StartsWith("Sprtr", StringComparison.Ordinal)) continue;
                    vals.Add(r.TryGetProperty("TrackCol", out var tc) && tc.ValueKind == JsonValueKind.String ? tc.GetString() ?? "" : "");
                    if (vals.Count == 3) break;
                }
                if (vals.Count < 3) return (double.NaN, double.NaN);
                return (TmKmh(vals[0]), TmKmh(vals[2]));
            }
            catch { return (double.NaN, double.NaN); }
        }

        static readonly Regex TmNum = new(@"(-?\d+(?:[.,]\d+)?)\s*(km/h|mph)", RegexOptions.IgnoreCase);

        static double TmKmh(string cell)
        {
            var m = TmNum.Match(cell ?? "");
            if (!m.Success) return double.NaN;
            if (!double.TryParse(m.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double x)) return double.NaN;
            x = Math.Abs(x);
            return m.Groups[2].Value.Equals("mph", StringComparison.OrdinalIgnoreCase) ? x * 1.60934 : x;
        }

        // ---------------------------------------------------------------- envío al registrar
        sealed class InfrReport
        {
            public string Err;
            public bool Voided;
            public List<Carne.Item> Items = new();
            public int Points = -1;
            public DateTime? Until;
        }

        // Envía al servidor lo detectado en este servicio. Sin nada que enviar, o con un servidor sin el
        // carné (sin carne-por-puntos.sql), no hace nada. Si falla la conexión, se conserva para reintentar.
        async Task<InfrReport> ReportInfractionsAsync(string svc, double km, int durationS, bool shortTrip)
        {
            InfrOverspeedClose();   // un exceso de velocidad que seguía en curso al registrar
            var rep = new InfrReport();
            // Viaje corto (no se registra): no cuenta ninguna infracción.
            if (shortTrip) _infrItems.Clear();
            if (_infrItems.Count == 0) return rep;
            var items = new List<object>();
            foreach (var (code, detail) in _infrItems) items.Add(new { code, detail });
            var (json, err) = await Supa.RpcAsync("report_infractions",
                new { p_service = svc, p_items = items, p_km = Math.Round(km, 1), p_duration_s = durationS });
            // Servidor sin carne-revision-superadmin.sql: sin los km ni la duración.
            if (err != null && (err.IndexOf("PGRST202", StringComparison.OrdinalIgnoreCase) >= 0
                                || err.IndexOf("Could not find the function", StringComparison.OrdinalIgnoreCase) >= 0))
                (json, err) = await Supa.RpcAsync("report_infractions", new { p_service = svc, p_items = items });
            if (err != null)
            {
                if (err.IndexOf("PGRST202", StringComparison.OrdinalIgnoreCase) >= 0
                    || err.IndexOf("Could not find the function", StringComparison.OrdinalIgnoreCase) >= 0)
                { _infrItems.Clear(); return rep; }
                rep.Err = err; return rep;
            }
            _infrItems.Clear();
            try
            {
                using var d = JsonDocument.Parse(json);
                var root = d.RootElement;
                rep.Voided = root.TryGetProperty("voided", out var v) && v.ValueKind == JsonValueKind.True;
                if (root.TryGetProperty("infractions", out var arr)) rep.Items = Carne.ParseItems(arr);
                if (root.TryGetProperty("license", out var lic)) (rep.Points, rep.Until) = Carne.ParseLicense(lic);
            }
            catch { }
            return rep;
        }

        // Motivos por los que un servicio falseado no se registra.
        List<string> InfrVoidReasons(List<Carne.Item> items)
        {
            var l = new List<string>();
            if (items.Exists(x => x.Code == "jump"))
                l.Add(Tr("Salto de posición del tren (teletransporte): el servicio no se registra."));
            if (items.Exists(x => x.Code == "time_accel"))
                l.Add(Tr("Conducción con el tiempo acelerado: el servicio no se registra."));
            if (l.Count == 0) l.Add(Tr("Registro no válido: el servicio no se registra."));
            return l;
        }

        // ---------------------------------------------------------------- Mi perfil: carné
        Label _licPtsLbl, _licStateLbl, _licNoteLbl;
        Panel _licBar;
        int _licPoints = Carne.MaxPoints;
        double _licFrozenKm = double.NaN;   // km del rango congelado (NaN = no congelado)
        LicenseTimeline _licHist;

        Control BuildLicenseCard()
        {
            var card = new Card { Dock = DockStyle.Top, Height = 100, Fill = Theme.Surface, Radius = 12, Margin = new Padding(0, 0, 0, 6), Padding = new Padding(16, 10, 16, 12) };
            var inner = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Color.Transparent };
            inner.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            inner.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
            inner.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var top = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0) };
            top.Controls.Add(new Label { Text = Tr("CARNÉ POR PUNTOS"), AutoSize = true, ForeColor = Theme.Subtle, Font = Theme.Font(8f, FontStyle.Bold), Margin = new Padding(0, 6, 10, 0) });
            _licPtsLbl = new Label { Text = "—", AutoSize = true, ForeColor = Carne.Green, Font = Theme.Font(15f, FontStyle.Bold), Margin = new Padding(0) };
            _licStateLbl = new Label { Text = "", AutoSize = true, ForeColor = Theme.Subtle, Font = Theme.Font(9f, FontStyle.Bold), Margin = new Padding(12, 6, 0, 0) };
            top.Controls.Add(_licPtsLbl); top.Controls.Add(_licStateLbl);
            _licBar = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent, Margin = new Padding(0, 6, 0, 2) };
            _licBar.Paint += (s, e) => Carne.PaintBar(e.Graphics, new Rectangle(0, 4, Math.Max(1, _licBar.ClientSize.Width - 1), 8), _licPoints);
            _licNoteLbl = new Label { Text = "", AutoSize = true, ForeColor = Theme.Subtle, Font = Theme.Font(8.5f), Margin = new Padding(0, 2, 0, 0) };
            inner.Controls.Add(top, 0, 0); inner.Controls.Add(_licBar, 0, 1); inner.Controls.Add(_licNoteLbl, 0, 2);
            card.Controls.Add(inner);
            return card;
        }

        LicenseTimeline BuildLicenseHistory()
        {
            _licHist = new LicenseTimeline { Dock = DockStyle.Top, Empty = Tr("Sin infracciones. ¡Buena conducción!"),
                                             EmptySub = Tr("Aquí aparecerán las infracciones del carné y los puntos que recuperes.") };
            return _licHist;
        }

        // Carga el carné (my_license). Sin el SQL del carné, la tarjeta queda con 15 puntos y sin historial.
        async Task LoadLicense()
        {
            if (_licPtsLbl == null) return;
            var (json, err) = await Supa.RpcAsync("my_license", new { });
            int pts = Carne.MaxPoints, clean = 0; DateTime? until = null; _licFrozenKm = double.NaN;
            var rows = new List<LicenseTimeline.Entry>();
            if (err == null && !string.IsNullOrWhiteSpace(json) && json.Trim() != "null")
            {
                try
                {
                    using var d = JsonDocument.Parse(json);
                    var root = d.RootElement;
                    (pts, until) = Carne.ParseLicense(root);
                    if (pts < 0) pts = Carne.MaxPoints;
                    clean = (int)Num(root, "clean_services");
                    if (root.TryGetProperty("rank_frozen_km", out var fk) && fk.ValueKind == JsonValueKind.Number) _licFrozenKm = fk.GetDouble();
                    if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                        foreach (var e in items.EnumerateArray())
                        {
                            string code = Str(e, "code"), status = Str(e, "status");
                            int p = (int)Num(e, "points");
                            string det = e.TryGetProperty("detail", out var dd) ? Carne.Detail(code, dd) : "";
                            string route = Str(e, "route");
                            if (route.Length > 0) det = det.Length > 0 ? route + " · " + det : route;
                            if (code == "recovery") det = string.Format(Tr("{0} servicios seguidos sin infracciones"), Carne.CleanForPoint);
                            DateTime.TryParse(Str(e, "created_at"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var at);
                            rows.Add(new LicenseTimeline.Entry
                            {
                                Local = at == DateTime.MinValue ? at : DateTime.SpecifyKind(at, DateTimeKind.Utc).ToLocalTime(),
                                Code = code, CodeTag = Carne.Code(code), Title = Carne.Name(code), Detail = det, Company = Str(e, "company"), Points = p, Status = status,
                                StatusText = code == "recovery" ? Tr("Recuperado") : Carne.StatusName(status)
                            });
                        }
                }
                catch { }
            }
            _licPoints = Math.Max(0, Math.Min(Carne.MaxPoints, pts));
            _licPtsLbl.Text = string.Format(Tr("{0} de {1} puntos"), _licPoints, Carne.MaxPoints);
            _licPtsLbl.ForeColor = until != null ? Carne.Red : Carne.PointsColor(_licPoints);
            if (until != null)
            { _licStateLbl.Text = string.Format(Tr("SUSPENDIDO hasta el {0}"), Carne.FmtLocal(until.Value)); _licStateLbl.ForeColor = Carne.Red; }
            else if (_licPoints < Carne.PenaltyBelow)
            { _licStateLbl.Text = Tr("Sanción: rango congelado y fuera de la liga"); _licStateLbl.ForeColor = Carne.Red; }
            else if (_licPoints < Carne.WarnBelow)
            { _licStateLbl.Text = Tr("Aviso: por debajo de 10 puntos"); _licStateLbl.ForeColor = Carne.Gold; }
            else
            { _licStateLbl.Text = Tr("En regla"); _licStateLbl.ForeColor = Carne.Green; }
            _licNoteLbl.Text = until != null
                ? Tr("Con el carné suspendido no puedes ponerte de servicio. Al terminar la suspensión vuelves con 8 puntos.")
                : _licPoints >= Carne.MaxPoints
                    ? Tr("Carné completo. Por debajo de 10: aviso · por debajo de 6: rango congelado y fuera de la liga · 0: suspensión de 7 días.")
                    : string.Format(Tr("Recuperación: {0} de {1} servicios seguidos sin infracciones para sumar 1 punto."), Math.Min(clean, Carne.CleanForPoint - 1), Carne.CleanForPoint);
            _licBar.Invalidate();

            if (_licHist != null)
            {
                bool realErr = err != null && err.IndexOf("PGRST202", StringComparison.OrdinalIgnoreCase) < 0;
                _licHist.Empty = realErr ? Tr("Error: ") + err : Tr("Sin infracciones. ¡Buena conducción!");
                _licHist.SetEntries(rows);
            }
        }

        // Km con los que se calcula el rango: con el carné por debajo de 6, los del momento en que se congeló.
        double RankKm(double validKm) => double.IsNaN(_licFrozenKm) ? validKm : Math.Min(validKm, _licFrozenKm);

        // ---------------------------------------------------------------- Revisión (gerente / gestores)
        StyledTable _revList; Label _revMsg;
        readonly List<(string id, string status, string driver, string code)> _revRows = new();
        static bool IsCodeA(string code) => code == "jump" || code == "speed_avg" || code == "time_accel";

        Panel BuildReviewSubpanel()
        {
            var outer = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, BackColor = Theme.Bg };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // explicación
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // pestañas + búsqueda
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // tabla
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // acciones

            var intro = EmpIntro("Todas las infracciones del carné de todos los usuarios. En cualquier momento puedes aprobarlas (los puntos quedan restados), anularlas (se devuelven los puntos) o eliminarlas (se devuelven los puntos y se borran). Los excesos de velocidad (B6 y B7) llegan pendientes de revisión.");
            intro.MaximumSize = new Size(900, 0);
            t.Controls.Add(intro);

            _revList = EmpTable();
            var top = new TableLayoutPanel { Anchor = AnchorStyles.Left | AnchorStyles.Right, Height = 42, ColumnCount = 2, RowCount = 1, BackColor = Theme.Bg, Margin = new Padding(0, 0, 0, 6) };
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            top.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            // Todas · Pendientes (B6/B7 por revisar) · A1, A2 y A3 · B6 y B7
            var tabs = MakeSubTabs(new[] { "Todas", "Pendientes", "A1 · A2 · A3", "B6 · B7" }, i =>
            {
                _revList.RowFilter = i == 1 ? cells => cells.Length > 0 && cells[^1] == Carne.StatusName("pending")
                                   : i == 2 ? cells => cells.Length > 3 && cells[3].StartsWith("A")
                                   : i == 3 ? cells => cells.Length > 3 && cells[3].StartsWith("B")
                                   : (Func<string[], bool>)null;
                _revList.Refilter();
            });
            tabs.Dock = DockStyle.Fill; tabs.Margin = new Padding(0);
            _revList.RowFilter = null;   // de entrada, todas
            var search = EmpSearch(_revList, 260); search.Anchor = AnchorStyles.Right; search.Margin = new Padding(10, 3, 0, 3);
            top.Controls.Add(tabs, 0, 0); top.Controls.Add(search, 1, 0);
            t.Controls.Add(top);

            _revList.SetColumns(
                new StyledTable.Col("FECHA", 92),
                new StyledTable.Col("EMPRESA", 150),
                new StyledTable.Col("MAQUINISTA", 128),
                new StyledTable.Col("INFRACCIÓN", 230),
                new StyledTable.Col("DETALLE", 0, true),
                new StyledTable.Col("TREN", 150),
                new StyledTable.Col("PUNTOS", 80, false, HorizontalAlignment.Right),
                new StyledTable.Col("ESTADO", 170));
            t.Controls.Add(_revList);

            var btns = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Theme.Bg, Margin = new Padding(0) };
            var ok = EmpButton(Tr("Aprobar"), primary: true); ok.Width = 170;
            ok.Click += (s, e) => AdminInfraction("confirm");
            var no = EmpButton(Tr("Anular")); no.Width = 150; no.Margin = new Padding(8, 10, 2, 2);
            no.Click += (s, e) => AdminInfraction("annul");
            var del = EmpButton(Tr("Eliminar")); del.Width = 150; del.Margin = new Padding(8, 10, 2, 2);
            del.BaseColor = Color.FromArgb(60, 44, 44); del.HoverColor = Color.FromArgb(150, 60, 60); del.TextColor = Color.FromArgb(229, 115, 115);
            del.Click += (s, e) => AdminInfraction("delete");
            btns.Controls.Add(ok); btns.Controls.Add(no); btns.Controls.Add(del);
            _revMsg = EmpMsg(); _revMsg.Margin = new Padding(12, 20, 2, 2); _revMsg.MaximumSize = new Size(600, 0);
            btns.Controls.Add(_revMsg);
            t.Controls.Add(btns);

            outer.Controls.Add(t);
            return outer;
        }

        // Carga las infracciones de TODAS las empresas (solo el superadministrador). onlyCount: solo el
        // contador «Revisión (n)» del menú.
        bool _revLoading;
        async void LoadReview(bool onlyCount = false)
        {
            if (!Supa.IsSuperadmin) { SetReviewCount(0); return; }
            if (_revLoading && onlyCount) return;   // un contador ya en camino basta
            _revLoading = true;
            string json, err;
            try { (json, err) = await Supa.RpcAsync("list_all_infractions", new { p_limit = 2000 }); }
            finally { _revLoading = false; }
            int pending = 0;
            var rows = new List<(string[] cells, Color?[] colors, string id, string status, string driver, string code)>();
            if (err == null)
            {
                try
                {
                    using var d = JsonDocument.Parse(json);
                    foreach (var e in d.RootElement.EnumerateArray())
                    {
                        string code = Str(e, "code"), status = Str(e, "status");
                        if (status == "pending") pending++;
                        int p = (int)Num(e, "points");
                        string det = e.TryGetProperty("detail", out var dd) ? Carne.Detail(code, dd) : "";
                        string route = Str(e, "route");
                        if (route.Length > 0) det = det.Length > 0 ? route + " · " + det : route;
                        string who = Str(e, "username"); if (who.Length == 0) who = "—";
                        string st = Carne.StatusName(status);
                        string rev = Str(e, "reviewer");
                        if (status != "pending" && rev.Length > 0) det += (det.Length > 0 ? " · " : "") + string.Format(Tr("revisada por {0}"), rev);
                        string train = Str(e, "consist"); if (train.Length == 0) train = "—";
                        string co = Str(e, "company_name"); if (co.Length == 0) co = "—";
                        rows.Add((new[] { FmtDate(Str(e, "created_at")), co, who, Carne.Label(code), det, train, Carne.PointsText(p), st },
                                  new Color?[] { null, Theme.Subtle, null, null, Theme.Subtle, Theme.Subtle, status == "annulled" ? Theme.Subtle : Carne.Red, Carne.StatusColor(status) },
                                  Str(e, "id"), status, Str(e, "driver_id"), code));
                    }
                }
                catch { }
            }
            SetReviewCount(pending);
            if (onlyCount || _revList == null) return;
            _revList.BeginReload("todas");
            _revList.ClearRows(); _revRows.Clear();
            foreach (var r in rows) { _revRows.Add((r.id, r.status, r.driver, r.code)); _revList.AddRow(r.cells, r.colors, null, r.id); }
            if (rows.Count == 0)
                _revList.SetEmpty(err != null
                    ? (err.IndexOf("PGRST202", StringComparison.OrdinalIgnoreCase) >= 0 ? Tr("El servidor aún no tiene la revisión del superadministrador (falta carne-revision-superadmin.sql).") : Tr("Error: ") + err)
                    : Tr("No hay infracciones."));

            _revList.EndReload();
        }

        void SetReviewCount(int n)
        {
            if (_empSubtabs == null || _empSubtabs.Length <= 6 || _empSubtabs[6] == null) return;
            string txt = Tr(SubNames[6]) + (n > 0 ? "  (" + n + ")" : "");
            if (_empSubtabs[6].Text != txt) { _empSubtabs[6].Text = txt; _empSubtabs[6].Invalidate(); }
        }

        // Superadministrador: aprobar, anular o eliminar CUALQUIER infracción, en cualquier momento (revision-total.sql).
        // Sin ese SQL, se hace lo que se podía antes (B6/B7 pendientes y anular A1, A2 y A3).
        async void AdminInfraction(string action)
        {
            if (_revList == null || !Supa.IsSuperadmin) return;
            int i = _revList.SelectedRow;
            if (i < 0 || i >= _revRows.Count) { Msg(_revMsg, Tr("Selecciona una infracción de la lista."), true); return; }
            var (id, status, driver, code) = _revRows[i];
            if (action == "confirm" && status == "applied") { Msg(_revMsg, Tr("Esta infracción ya está aprobada."), true); return; }
            if (action == "annul" && status == "annulled") { Msg(_revMsg, Tr("Esta infracción ya está anulada."), true); return; }
            string q = action == "confirm" ? Tr("¿Aprobar la infracción? Los puntos quedan restados al maquinista.")
                     : action == "annul" ? Tr("¿Anular la infracción? Se devolverán los puntos al maquinista.")
                     : Tr("¿Eliminar la infracción? Si restó puntos, se devuelven al maquinista y la infracción se borra para siempre.");
            if (MessageBox.Show(this, q, Tr("Revisión"), MessageBoxButtons.YesNo, action == "delete" ? MessageBoxIcon.Warning : MessageBoxIcon.Question) != DialogResult.Yes) return;
            Msg(_revMsg, action == "confirm" ? Tr("Aprobando…") : action == "annul" ? Tr("Anulando…") : Tr("Eliminando…"), false);
            var (_, err) = await Supa.RpcAsync("admin_set_infraction", new { p_id = id, p_action = action });
            if (err != null && err.IndexOf("PGRST202", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                // servidor sin revision-total.sql
                if (action == "delete") { Msg(_revMsg, Tr("El servidor aún no permite eliminar infracciones (falta revision-total.sql)."), true); return; }
                if (status == "pending") { ReviewInfractionCore(action == "confirm", ask: false); return; }
                if (action == "annul" && IsCodeA(code)) (_, err) = await Supa.RpcAsync("annul_infraction", new { p_id = id });
                else { Msg(_revMsg, Tr("El servidor aún no permite cambiar esta infracción (falta revision-total.sql)."), true); return; }
            }
            if (err != null) { Msg(_revMsg, Tr("Error: ") + err, true); return; }
            Msg(_revMsg, action == "confirm" ? Tr("Infracción aprobada.") : action == "annul" ? Tr("Infracción anulada: se han devuelto los puntos.") : Tr("Infracción eliminada."), false);
            LoadReview();
            if (_empSel != null) LoadMembers(_empSel);   // columna PUNTOS de Socios
        }

        void ReviewInfraction(bool confirm) => ReviewInfractionCore(confirm, ask: true);

        async void ReviewInfractionCore(bool confirm, bool ask)
        {
            if (_revList == null) return;
            int i = _revList.SelectedRow;
            if (i < 0 || i >= _revRows.Count) { Msg(_revMsg, Tr("Selecciona una infracción de la lista."), true); return; }
            var (id, status, driver, code) = _revRows[i];
            if (!Supa.IsSuperadmin) return;
            // A1, A2 y A3: no hay nada que confirmar (ya están aplicadas), pero se pueden anular.
            if (IsCodeA(code) && status != "pending")
            {
                if (confirm) { Msg(_revMsg, Tr("Las A1, A2 y A3 ya están aplicadas: solo se pueden anular."), true); return; }
                if (status == "annulled") { Msg(_revMsg, Tr("Esta infracción ya está anulada."), true); return; }
                if (MessageBox.Show(this, Tr("¿Anular la infracción? Se devolverán los puntos al maquinista. El servicio, que no se registró, no se recupera."),
                                    Tr("Revisión"), MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                Msg(_revMsg, Tr("Anulando…"), false);
                var (_, errA) = await Supa.RpcAsync("annul_infraction", new { p_id = id });
                if (errA != null)
                {
                    Msg(_revMsg, errA.IndexOf("PGRST202", StringComparison.OrdinalIgnoreCase) >= 0
                        ? Tr("El servidor aún no permite anular las A1, A2 y A3 (falta revision-infracciones-a.sql).") : Tr("Error: ") + errA, true);
                    return;
                }
                Msg(_revMsg, Tr("Infracción anulada: se han devuelto los puntos."), false);
                LoadReview();
                if (_empSel != null) LoadMembers(_empSel);
                return;
            }
            if (status != "pending") { Msg(_revMsg, Tr("Esta infracción ya está revisada."), true); return; }
            string q = confirm ? Tr("¿Confirmar la infracción? Los puntos siguen restados al maquinista.")
                               : Tr("¿Anular la infracción? Se devolverán los puntos al maquinista.");
            if (ask && MessageBox.Show(this, q, Tr("Revisión"), MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            Msg(_revMsg, confirm ? Tr("Confirmando…") : Tr("Anulando…"), false);
            var (_, err) = await Supa.RpcAsync("review_infraction", new { p_id = id, p_confirm = confirm });
            if (err != null) { Msg(_revMsg, Tr("Error: ") + err, true); return; }
            Msg(_revMsg, confirm ? Tr("Infracción confirmada.") : Tr("Infracción anulada: se han devuelto los puntos."), false);
            LoadReview();
            if (_empSel != null) LoadMembers(_empSel);   // columna PUNTOS de Socios
        }

        // ---------------------------------------------------------------- Socios: puntos
        readonly Dictionary<string, (int points, DateTime? until)> _memberPoints = new(StringComparer.OrdinalIgnoreCase);

        async Task LoadMemberPoints(string companyId)
        {
            _memberPoints.Clear();
            var (json, err) = await Supa.RpcAsync("company_license_points", new { p_company = companyId });
            if (err != null) return;
            try
            {
                using var d = JsonDocument.Parse(json);
                foreach (var e in d.RootElement.EnumerateArray())
                    _memberPoints[Str(e, "user_id")] = Carne.ParseLicense(e);
            }
            catch { }
        }

        (string text, Color? color) MemberPointsCell(string userId)
        {
            if (!_memberPoints.TryGetValue(userId ?? "", out var p) || p.points < 0) return ("—", Theme.Subtle);
            if (p.until != null) return (Tr("Suspendido"), Carne.Red);
            return (p.points + " / " + Carne.MaxPoints, Carne.PointsColor(p.points));
        }
    }
}
