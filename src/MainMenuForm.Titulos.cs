// Mi perfil → TÍTULOS (certificación, 2026-11-08; servidor: sql/certificacion.sql). Dos tarjetas:
//   · 🎓 Maquinista (niveles B, A, A+): el examen (20 preguntas al azar, se corrige en el servidor) y la práctica, que se
//     cuenta sola con los servicios (lo que llevas frente a lo que pide cada nivel) y el carné.
//   · 👮 Jefe de circulación: el título de maquinista (B), su examen (15 preguntas) y las horas con el mando en la sala.
//     Con el examen aprobado, «en prácticas»: ya puedes tomar el mando en el Puesto de Mando.
// El examen se hace en su ventana (CertExam): el tiempo lo lleva el servidor; si cierras, sigue corriendo y puedes volver.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SelectOR
{
    public partial class MainMenuForm
    {
        // (2026-11-08, «un toque más temático») cada título como una credencial ferroviaria: franja de color con su nombre, el
        // sello con el nivel (dorado si lo tienes), el avance como una línea con sus estaciones y los requisitos con luces de
        // señal (verde: cumplido · ámbar: a medias · rojo: pendiente). CertCard lo dibuja todo; el botón del examen va dentro.
        Control _certSection;
        CertCard _certDrvCard, _certJefCard;

        Control BuildTitlesSection()
        {
            var wrap = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 1, AutoSize = true, BackColor = Theme.Bg, Margin = new Padding(0), Visible = false };
            wrap.Controls.Add(EmpHeader("TÍTULOS"));
            var row = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, RowCount = 1, AutoSize = true, BackColor = Theme.Bg, Margin = new Padding(0, 0, 0, 10) };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            _certDrvCard = new CertCard(Tr("CERTIFICADO DE MAQUINISTA"), Color.FromArgb(27, 94, 52), Color.FromArgb(46, 125, 79), () => _ = TakeExam("driver")) { Dock = DockStyle.Top, Margin = new Padding(0, 0, 7, 0) };
            _certJefCard = new CertCard(Tr("HABILITACIÓN DE JEFE DE CIRCULACIÓN"), Color.FromArgb(28, 58, 94), Color.FromArgb(43, 90, 138), () => _ = TakeExam("jefe")) { Dock = DockStyle.Top, Margin = new Padding(7, 0, 0, 0) };
            row.Controls.Add(_certDrvCard, 0, 0); row.Controls.Add(_certJefCard, 1, 0);
            wrap.Controls.Add(row);
            _certSection = wrap;
            return wrap;
        }

        static string CertWhy(string s) => s switch
        {
            "carne" => Tr("tu carné está por debajo del mínimo de puntos o retirado"),
            "sancion" => Tr("tienes una sanción C2 o C3 reciente"),
            "admin" => Tr("lo ha suspendido el administrador"),
            _ => s
        };

        async Task LoadTitlesAsync()
        {
            if (_certSection == null || !Supa.IsLoggedIn) return;
            var (json, err) = await Supa.RpcAsync("cert_status", new { });
            if (err != null) { _certSection.Visible = false; return; }   // (sin certificacion.sql: no se enseña)
            JsonElement r;
            try { r = JsonDocument.Parse(json).RootElement; } catch { return; }
            _certSection.Visible = true;
            var cfg = r.GetProperty("cfg"); var pr = r.GetProperty("practice");
            string lvl = Str(r, "driver"), jef = Str(r, "jefe"), sus = Str(r, "suspended");
            bool drvExam = Str(r, "driver_exam_at").Length > 0, jefExam = Str(r, "jefe_exam_at").Length > 0;
            int qd = 0, qj = 0;
            if (r.TryGetProperty("questions", out var qn)) { qd = (int)Num(qn, "driver"); qj = (int)Num(qn, "jefe"); }
            JsonElement last = r.TryGetProperty("last", out var la) ? la : default;

            // ¿un examen abierto, o hay que esperar para repetir?
            (string action, string note) ExamState(string exam, bool passed, int questions, bool allowed, string notAllowedWhy)
            {
                if (passed) return (null, null);
                if (last.ValueKind == JsonValueKind.Object && last.TryGetProperty(exam, out var a))
                {
                    bool open = Str(a, "submitted").Length == 0 && DateTime.TryParse(Str(a, "expires"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var ex) && ex > DateTime.UtcNow;
                    if (open) return ("resume", Tr("Tienes un examen empezado: el tiempo sigue corriendo."));
                    if (Str(a, "retry_at").Length > 0 && DateTime.TryParse(Str(a, "retry_at"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var rt) && rt > DateTime.UtcNow)
                        return (null, string.Format(Tr("Último intento: {0} de {1}. Podrás repetirlo a partir del {2}."), (int)Num(a, "score"), (int)Num(a, "total"), rt.ToLocalTime().ToString("g", I18n.Culture)));
                }
                if (sus.Length > 0) return (null, string.Format(Tr("No puedes examinarte ahora: {0}."), CertWhy(sus)));
                if (!allowed) return (null, notAllowedWhy);
                if (questions < (exam == "driver" ? (int)Num(cfg, "driver_count") : (int)Num(cfg, "jefe_count"))) return (null, Tr("El examen aún no está preparado."));
                return ("start", null);
            }
            static int Lamp(double have, double need) => need <= 0 || have >= need ? 2 : have >= need / 2 ? 1 : 0;

            // ── maquinista: el sello con el nivel, la línea B → A → A+ y los requisitos del siguiente nivel
            var lv = cfg.GetProperty("levels");
            int srv = (int)Num(pr, "services"), kinds = (int)Num(pr, "kinds"), room = (int)Num(pr, "room"), pts = (int)Num(pr, "points");
            double km = Num(pr, "km");
            string[] names = { "B", "A", "A+" };
            int reached = Array.IndexOf(names, lvl);   // −1: ninguno
            var d = _certDrvCard;
            d.Medal = lvl.Length > 0 ? lvl : "—"; d.MedalOn = lvl.Length > 0; d.MedalCaption = Tr("NIVEL");
            d.Suspended = sus.Length > 0 && lvl.Length > 0 ? string.Format(Tr("Suspendido: {0}."), CertWhy(sus)) : null;
            d.State = lvl.Length > 0 ? string.Format(Tr("Maquinista nivel {0}"), lvl) : drvExam ? Tr("Examen aprobado: falta la práctica") : Tr("Sin título");
            d.Stations = new List<(string, bool)> { (Tr("Examen"), drvExam) };
            for (int i = 0; i < 3; i++) d.Stations.Add((names[i], reached >= i));
            d.Reqs = new List<(int, string)>();
            if (!drvExam) d.Reqs.Add((0, string.Format(Tr("Examen: {0} preguntas al azar, aprueba con {1} ({2} min)"), (int)Num(cfg, "driver_count"), (int)Num(cfg, "driver_pass"), (int)Num(cfg, "minutes"))));
            string next = reached < 2 ? names[reached + 1] : null;
            if (next != null && lv.TryGetProperty(next, out var q))
            {
                d.ReqTitle = string.Format(Tr("PARA EL NIVEL {0}"), next);
                int ns = (int)Num(q, "services"), nk = (int)Num(q, "kinds"), nr = (int)Num(q, "room"), np = (int)Num(q, "points"); double nkm = Num(q, "km");
                d.Reqs.Add((Lamp(srv, ns), string.Format(Tr("{0} servicios sin infracciones graves ({1})"), ns, srv)));
                if (nkm > 0) d.Reqs.Add((Lamp(km, nkm), string.Format(Tr("{0} km ({1})"), nkm.ToString("N0", I18n.Culture), km.ToString("N0", I18n.Culture))));
                if (nk >= 2) d.Reqs.Add((Lamp(kinds, nk), Tr("viajeros y mercancías") + " (" + (kinds >= 2 ? "✓" : Tr(kinds == 0 ? "ninguno" : (pr.TryGetProperty("pax", out var px) && px.ValueKind == JsonValueKind.True ? "solo viajeros" : "solo mercancías"))) + ")"));
                if (nr > 0) d.Reqs.Add((Lamp(room, nr), string.Format(Tr("{0} en la sala compartida ({1})"), nr, room)));
                if (np > 0) d.Reqs.Add((pts >= np ? 2 : 0, string.Format(Tr("carné de {0} puntos ({1})"), np, pts)));
            }
            else d.ReqTitle = next == null && reached == 2 ? Tr("NIVEL MÁS ALTO ALCANZADO") : null;
            var (da, dn) = ExamState("driver", drvExam, qd, true, null);
            d.Note = dn;
            d.Button = da == null ? null : da == "resume" ? Tr("Seguir con el examen") : Tr("Hacer el examen");
            d.Refresh2();

            // ── jefe de circulación: el sello, la línea Maquinista → Examen → Prácticas → Título y las horas
            double hrs = Num(r, "jefe_seconds") / 3600.0, needH = Num(cfg, "jefe_hours");
            bool titled = jef == "titulo";
            var j = _certJefCard;
            j.Medal = "👮"; j.MedalOn = titled; j.MedalCaption = titled ? Tr("TITULADO") : jef == "practicas" ? Tr("PRÁCTICAS") : Tr("SIN TÍTULO");
            j.Suspended = sus.Length > 0 && (jef ?? "").Length > 0 ? string.Format(Tr("Suspendido: {0}."), CertWhy(sus)) : null;
            j.State = titled ? Tr("Jefe de circulación") : jef == "practicas" ? Tr("En prácticas") : Tr("Sin título");
            j.Stations = new List<(string, bool)> { (Tr("Maquinista"), lvl.Length > 0), (Tr("Examen"), jefExam || titled), (Tr("Prácticas"), titled || hrs >= needH), (Tr("Título"), titled) };
            j.ReqTitle = titled ? null : Tr("REQUISITOS");
            j.Reqs = titled ? new List<(int, string)>() : new List<(int, string)>
            {
                (lvl.Length > 0 ? 2 : 0, Tr("Título de maquinista (nivel B)")),
                (jefExam ? 2 : 0, jefExam ? Tr("Examen aprobado") : string.Format(Tr("Examen: {0} preguntas al azar, aprueba con {1} ({2} min)"), (int)Num(cfg, "jefe_count"), (int)Num(cfg, "jefe_pass"), (int)Num(cfg, "minutes"))),
                (Lamp(hrs, needH), string.Format(Tr("{0} de {1} h con el mando en la sala compartida"), hrs.ToString("N1", I18n.Culture), needH.ToString("N0", I18n.Culture))),
            };
            var (ja, jn) = ExamState("jefe", jefExam || titled, qj, lvl.Length > 0, Tr("Primero necesitas el título de maquinista (nivel B)."));
            j.Note = jn ?? (jef == "practicas" && sus.Length == 0 ? Tr("Ya puedes tomar el mando en el Puesto de Mando: las horas se cuentan solas.") : null);
            j.Button = ja == null ? null : ja == "resume" ? Tr("Seguir con el examen") : Tr("Hacer el examen");
            j.Refresh2();
        }

        // ── la credencial (dibujada entera; el botón del examen, dentro)
        sealed class CertCard : Panel
        {
            readonly string _title; readonly Color _c1, _c2; readonly RoundButton _btn;
            public string Medal = "—", MedalCaption = "", State = "", Suspended, ReqTitle, Note, Button;
            public bool MedalOn;
            public List<(string label, bool done)> Stations = new();
            public List<(int lamp, string text)> Reqs = new();
            static readonly Color Gold = Color.FromArgb(212, 169, 58), Rail = Color.FromArgb(70, 76, 84);
            const int Band = 44;

            public CertCard(string title, Color c1, Color c2, Action click)
            {
                _title = title; _c1 = c1; _c2 = c2;
                DoubleBuffered = true; BackColor = Theme.Bg; Height = Theme.Px(250);
                _btn = EmpButton("", primary: true); _btn.Width = Theme.Px(200); _btn.Visible = false;
                _btn.Click += (s, e) => click();
                Controls.Add(_btn);
                Resize += (s, e) => Relayout();
            }

            public void Refresh2() { _btn.Visible = Button != null; if (Button != null) _btn.Text = Button; Relayout(); Invalidate(); }

            int P(int v) => Theme.Px(v);
            int ReqH(Graphics g, int w)
            {
                using var f = Theme.Font(9.25f);
                int h = 0;
                foreach (var (_, t) in Reqs) h += Math.Max(P(20), TextRenderer.MeasureText(g, t, f, new Size(w, 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height + P(4));
                return h;
            }

            void Relayout()
            {
                if (Width < 50) return;
                using var g = CreateGraphics();
                int x0 = P(18), w = Width - 2 * x0;
                int y = P(Band) + P(14) + P(96) + P(10);   // franja, sello y la línea
                if (Suspended != null) y += P(24);
                if (ReqTitle != null) y += P(22);
                y += ReqH(g, w - P(22));
                if (Note != null) { using var fn = Theme.Font(8.75f); y += TextRenderer.MeasureText(g, Note, fn, new Size(w, 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height + P(8); }
                if (_btn.Visible) { _btn.Location = new Point(x0, y + P(4)); y += _btn.Height + P(12); }
                y += P(10);
                if (Height != y) Height = y;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
                var all = new Rectangle(0, 0, Width - 1, Height - 1);
                using (var p = Theme.Round(all, 12)) using (var b = new SolidBrush(Theme.Surface)) g.FillPath(b, p);
                // franja de color, con su raya amarilla y negra de vía debajo
                var band = new Rectangle(0, 0, Width - 1, P(Band));
                using (var p = Theme.Round(new Rectangle(0, 0, Width - 1, P(Band) + 12), 12))
                using (var b = new LinearGradientBrush(band, _c1, _c2, LinearGradientMode.Horizontal))
                { var old = g.Clip; g.SetClip(band); g.FillPath(b, p); g.Clip = old; }
                int sy = P(Band);
                for (int x = 0; x < Width; x += P(12))
                    using (var sb = new SolidBrush(x / P(12) % 2 == 0 ? Color.FromArgb(240, 196, 64) : Color.FromArgb(30, 30, 30))) g.FillRectangle(sb, x, sy, P(12), P(3));
                using (var fb = Theme.Font(9.5f, FontStyle.Bold)) TextRenderer.DrawText(g, _title, fb, new Rectangle(P(18), 0, Width - P(130), P(Band)), Color.White, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                using (var fs = Theme.Font(8f, FontStyle.Bold)) TextRenderer.DrawText(g, "SELECTOR · CGL", fs, new Rectangle(Width - P(130), 0, P(116), P(Band)), Color.FromArgb(200, 225, 235, 240), TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.NoPrefix);

                // el sello
                int x0 = P(18), top = P(Band) + P(14), R = P(42);
                var mc = new Rectangle(x0, top, 2 * R, 2 * R);
                using (var b = new SolidBrush(MedalOn ? Color.FromArgb(46, 38, 18) : Theme.Surface2)) g.FillEllipse(b, mc);
                using (var pen = new Pen(MedalOn ? Gold : Rail, P(3))) g.DrawEllipse(pen, mc);
                var inner = Rectangle.Inflate(mc, -P(6), -P(6));
                using (var pen = new Pen(MedalOn ? Color.FromArgb(150, Gold) : Color.FromArgb(120, Rail), 1f) { DashStyle = DashStyle.Dot }) g.DrawEllipse(pen, inner);
                bool emoji = Medal.Length > 0 && char.IsSurrogate(Medal[0]);
                using (var fm = Theme.Font(emoji ? 20f : Medal.Length > 1 ? 20f : 24f, FontStyle.Bold))
                    TextRenderer.DrawText(g, Medal, fm, new Rectangle(mc.X, mc.Y - P(6), mc.Width, mc.Height), MedalOn ? Gold : Theme.Subtle, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                using (var fc = Theme.Font(7f, FontStyle.Bold))
                    TextRenderer.DrawText(g, MedalCaption, fc, new Rectangle(mc.X - P(10), mc.Bottom - P(26), mc.Width + P(20), P(14)), MedalOn ? Gold : Theme.Subtle, TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);

                // el estado y la línea con sus estaciones
                int lx = mc.Right + P(18), lw = Width - lx - P(24);
                using (var fs = Theme.Font(13f, FontStyle.Bold)) TextRenderer.DrawText(g, State, fs, new Point(lx, top), MedalOn ? Gold : Theme.Text, TextFormatFlags.NoPrefix);
                if (Stations.Count > 1 && lw > 60)
                {
                    int ly = top + P(56), n = Stations.Count;
                    float step = (float)lw / (n - 1);
                    using (var rail = new Pen(Rail, P(4))) g.DrawLine(rail, lx, ly, lx + lw, ly);
                    // las traviesas
                    using (var tie = new Pen(Color.FromArgb(55, 60, 66), P(2)))
                        for (float tx = lx; tx <= lx + lw; tx += P(9)) g.DrawLine(tie, tx, ly - P(5), tx, ly + P(5));
                    int lastDone = -1; for (int i = 0; i < n; i++) if (Stations[i].done) lastDone = i;
                    if (lastDone > 0) using (var done = new Pen(_c2.GetBrightness() < 0.45f ? ControlPaint.Light(_c2, 0.6f) : _c2, P(4))) g.DrawLine(done, lx, ly, lx + step * lastDone, ly);
                    using var fl = Theme.Font(8.25f, FontStyle.Bold);
                    for (int i = 0; i < n; i++)
                    {
                        float cx = lx + step * i; int r = P(7);
                        bool dn = Stations[i].done;
                        using (var b = new SolidBrush(dn ? Gold : Theme.Surface)) g.FillEllipse(b, cx - r, ly - r, 2 * r, 2 * r);
                        using (var pen = new Pen(dn ? Gold : Theme.Subtle, P(2))) g.DrawEllipse(pen, cx - r, ly - r, 2 * r, 2 * r);
                        var ts = TextRenderer.MeasureText(g, Stations[i].label, fl);
                        float tx = Math.Max(lx - P(4), Math.Min(lx + lw - ts.Width + P(4), cx - ts.Width / 2f));
                        TextRenderer.DrawText(g, Stations[i].label, fl, new Point((int)tx, ly + P(10)), dn ? Theme.Text : Theme.Subtle, TextFormatFlags.NoPrefix);
                    }
                }

                // suspendido · requisitos con su luz · nota
                int y = top + P(96) + P(10), w = Width - 2 * x0;
                if (Suspended != null)
                {
                    using var f = Theme.Font(9.25f, FontStyle.Bold);
                    TextRenderer.DrawText(g, "⚠  " + Suspended, f, new Rectangle(x0, y, w, P(22)), Carne.Red, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                    y += P(24);
                }
                if (ReqTitle != null)
                {
                    using var f = Theme.Font(8f, FontStyle.Bold);
                    TextRenderer.DrawText(g, ReqTitle, f, new Point(x0, y), Theme.Subtle, TextFormatFlags.NoPrefix);
                    y += P(22);
                }
                using (var f = Theme.Font(9.25f))
                    foreach (var (lamp, t) in Reqs)
                    {
                        var col = lamp == 2 ? Color.FromArgb(76, 175, 80) : lamp == 1 ? Color.FromArgb(255, 179, 0) : Color.FromArgb(229, 57, 53);
                        int r = P(6);
                        // la luz, con su visera y su brillo
                        using (var hood = new SolidBrush(Color.FromArgb(25, 25, 25))) g.FillEllipse(hood, x0 - 2, y + P(3) - 2, 2 * r + 4, 2 * r + 4);
                        using (var b = new SolidBrush(col)) g.FillEllipse(b, x0, y + P(3), 2 * r, 2 * r);
                        using (var glow = new SolidBrush(Color.FromArgb(110, Color.White))) g.FillEllipse(glow, x0 + r / 2, y + P(3) + r / 3, r * 2 / 3, r * 2 / 3);
                        var sz = TextRenderer.MeasureText(g, t, f, new Size(w - P(22), 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                        TextRenderer.DrawText(g, t, f, new Rectangle(x0 + P(22), y, w - P(22), sz.Height + 2), Theme.Text, TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                        y += Math.Max(P(20), sz.Height + P(4));
                    }
                if (Note != null)
                {
                    using var f = Theme.Font(8.75f);
                    var sz = TextRenderer.MeasureText(g, Note, f, new Size(w, 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                    TextRenderer.DrawText(g, Note, f, new Rectangle(x0, y + P(4), w, sz.Height + 2), Theme.Subtle, TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                }
            }
        }

        async Task TakeExam(string exam)
        {
            var (json, err) = await Supa.RpcAsync("cert_exam_start", new { p_exam = exam });
            if (err != null) { ThemedBox.Show(this, CleanRpcErr(err), Tr("Examen"), MessageBoxButtons.OK, MessageBoxIcon.Warning); _ = LoadTitlesAsync(); return; }
            JsonElement r;
            try { r = JsonDocument.Parse(json).RootElement.Clone(); } catch { return; }
            using (var w = new CertExamWindow(r, exam)) w.ShowDialog(this);
            await LoadTitlesAsync();
        }

        // el texto de una excepción de Supabase sin lo técnico («… P0001: texto» → «texto»)
        static string CleanRpcErr(string err)
        {
            if (string.IsNullOrEmpty(err)) return err;
            try
            {
                int k = err.IndexOf('{');
                if (k >= 0) { using var d = JsonDocument.Parse(err.Substring(k)); if (d.RootElement.TryGetProperty("message", out var m)) return m.GetString(); }
            }
            catch { }
            return err;
        }

        // ── la ventana del examen
        sealed class CertExamWindow : Form
        {
            readonly JsonElement _r; readonly string _exam; readonly string _attempt; readonly DateTime _expires; readonly int _pass;
            readonly List<(Label q, RadioButton[] opts)> _qs = new();
            readonly Label _clock, _result; readonly RoundButton _send; readonly Timer _tick;
            bool _done, _sending;

            public CertExamWindow(JsonElement r, string exam)
            {
                _r = r; _exam = exam;
                _attempt = Str(r, "attempt"); _pass = (int)Num(r, "pass");
                DateTime.TryParse(Str(r, "expires_at"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out _expires);
                Text = Tr("Examen"); BackColor = Theme.Bg; ForeColor = Theme.Text; StartPosition = FormStartPosition.CenterParent;
                MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false; KeyPreview = true; Font = Theme.Font(9.5f); FormBorderStyle = FormBorderStyle.FixedDialog;
                DialogKit.Dress(this, false);
                ClientSize = new Size(Theme.Px(820), Theme.Px(720));
                var qs = r.GetProperty("questions");
                var top = new Panel { Dock = DockStyle.Top, Height = Theme.Px(40), BackColor = Theme.Surface };
                _clock = new Label { Dock = DockStyle.Right, Width = Theme.Px(220), TextAlign = ContentAlignment.MiddleRight, Font = Theme.Font(11f, FontStyle.Bold), ForeColor = Carne.Gold, Padding = new Padding(0, 0, 14, 0), BackColor = Theme.Surface };
                var info = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(14, 0, 0, 0), ForeColor = Theme.Subtle, BackColor = Theme.Surface,
                                       Text = string.Format(Tr("{0} preguntas · aprueba con {1} · una sola respuesta correcta en cada una"), qs.GetArrayLength(), _pass) };
                top.Controls.Add(info); top.Controls.Add(_clock);
                var list = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = Theme.Bg, Padding = new Padding(16, 10, 16, 10) };
                try { Native.UseDarkScrollBars(list); } catch { }
                int W = ClientSize.Width - Theme.Px(70);
                string qk = I18n.English ? "q_en" : "q_es", ok = I18n.English ? "options_en" : "options_es";
                foreach (var q in qs.EnumerateArray())
                {
                    int n = (int)Num(q, "n");
                    var box = new Panel { Width = W, AutoSize = true, BackColor = Theme.Bg, Margin = new Padding(0, 0, 0, 12) };
                    var lq = new Label { Text = n + ".  " + Str(q, qk), AutoSize = true, MaximumSize = new Size(W, 0), Font = Theme.Font(10f, FontStyle.Bold), ForeColor = Theme.Text, Location = new Point(0, 0), UseMnemonic = false };
                    box.Controls.Add(lq);
                    int y = lq.PreferredHeight + Theme.Px(6);
                    var opts = new RadioButton[4]; int k = 0;
                    foreach (var o in q.GetProperty(ok).EnumerateArray())
                    {
                        if (k >= 4) break;
                        var rb = new RadioButton { Text = o.GetString(), AutoSize = true, MaximumSize = new Size(W - Theme.Px(20), 0), Location = new Point(Theme.Px(14), y), ForeColor = Theme.Text, BackColor = Theme.Bg, UseMnemonic = false };
                        box.Controls.Add(rb);
                        y += rb.GetPreferredSize(new Size(W - Theme.Px(20), 0)).Height + Theme.Px(4);
                        opts[k++] = rb;
                    }
                    box.Height = y;
                    list.Controls.Add(box);
                    _qs.Add((lq, opts));
                }
                var bottom = new Panel { Dock = DockStyle.Bottom, Height = Theme.Px(64), BackColor = Theme.Surface, Padding = new Padding(16, 10, 16, 10) };
                _send = EmpButton(Tr("Entregar el examen"), primary: true); _send.Width = Theme.Px(200); _send.Dock = DockStyle.Right;
                _send.Click += async (s, e) => { if (_done) Close(); else await Send(false); };
                _result = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Font = Theme.Font(10f, FontStyle.Bold), ForeColor = Theme.Text, BackColor = Theme.Surface, UseMnemonic = false };
                bottom.Controls.Add(_result); bottom.Controls.Add(_send);
                Controls.Add(list); Controls.Add(bottom); Controls.Add(top);
                DialogKit.Crown(this, Tr(exam == "driver" ? "Examen de maquinista" : "Examen de jefe de circulación"), Tr("Elige una respuesta en cada pregunta y entrégalo antes de que acabe el tiempo"), exam == "driver" ? "🎓" : "👮");
                _tick = new Timer { Interval = 500 };
                _tick.Tick += async (s, e) =>
                {
                    var left = _expires - DateTime.UtcNow;
                    if (left <= TimeSpan.Zero) { _clock.Text = "0:00"; if (!_done && !_sending) await Send(true); return; }
                    _clock.Text = "⏱ " + ((int)left.TotalMinutes) + ":" + left.Seconds.ToString("00");
                    _clock.ForeColor = left.TotalMinutes < 2 ? Carne.Red : Carne.Gold;
                };
                _tick.Start();
                FormClosing += (s, e) =>
                {
                    if (_done || _sending) return;
                    if (ThemedBox.Show(this, Tr("El examen no está entregado. El tiempo sigue corriendo aunque cierres: puedes volver desde Mi perfil antes de que se acabe.\n\n¿Cerrar?"),
                                       Tr("Examen"), MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) e.Cancel = true;
                };
                FormClosed += (s, e) => _tick.Stop();
            }

            async Task Send(bool timeUp)
            {
                if (_done || _sending) return;
                int[] ans = _qs.Select(x => Array.FindIndex(x.opts, o => o != null && o.Checked)).ToArray();
                int blank = ans.Count(a => a < 0);
                if (!timeUp && blank > 0 && ThemedBox.Show(this, string.Format(Tr("Te faltan {0} preguntas por contestar. ¿Entregar igual?"), blank), Tr("Examen"), MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                _sending = true; _send.Enabled = false; _result.Text = Tr("Corrigiendo…");
                var (json, err) = await Supa.RpcAsync("cert_exam_submit", new { p_attempt = _attempt, p_answers = ans });
                _sending = false;
                if (err != null) { _send.Enabled = true; _result.ForeColor = Carne.Red; _result.Text = CleanRpcErr(err); return; }
                JsonElement r;
                try { r = JsonDocument.Parse(json).RootElement; } catch { _send.Enabled = true; return; }
                _done = true; _tick.Stop();
                bool passed = r.TryGetProperty("passed", out var pp) && pp.ValueKind == JsonValueKind.True;
                int score = (int)Num(r, "score"), total = (int)Num(r, "total");
                var wrong = new HashSet<int>();
                if (r.TryGetProperty("wrong", out var w) && w.ValueKind == JsonValueKind.Array) foreach (var x in w.EnumerateArray()) wrong.Add(x.GetInt32());
                for (int i = 0; i < _qs.Count; i++)
                {
                    foreach (var o in _qs[i].opts) if (o != null) o.Enabled = false;
                    bool bad = wrong.Contains(i + 1);
                    _qs[i].q.ForeColor = bad ? Carne.Red : Color.FromArgb(129, 199, 132);
                    _qs[i].q.Text = (bad ? "✗ " : "✓ ") + _qs[i].q.Text;
                }
                string retry = Str(r, "retry_at");
                _result.ForeColor = passed ? Color.FromArgb(129, 199, 132) : Carne.Red;
                _result.Text = passed ? string.Format(Tr("¡Aprobado! {0} de {1}."), score, total)
                             : string.Format(Tr("Suspendido: {0} de {1} (hacen falta {2})."), score, total, _pass)
                               + (DateTime.TryParse(retry, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var rt) ? " " + string.Format(Tr("Podrás repetirlo a partir del {0}."), rt.ToLocalTime().ToString("g", I18n.Culture)) : "")
                               + (timeUp ? " " + Tr("Se acabó el tiempo.") : "");
                _send.Text = Tr("Cerrar"); _send.Enabled = true;
            }
        }
    }
}
