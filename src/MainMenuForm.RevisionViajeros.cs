// Revisión → «Viajeros», «Plazas» y «Por empresa» (solo el administrador; revision-viajeros.sql): servicios con viajeros
// o plazas que no cuadran en los últimos 30 días, para ver si una empresa o un maquinista los ha falseado.
//  · Viajeros: cada servicio con sus motivos (declara más viajeros que los que suben en sus paradas, más viajeros·km que
//    plazas × km, ocupación del 95 %, viajeros en un tren sin plazas, recuperado del archivo del ordenador…) y su
//    riesgo; a la derecha sus paradas (suben, bajan y la suma frente a lo declarado) y su recorrido en el mapa.
//  · Plazas: el mismo tren (.con), maquinista y plazas, cuando lleva más plazas que el mismo tren en los servicios de
//    otros usuarios, que antes, que sus archivos o que el tren de la flota, o demasiadas por coche.
//  · Por empresa: servicios, marcados, viajeros por km frente a la media y alertas de plazas.
// Acciones: anular (annul_service: el dinero sale de la tesorería y al maquinista le llega un aviso), marcar como
// revisado (deja de salir en «Sin revisar») y ver todos los servicios del maquinista.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SelectOR
{
    public sealed class PaxRevItem
    {
        public int Kind;   // 0 servicio (Viajeros) · 1 tren (Plazas) · 2 empresa
        public string Id = "", DriverId = "", Driver = "", Company = "", CompanyId = "", When = "", Route = "", Consist = "", Reviewer = "";
        public readonly List<string> Ids = new();
        public double Km, Pax, PaxClaimed, PaxKm, Cap = double.NaN, CapFiles = double.NaN, Net;
        public int DurS, Cars, Services;
        public bool Recovered, Reviewed, StopsKnown;
        public bool? Checked;
        public readonly List<(string s, string t, int b, int a)> Stops = new();
        public readonly List<(char sev, string text)> Reasons = new();
        public int Score;
        public string First = "", Last = "";
        // por empresa
        public int Flagged, High, SeatAlerts;
        public double Ppk = double.NaN, VsAvg = double.NaN;
        // mapa del recorrido
        public ServiceResultDialog.TripMap Map;
        public int MapState;   // 0 sin pedir · 1 preparando · 2 listo · 3 sin recorrido
        // 2 alto · 1 medio · 0 bajo
        public int Risk => Reasons.Any(r => r.sev == 'h') || Score >= 5 ? 2 : Score >= 2 ? 1 : 0;
        internal string Search;
        public string SearchText => Search ??= string.Join(" ", Driver, Company, Route, Consist, When, string.Join(" ", Reasons.Select(r => r.text))).ToLowerInvariant();
    }

    // Tarjetas de Viajeros y Plazas, y filas de «Por empresa».
    public sealed class PaxRevList : CardListBase
    {
        readonly Font _fTitle = Theme.Font(10f, FontStyle.Bold), _fText = Theme.Font(9f), _fPill = Theme.Font(8.25f, FontStyle.Bold),
                      _fHead = Theme.Font(8.25f, FontStyle.Bold), _fSmall = Theme.Font(8.25f);
        public static readonly Color Hi = Color.FromArgb(255, 138, 128), Mid = Color.FromArgb(255, 209, 102), Lo = Color.FromArgb(143, 214, 148);
        public static Color RiskColor(int r) => r == 2 ? Hi : r == 1 ? Mid : Lo;
        public static string RiskName(int r) => I18n.T(r == 2 ? "Riesgo alto" : r == 1 ? "Riesgo medio" : "Riesgo bajo");
        public static Color SevColor(char s) => s == 'h' ? Hi : s == 'm' ? Mid : Lo;

        int _mode;
        public int Mode { get => _mode; set { if (_mode == value) return; _mode = value; Relayout(); } }
        protected override int MinCardW => Theme.Px(420);
        protected override int MaxCols => 1;
        protected override int CardH => Theme.Px(_mode == 2 ? 44 : 124);
        protected override int Gap => Theme.Px(_mode == 2 ? 4 : 8);
        protected override int TopOffset => _mode == 2 ? Theme.Px(30) : 0;

        protected override void Dispose(bool disposing)
        {
            if (disposing) foreach (var f in new[] { _fTitle, _fText, _fPill, _fHead, _fSmall }) f.Dispose();
            base.Dispose(disposing);
        }

        // columnas de «Por empresa» (fracciones del ancho)
        static readonly float[] CoCols = { 0.32f, 0.12f, 0.15f, 0.13f, 0.15f, 0.13f };
        static readonly string[] CoHeads = { "Empresa", "Servicios 30 d", "Marcados", "Viajeros/km", "Frente a la media", "Plazas" };
        IEnumerable<Rectangle> CoCells(Rectangle rc)
        {
            float x = rc.X + Theme.Px(14), w = rc.Width - Theme.Px(28);
            foreach (var f in CoCols) { yield return new Rectangle((int)x, rc.Y, (int)(w * f) - Theme.Px(6), rc.Height); x += w * f; }
        }

        protected override void AfterPaint(Graphics g, int first, int last)
        {
            if (_mode != 2 || Items.Count == 0) return;
            var hr = new Rectangle(Theme.Px(2), 0, CardW, Theme.Px(28));
            using (var b = new SolidBrush(BackColor)) g.FillRectangle(b, 0, 0, ClientSize.Width, hr.Bottom);
            int k = 0;
            foreach (var c in CoCells(hr)) TextRenderer.DrawText(g, I18n.T(CoHeads[k++]), _fHead, c, Theme.Subtle, L1);
            using (var p = new Pen(Theme.Border)) g.DrawLine(p, hr.X, hr.Bottom - 1, hr.Right, hr.Bottom - 1);
        }

        protected override void PaintCard(Graphics g, int i, Rectangle rc, bool selected, bool hover)
        {
            if (Items[i] is not PaxRevItem it) return;
            var es = System.Globalization.CultureInfo.GetCultureInfo("es-ES");
            if (it.Kind == 2)
            {
                Fill(g, rc, Theme.Px(8), selected || hover ? Color.FromArgb(50, 54, 57) : Theme.Surface);
                if (selected) Stroke(g, rc, Theme.Px(8), Theme.Accent);
                var cells = CoCells(rc).ToArray();
                TextRenderer.DrawText(g, it.Company, _fTitle, cells[0], Theme.Text, L1);
                TextRenderer.DrawText(g, it.Services.ToString("N0", es), _fText, cells[1], Theme.Text, L1);
                double pct = it.Services > 0 ? 100.0 * it.Flagged / it.Services : 0;
                string fl = it.Flagged == 0 ? "0" : $"{it.Flagged:N0} ({pct:0} %)";
                TextRenderer.DrawText(g, fl, _fText, cells[2], it.High > 0 ? Hi : it.Flagged > 0 ? Mid : Theme.Text, L1);
                TextRenderer.DrawText(g, double.IsNaN(it.Ppk) ? "—" : it.Ppk.ToString("N1", es), _fText, cells[3], Theme.Text, L1);
                string vs = double.IsNaN(it.VsAvg) ? "—" : (it.VsAvg > 0 ? "+" : "") + it.VsAvg.ToString("N0", es) + " %";
                TextRenderer.DrawText(g, vs, _fText, cells[4], !double.IsNaN(it.VsAvg) && it.VsAvg >= 100 ? Hi : !double.IsNaN(it.VsAvg) && it.VsAvg >= 50 ? Mid : Theme.Text, L1);
                string sa = it.SeatAlerts == 0 ? "—" : it.SeatAlerts == 1 ? I18n.T("1 alerta") : string.Format(I18n.T("{0} alertas"), it.SeatAlerts);
                TextRenderer.DrawText(g, sa, _fText, cells[5], it.SeatAlerts > 0 ? Mid : Theme.Subtle, L1);
                return;
            }

            int risk = it.Risk; var rcol = RiskColor(risk);
            Fill(g, rc, Theme.Px(10), selected || hover ? Color.FromArgb(50, 54, 57) : Theme.Surface);
            if (selected) Stroke(g, rc, Theme.Px(10), rcol);
            using (var sb = new SolidBrush(it.Reviewed ? Theme.Subtle : rcol)) g.FillRectangle(sb, rc.X, rc.Y + Theme.Px(12), Theme.Px(3), rc.Height - Theme.Px(24));
            int pad = Theme.Px(14), x = rc.X + pad + Theme.Px(2), y = rc.Y + Theme.Px(10), right = rc.Right - pad;

            // riesgo · título · fecha (o «Revisado»)
            string rn = RiskName(risk);
            int pw = TW(rn, _fPill) + Theme.Px(16), ph = Theme.Px(20);
            var pill = new Rectangle(x, y + 1, pw, ph);
            Fill(g, pill, Theme.Px(6), Color.FromArgb(55, rcol));
            TextRenderer.DrawText(g, rn, _fPill, pill, rcol, C1);
            string when = it.Kind == 0 ? it.When : string.Format(I18n.T("{0} servicios · {1}"), it.Services, it.Last);
            if (it.Reviewed) when = I18n.T("Revisado") + (it.Reviewer.Length > 0 ? " · " + it.Reviewer : "") + "  ·  " + when;
            int ww = Math.Min(TW(when, _fSmall) + Theme.Px(4), (right - x) / 2);
            TextRenderer.DrawText(g, when, _fSmall, new Rectangle(right - ww, y, ww, Theme.Px(22)), it.Reviewed ? Lo : Theme.Subtle, R1);
            string title = it.Kind == 0 ? it.Driver + " · " + it.Company : "«" + it.Consist + "» · " + it.Driver;
            int tx = pill.Right + Theme.Px(10);
            TextRenderer.DrawText(g, title, _fTitle, new Rectangle(tx, y, right - ww - Theme.Px(10) - tx, Theme.Px(22)), Theme.Text, L1);

            // datos del servicio o del tren
            string sub;
            if (it.Kind == 0)
            {
                var parts = new List<string>();
                if (it.Route.Length > 0) parts.Add(it.Route);
                parts.Add(it.Consist + (double.IsNaN(it.Cap) ? "" : " (" + string.Format(I18n.T("{0} plazas"), it.Cap.ToString("N0", es)) + ")"));
                parts.Add(it.Km.ToString("N1", es) + " km");
                if (it.DurS > 0) parts.Add(Math.Round(it.DurS / 60.0).ToString("N0", es) + " min");
                parts.Add(string.Format(I18n.T("{0} viajeros"), it.Pax.ToString("N0", es)));
                sub = string.Join(" · ", parts);
            }
            else
                sub = it.Company + " · " + string.Format(I18n.T("{0} plazas"), it.Cap.ToString("N0", es))
                      + (it.Cars > 0 ? " · " + string.Format(I18n.T("{0} vehículos"), it.Cars) : "")
                      + " · " + I18n.T("neto") + " " + it.Net.ToString("N0", es) + " €";
            TextRenderer.DrawText(g, sub, _fText, new Rectangle(x, y + Theme.Px(24), right - x, Theme.Px(20)), Theme.Subtle, L1);

            // motivos (3 como mucho; los demás, «+N»)
            int ry = y + Theme.Px(48), n = Math.Min(3, it.Reasons.Count);
            for (int k = 0; k < n; k++)
            {
                var (sev, text) = it.Reasons[k];
                string t = text + (k == n - 1 && it.Reasons.Count > n ? "   " + string.Format(I18n.T("(+{0} más)"), it.Reasons.Count - n) : "");
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var b = new SolidBrush(SevColor(sev))) g.FillEllipse(b, x + Theme.Px(2), ry + Theme.Px(7), Theme.Px(6), Theme.Px(6));
                g.SmoothingMode = SmoothingMode.None;
                TextRenderer.DrawText(g, t, _fText, new Rectangle(x + Theme.Px(14), ry, right - x - Theme.Px(14), Theme.Px(20)), Theme.Text, L1);
                ry += Theme.Px(19);
            }
        }
    }

    // Detalle de lo elegido: paradas y mapa (Viajeros) o los motivos y las cifras del tren (Plazas).
    public sealed class PaxRevDetail : Control
    {
        readonly Font _fTitle = Theme.Font(10.5f, FontStyle.Bold), _fText = Theme.Font(9f), _fBold = Theme.Font(9f, FontStyle.Bold),
                      _fHead = Theme.Font(8.25f, FontStyle.Bold), _fSmall = Theme.Font(8.25f);
        public PaxRevItem Item;
        public PaxRevDetail()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) foreach (var f in new[] { _fTitle, _fText, _fBold, _fHead, _fSmall }) f.Dispose();
            base.Dispose(disposing);
        }
        const TextFormatFlags L1 = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
        const TextFormatFlags R1 = L1 | TextFormatFlags.Right;
        const TextFormatFlags Wrap = TextFormatFlags.NoPadding | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix;

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(Theme.Bg);
            var card = new Rectangle(0, 0, Width - 1, Height - 1);
            g.SmoothingMode = SmoothingMode.AntiAlias; Theme.FillRound(g, card, Theme.Px(10), Theme.Surface); g.SmoothingMode = SmoothingMode.None;
            var es = System.Globalization.CultureInfo.GetCultureInfo("es-ES");
            int pad = Theme.Px(14), x = pad, w = Width - 2 * pad, y = Theme.Px(12);
            var it = Item;
            if (it == null || it.Kind == 2)
            {
                TextRenderer.DrawText(g, I18n.T("Elige un servicio de la lista."), _fText, new Rectangle(x, y, w, Theme.Px(40)), Theme.Subtle, Wrap);
                return;
            }
            if (it.Kind == 0)
            {
                TextRenderer.DrawText(g, string.Format(I18n.T("Servicio de {0} · {1}"), it.Driver, it.When), _fTitle, new Rectangle(x, y, w, Theme.Px(22)), Theme.Text, L1);
                y += Theme.Px(24);
                string sub = it.StopsKnown ? (it.Stops.Count == 0 ? I18n.T("Sin paradas con viajeros") : I18n.T("Paradas declaradas"))
                                           : I18n.T("Sin paradas anotadas");
                TextRenderer.DrawText(g, sub, _fSmall, new Rectangle(x, y, w, Theme.Px(18)), Theme.Subtle, L1);
                y += Theme.Px(22);
                // mapa abajo (40 % del alto) y la tabla en lo que queda
                int mapH = Math.Max(Theme.Px(120), (int)(Height * 0.40));
                var mapR = new Rectangle(x, Height - Theme.Px(12) - mapH, w, mapH);
                int tableBottom = mapR.Top - Theme.Px(10);
                int c1 = x, c2 = x + (int)(w * 0.52), c3 = x + (int)(w * 0.70), c4 = x + (int)(w * 0.85), rowH = Theme.Px(21);
                void Row(string a, string b, string c, string d, Font f, Color col, Color colC)
                {
                    TextRenderer.DrawText(g, a, f, new Rectangle(c1, y, c2 - c1 - Theme.Px(6), rowH), col, L1);
                    TextRenderer.DrawText(g, b, f, new Rectangle(c2, y, c3 - c2 - Theme.Px(6), rowH), col, L1);
                    TextRenderer.DrawText(g, c, f, new Rectangle(c3, y, c4 - c3 - Theme.Px(6), rowH), colC, R1);
                    TextRenderer.DrawText(g, d, f, new Rectangle(c4, y, x + w - c4, rowH), col, R1);
                    y += rowH;
                    using var p = new Pen(Theme.Surface2); g.DrawLine(p, x, y - 1, x + w, y - 1);
                }
                if (it.StopsKnown && it.Stops.Count > 0)
                {
                    Row(I18n.T("Estación"), I18n.T("Hora"), I18n.T("Suben"), I18n.T("Bajan"), _fHead, Theme.Subtle, Theme.Subtle);
                    int room = Math.Max(1, (tableBottom - y) / rowH - 3);
                    int shown = Math.Min(it.Stops.Count, it.Stops.Count > room ? room - 1 : room);
                    for (int k = 0; k < shown; k++)
                    {
                        var s = it.Stops[k];
                        Row(s.s, s.t, s.b.ToString("N0", es), s.a.ToString("N0", es), _fText, Theme.Text, Theme.Text);
                    }
                    if (shown < it.Stops.Count) Row(string.Format(I18n.T("… y {0} paradas más"), it.Stops.Count - shown), "", "", "", _fSmall, Theme.Subtle, Theme.Subtle);
                    int sum = it.Stops.Sum(s => s.b);
                    Row(I18n.T("Suma"), "", sum.ToString("N0", es), "", _fBold, Theme.Subtle, it.Pax > sum * 1.05 + 2 || it.PaxClaimed > sum ? PaxRevList.Hi : PaxRevList.Lo);
                }
                string decl = string.Format(I18n.T("Viajeros registrados: {0}"), it.Pax.ToString("N0", es));
                if (it.PaxClaimed > it.Pax) decl += "  ·  " + string.Format(I18n.T("declaraba {0}"), it.PaxClaimed.ToString("N0", es));
                if (!double.IsNaN(it.Cap)) decl += "  ·  " + string.Format(I18n.T("{0} plazas"), it.Cap.ToString("N0", es));
                if (!double.IsNaN(it.CapFiles) && Math.Abs(it.CapFiles - it.Cap) >= 1) decl += " " + string.Format(I18n.T("({0} en sus archivos)"), it.CapFiles.ToString("N0", es));
                if (y < tableBottom - Theme.Px(18))
                    TextRenderer.DrawText(g, decl, _fText, new Rectangle(x, y + Theme.Px(4), w, Theme.Px(20)), it.PaxClaimed > it.Pax ? PaxRevList.Hi : Theme.Text, L1);

                // el recorrido
                if (it.Map != null) ServiceResultDialog.PaintTripMap(g, mapR, it.Map, false, 1f);
                else
                {
                    using (var bp = Theme.Round(mapR, Theme.Px(8))) using (var b = new SolidBrush(Color.FromArgb(30, 34, 38))) { g.SmoothingMode = SmoothingMode.AntiAlias; g.FillPath(b, bp); g.SmoothingMode = SmoothingMode.None; }
                    string note = it.MapState == 3 ? I18n.T("Sin recorrido guardado para este servicio.") : I18n.T("Preparando el mapa…");
                    TextRenderer.DrawText(g, note, _fText, mapR, Theme.Subtle, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                }
                return;
            }

            // Plazas
            TextRenderer.DrawText(g, "«" + it.Consist + "»", _fTitle, new Rectangle(x, y, w, Theme.Px(22)), Theme.Text, L1);
            y += Theme.Px(24);
            TextRenderer.DrawText(g, it.Driver + " · " + it.Company, _fText, new Rectangle(x, y, w, Theme.Px(20)), Theme.Subtle, L1);
            y += Theme.Px(30);
            foreach (var (sev, text) in it.Reasons)
            {
                int h = TextRenderer.MeasureText(g, text, _fText, new Size(w - Theme.Px(16), 0), Wrap).Height;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var b = new SolidBrush(PaxRevList.SevColor(sev))) g.FillEllipse(b, x + Theme.Px(2), y + Theme.Px(6), Theme.Px(6), Theme.Px(6));
                g.SmoothingMode = SmoothingMode.None;
                TextRenderer.DrawText(g, text, _fText, new Rectangle(x + Theme.Px(14), y, w - Theme.Px(14), h + 2), Theme.Text, Wrap);
                y += h + Theme.Px(6);
            }
            y += Theme.Px(10);
            void Line(string a, string b)
            {
                TextRenderer.DrawText(g, a, _fText, new Rectangle(x, y, w / 2, Theme.Px(20)), Theme.Subtle, L1);
                TextRenderer.DrawText(g, b, _fBold, new Rectangle(x + w / 2, y, w - w / 2, Theme.Px(20)), Theme.Text, R1);
                y += Theme.Px(22);
            }
            Line(I18n.T("Plazas con las que ha cobrado"), it.Cap.ToString("N0", es));
            Line(I18n.T("Servicios con estas plazas"), it.Services.ToString("N0", es));
            Line(I18n.T("Desde · hasta"), it.First + " · " + it.Last);
            Line(I18n.T("Neto de esos servicios"), it.Net.ToString("N0", es) + " €");
        }
    }

    public partial class MainMenuForm
    {
        const int PaxRevDays = 30;
        Panel _prPanel, _prRight; FlowLayoutPanel _prFilterBar; PaxRevList _prList; PaxRevDetail _prDetail; Label _prMsg;
        RoundButton _prAnnul, _prMark, _prAll; TableLayoutPanel _prSplit; Label _revIntro; FlowLayoutPanel _revTabBar; RoundedInput _revSearch;
        readonly List<PaxRevItem> _prPax = new(), _prSeats = new(), _prCos = new();
        string _prErr, _prCoErr; int _prFilter; bool _prLoading, _prLoaded, _prCoLoaded;

        static readonly string RevIntroInfr = "Todas las infracciones del carné de todos los usuarios. En cualquier momento puedes aprobarlas (los puntos quedan restados), anularlas (se devuelven los puntos) o eliminarlas (se devuelven los puntos y se borran). Los excesos de velocidad (B6 y B7) llegan pendientes de revisión.";
        static readonly string RevIntroPax = "Servicios de los últimos 30 días con viajeros o plazas que no cuadran, del riesgo más alto al más bajo. Desde la 1.2.64 el servidor ya no acepta más viajeros que los que suben en las paradas ni más de los que caben; aquí salen los que lo intentaron y los de antes.";

        // Las pestañas nuevas, dentro de Revisión (las llama BuildReviewSubpanel).
        Panel BuildPaxReviewPanel()
        {
            _prPanel = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Visible = false };
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Theme.Bg };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _prFilterBar = MakeSubTabs(new[] { "Sin revisar", "Riesgo alto", "Revisados" }, i => { _prFilter = i; FillPaxReview(); });
            _prFilterBar.Dock = DockStyle.None; _prFilterBar.AutoSize = true; _prFilterBar.Height = 38;
            t.Controls.Add(_prFilterBar, 0, 0);   // (filas fijas: oculta en «Por empresa», la lista no sube a su fila)

            _prSplit = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Theme.Bg, Margin = new Padding(0) };
            _prSplit.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
            _prSplit.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
            _prSplit.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _prList = new PaxRevList { Dock = DockStyle.Fill, Margin = new Padding(2, 4, 8, 4) };
            _prList.SelectedIndexChanged += (s, e) => PaxRevSelected();
            _prList.ItemActivated += o => { if (o is PaxRevItem it && it.Kind == 2) PaxRevOpenCompany(it); };
            _prSplit.Controls.Add(_prList, 0, 0);

            _prRight = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Margin = new Padding(4, 4, 2, 4) };
            var rt = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Theme.Bg };
            rt.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            rt.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            rt.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _prDetail = new PaxRevDetail { Dock = DockStyle.Fill, Margin = new Padding(0) };
            rt.Controls.Add(_prDetail, 0, 0);
            var btns = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, BackColor = Theme.Bg, Margin = new Padding(0), Dock = DockStyle.Fill };
            _prAnnul = EmpButton(Tr("Anular servicio"));
            _prAnnul.BaseColor = Color.FromArgb(60, 44, 44); _prAnnul.HoverColor = Color.FromArgb(150, 60, 60); _prAnnul.TextColor = Color.FromArgb(229, 115, 115);
            _prAnnul.Click += (s, e) => PaxRevAnnul();
            _prMark = EmpButton(Tr("Marcar como revisado"), primary: true);
            _prMark.Click += (s, e) => PaxRevMark();
            _prAll = EmpButton(Tr("Ver todos sus servicios"));
            _prAll.Click += (s, e) => PaxRevDriverServices();
            foreach (var b in new[] { _prMark, _prAnnul, _prAll }) { b.Margin = new Padding(0, 10, 8, 2); btns.Controls.Add(b); }
            _prMsg = EmpMsg(); _prMsg.MaximumSize = new Size(520, 0);
            btns.Controls.Add(_prMsg);
            rt.Controls.Add(btns, 0, 1);
            _prRight.Controls.Add(rt);
            _prSplit.Controls.Add(_prRight, 1, 0);
            t.Controls.Add(_prSplit, 0, 1);
            _prPanel.Controls.Add(t);
            PaxRevButtons();
            return _prPanel;
        }

        // Al elegir una pestaña de Revisión (5 Viajeros · 6 Plazas · 7 Por empresa; el resto, infracciones).
        void RevTabChanged(int i)
        {
            bool mine = i >= 5;
            if (_prPanel != null) _prPanel.Visible = mine;
            if (_revList != null) _revList.Visible = !mine;
            if (_revIntro != null) _revIntro.Text = Tr(mine ? RevIntroPax : RevIntroInfr);
            if (!mine) return;
            _prList.Mode = i == 5 ? 0 : i == 6 ? 1 : 2;
            _prFilterBar.Visible = i != 7;
            _prRight.Visible = i != 7;
            _prSplit.ColumnStyles[0].Width = i == 7 ? 100 : 55;
            _prSplit.ColumnStyles[1].Width = i == 7 ? 0 : 45;
            if (!_prLoaded) LoadPaxReview();
            if (i == 7 && !_prCoLoaded) LoadPaxByCompany();
            FillPaxReview();
        }

        // Pasa a otra pestaña de Revisión como si se hubiera pulsado.
        void RevSelectTab(int i)
        {
            if (_revTabBar == null) return;
            int k = 0;
            foreach (Control c in _revTabBar.Controls) if (c is RoundButton b) { b.Active = k == i; b.Invalidate(); k++; }
            _revTab = i; RevTabChanged(i); FillReviewCards();
        }

        // «Viajeros (7)» y «Plazas (3)»: los que quedan sin revisar.
        void PaxRevTabCounts()
        {
            if (_revTabBar == null) return;
            var tabs = _revTabBar.Controls.OfType<RoundButton>().ToList();
            if (tabs.Count < 7) return;
            void Set(RoundButton b, string name, int n)
            {
                string txt = Tr(name) + (n > 0 ? "  (" + n + ")" : "");
                if (b.Text == txt) return;
                b.Text = txt;
                using var f = Theme.Font(9.75f, FontStyle.Bold);
                b.Width = TextRenderer.MeasureText(txt, f).Width + 34; b.Invalidate();
            }
            Set(tabs[5], "Viajeros", _prPax.Count(p => !p.Reviewed));
            Set(tabs[6], "Plazas", _prSeats.Count(p => !p.Reviewed));
        }

        static bool MissingSql(string err) => err != null && (err.IndexOf("PGRST202", StringComparison.OrdinalIgnoreCase) >= 0
                                                               || err.IndexOf("Could not find the function", StringComparison.OrdinalIgnoreCase) >= 0);

        async void LoadPaxReview()
        {
            if (!Supa.IsSuperadmin || _prLoading) return;
            _prLoading = true;
            try
            {
                _prList.EmptyText = Tr("Cargando…"); _prList.Invalidate();
                var tp = Supa.RpcAsync("admin_pax_review", new { p_days = PaxRevDays });
                var ts = Supa.RpcAsync("admin_seats_review", new { p_days = PaxRevDays });
                var (jp, ep) = await tp; var (js, es) = await ts;
                string err = ep ?? es;
                _prErr = err == null ? null : MissingSql(err) ? Tr("El servidor aún no tiene la revisión de viajeros y plazas (falta revision-viajeros.sql).") : Tr("Error: ") + err;
                var pax = new List<PaxRevItem>(); var seats = new List<PaxRevItem>();
                if (ep == null) try { using var d = JsonDocument.Parse(jp); foreach (var e in d.RootElement.EnumerateArray()) pax.Add(ParsePaxRev(e)); } catch { }
                if (es == null) try { using var d = JsonDocument.Parse(js); foreach (var e in d.RootElement.EnumerateArray()) seats.Add(ParseSeatRev(e)); } catch { }
                // lo que estaba abierto sigue con su mapa
                var maps = _prPax.Where(p => p.Map != null || p.MapState == 3).ToDictionary(p => p.Id, p => (p.Map, p.MapState));
                foreach (var p in pax) if (maps.TryGetValue(p.Id, out var m)) { p.Map = m.Map; p.MapState = m.MapState; }
                _prPax.Clear(); _prPax.AddRange(pax); _prSeats.Clear(); _prSeats.AddRange(seats);
                _prLoaded = err == null;
                PaxRevTabCounts();
                FillPaxReview();
            }
            finally { _prLoading = false; }
        }

        async void LoadPaxByCompany()
        {
            if (!Supa.IsSuperadmin) return;
            var (j, err) = await Supa.RpcAsync("admin_pax_by_company", new { p_days = PaxRevDays });
            var rows = new List<PaxRevItem>();
            _prCoErr = err == null ? null : MissingSql(err) ? Tr("El servidor aún no tiene la revisión de viajeros y plazas (falta revision-viajeros.sql).") : Tr("Error: ") + err;
            if (err == null)
                try
                {
                    using var d = JsonDocument.Parse(j);
                    foreach (var e in d.RootElement.EnumerateArray())
                        rows.Add(new PaxRevItem
                        {
                            Kind = 2, Id = Str(e, "company_id"), CompanyId = Str(e, "company_id"), Company = Str(e, "company_name"),
                            Services = (int)Num(e, "services"), Flagged = (int)Num(e, "flagged"), High = (int)Num(e, "high"),
                            Ppk = NumOrNaN(e, "pax_per_km"), VsAvg = NumOrNaN(e, "vs_avg"), SeatAlerts = (int)Num(e, "seat_alerts")
                        });
                }
                catch { }
            _prCos.Clear(); _prCos.AddRange(rows);
            _prCoLoaded = err == null;
            if (_revTab == 7) FillPaxReview();
        }

        static double NumOrNaN(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : double.NaN;

        PaxRevItem ParsePaxRev(JsonElement e)
        {
            var it = new PaxRevItem
            {
                Kind = 0, Id = Str(e, "id"), CompanyId = Str(e, "company_id"), Company = Str(e, "company_name"), DriverId = Str(e, "driver_id"),
                Driver = Str(e, "username"), When = FmtDate(Str(e, "started_at")), Route = Str(e, "route"), Consist = Str(e, "consist"),
                Km = Num(e, "km"), DurS = (int)Num(e, "duration_s"), Pax = Num(e, "pax"), PaxClaimed = Num(e, "pax_claimed"), PaxKm = Num(e, "pax_km"),
                Cap = NumOrNaN(e, "capacity"), CapFiles = NumOrNaN(e, "capacity_files"), Cars = (int)Num(e, "cars"), Net = Num(e, "net"),
                Recovered = e.TryGetProperty("recovered", out var rc) && rc.ValueKind == JsonValueKind.True,
                Checked = e.TryGetProperty("checked", out var ck) && ck.ValueKind != JsonValueKind.Null ? ck.ValueKind == JsonValueKind.True : null,
                Score = (int)Num(e, "score"), Reviewed = e.TryGetProperty("reviewed", out var rv) && rv.ValueKind == JsonValueKind.True,
                Reviewer = Str(e, "reviewer")
            };
            if (it.Driver.Length == 0) it.Driver = "—";
            if (e.TryGetProperty("stops", out var st) && st.ValueKind == JsonValueKind.Array)
            {
                it.StopsKnown = true;
                foreach (var x in st.EnumerateArray())
                {
                    if (x.ValueKind != JsonValueKind.Object) continue;
                    it.Stops.Add((Str(x, "s"), Str(x, "t"), (int)Math.Max(0, Num(x, "b")), (int)Math.Max(0, Num(x, "a"))));
                }
            }
            if (e.TryGetProperty("reasons", out var rs) && rs.ValueKind == JsonValueKind.Array)
                foreach (var r in rs.EnumerateArray()) { var t = PaxReasonText(r); if (t != null) it.Reasons.Add((SevOf(r), t)); }
            SortReasons(it);
            return it;
        }

        PaxRevItem ParseSeatRev(JsonElement e)
        {
            var it = new PaxRevItem
            {
                Kind = 1, Consist = Str(e, "consist"), DriverId = Str(e, "driver_id"), Driver = Str(e, "username"), CompanyId = Str(e, "company_id"),
                Company = Str(e, "company_name"), Cap = Num(e, "capacity"), Services = (int)Num(e, "services"), Net = Num(e, "net"),
                Cars = (int)Num(e, "cars"), First = FmtDate(Str(e, "first_at")), Last = FmtDate(Str(e, "last_at")),
                Score = (int)Num(e, "score"), Reviewed = e.TryGetProperty("reviewed", out var rv) && rv.ValueKind == JsonValueKind.True
            };
            if (it.Driver.Length == 0) it.Driver = "—";
            if (e.TryGetProperty("ids", out var ids) && ids.ValueKind == JsonValueKind.Array)
                foreach (var x in ids.EnumerateArray()) if (x.ValueKind == JsonValueKind.String) it.Ids.Add(x.GetString());
            it.Id = it.Consist + "|" + it.DriverId + "|" + it.CompanyId + "|" + it.Cap.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (e.TryGetProperty("reasons", out var rs) && rs.ValueKind == JsonValueKind.Array)
                foreach (var r in rs.EnumerateArray()) { var t = SeatReasonText(r); if (t != null) it.Reasons.Add((SevOf(r), t)); }
            SortReasons(it);
            return it;
        }

        static char SevOf(JsonElement r) { string v = Str(r, "v"); return v.Length > 0 ? v[0] : 'l'; }
        static void SortReasons(PaxRevItem it)
        {
            var sorted = it.Reasons.OrderBy(r => r.sev == 'h' ? 0 : r.sev == 'm' ? 1 : 2).ToList();
            it.Reasons.Clear(); it.Reasons.AddRange(sorted);
        }

        static string N0(JsonElement r, string p) => Num(r, p).ToString("N0", EsEs);

        // El motivo, en palabras (Viajeros).
        string PaxReasonText(JsonElement r)
        {
            switch (Str(r, "c"))
            {
                case "clamped": return string.Format(Tr("Declaraba {0} viajeros; el servidor solo aceptó {1} (los que suben en sus paradas y caben)"), N0(r, "claimed"), N0(r, "pax"));
                case "sum_b": return string.Format(Tr("Declara {0} viajeros, pero sus paradas suman {1}"), N0(r, "pax"), N0(r, "sum"));
                case "cap_stops": return string.Format(Tr("Más viajeros ({0}) que plazas × paradas con subidas ({1} × {2})"), N0(r, "pax"), N0(r, "cap"), N0(r, "n"));
                case "paxkm_cap": return string.Format(Tr("Más viajeros·km que plazas × km: {0} frente a {1}"), N0(r, "paxkm"), N0(r, "max"));
                case "nocap": return string.Format(Tr("{0} viajeros en un tren sin plazas o de mercancías"), N0(r, "pax"));
                case "occ":
                    return string.Format(Tr("Ocupación media del {0} %"), N0(r, "occ"))
                           + (r.TryGetProperty("med", out var m) && m.ValueKind == JsonValueKind.Number ? " " + string.Format(Tr("(lo normal en esta ruta: {0} %)"), m.GetDouble().ToString("N0", EsEs)) : "");
                case "nostops": return Tr("Viajeros sin ninguna parada anotada");
                case "busy": return string.Format(Tr("{0} paradas en {1} min"), N0(r, "n"), N0(r, "min"));
                case "same": return string.Format(Tr("La misma estación ({0}) {1} veces"), Str(r, "s"), N0(r, "n"));
                case "trail": return string.Format(Tr("{0} paradas que no están en su recorrido"), N0(r, "n"));
                case "ratio": return string.Format(Tr("Viajeros por km {0} veces lo normal en esta ruta con trenes parecidos ({1} servicios)"), Num(r, "x").ToString("N1", EsEs), N0(r, "n"));
                case "recovered": return Tr("Servicio recuperado del archivo del ordenador");
                case "legacy": return Tr("Registrado con un SelectOR anterior a la 1.2.64 (sin comprobar las paradas)");
                default: return null;
            }
        }

        // El motivo, en palabras (Plazas).
        string SeatReasonText(JsonElement r)
        {
            switch (Str(r, "c"))
            {
                case "others": return string.Format(Tr("Lleva {0} plazas; el mismo tren en otros {1} servicios de {2} usuarios: {3}"), N0(r, "cap"), N0(r, "n"), N0(r, "users"), N0(r, "mode"));
                case "jump": return string.Format(Tr("Antes este maquinista lo llevaba con {0} plazas; ahora con {1}"), N0(r, "before"), N0(r, "now"));
                case "files": return string.Format(Tr("Ha cobrado con {0} plazas; los archivos del propio maquinista dicen {1}"), N0(r, "cap"), N0(r, "files"));
                case "company":
                    double s = Num(r, "seats"), f = Num(r, "files");
                    return string.Format(Tr("La empresa ha fijado {0} plazas; los archivos del tren dicen {1} (+{2} %)"), N0(r, "seats"), N0(r, "files"), f > 0 ? Math.Round(100 * (s / f - 1)).ToString("N0", EsEs) : "—");
                case "fleet": return string.Format(Tr("El tren de la flota tiene {0} plazas; aquí lleva {1}"), N0(r, "unit"), N0(r, "cap"));
                case "per_car": return string.Format(Tr("{0} plazas por vehículo: no es creíble"), N0(r, "per"));
                default: return null;
            }
        }

        // Tarjetas según la pestaña, el filtro y la búsqueda.
        void FillPaxReview()
        {
            if (_prList == null || _revTab < 5) return;
            var words = _revQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string keep = (_prList.SelectedItem as PaxRevItem)?.Id;
            var src = _revTab == 5 ? _prPax : _revTab == 6 ? _prSeats : _prCos;
            _prList.BeginUpdate();
            _prList.Items.Clear();
            foreach (var it in src)
            {
                if (it.Kind != 2)
                {
                    bool ok = _prFilter switch { 1 => !it.Reviewed && it.Risk == 2, 2 => it.Reviewed, _ => !it.Reviewed };
                    if (!ok) continue;
                }
                bool match = true;
                string st = it.Kind == 2 ? it.Company.ToLowerInvariant() : it.SearchText;
                foreach (var w in words) if (st.IndexOf(w, StringComparison.Ordinal) < 0) { match = false; break; }
                if (match) _prList.Items.Add(it);
            }
            string err = _revTab == 7 ? _prCoErr : _prErr;
            _prList.EmptyText = err ?? (_revTab == 7 ? (_prCoLoaded ? Tr("Ningún servicio en los últimos 30 días.") : Tr("Cargando…"))
                                      : !_prLoaded ? Tr("Cargando…")
                                      : _prFilter == 2 ? Tr("Nada revisado todavía.")
                                      : _revTab == 5 ? Tr("Ningún servicio con viajeros que no cuadren.") : Tr("Ningún tren con plazas que no cuadren."));
            _prList.EndUpdate();
            if (keep != null)
                for (int k = 0; k < _prList.Items.Count; k++) if (((PaxRevItem)_prList.Items[k]).Id == keep) { _prList.SelectedIndex = k; break; }
            if (_prList.SelectedItem == null && _prList.Items.Count > 0 && _revTab != 7) _prList.SelectedIndex = 0;
            PaxRevSelected();
        }

        void PaxRevSelected()
        {
            var it = _prList?.SelectedItem as PaxRevItem;
            if (_prDetail == null) return;
            _prDetail.Item = it?.Kind == 2 ? null : it;
            _prDetail.Invalidate();
            PaxRevButtons();
            if (it != null && it.Kind == 2 && _revTab == 7) return;
            if (it != null && it.Kind == 0 && it.MapState == 0) _ = PaxRevMap(it);
        }

        void PaxRevButtons()
        {
            if (_prAnnul == null) return;
            var it = _prList?.SelectedItem as PaxRevItem;
            bool seats = it?.Kind == 1;
            void Txt(RoundButton b, string t)
            {
                if (b.Text == t) return;
                b.Text = t;
                using var f = Theme.Font(b.FontSize, FontStyle.Bold);
                b.Width = Math.Max(Theme.Px(150), TextRenderer.MeasureText(t, f).Width + Theme.Px(40)); b.Invalidate();
            }
            Txt(_prAnnul, seats ? (it.Ids.Count == 1 ? Tr("Anular el servicio") : string.Format(Tr("Anular sus {0} servicios"), it.Ids.Count)) : Tr("Anular servicio"));
            Txt(_prMark, it?.Reviewed == true ? Tr("Quitar de revisados") : seats ? Tr("Es correcto: marcar como revisado") : Tr("Marcar como revisado"));
            Txt(_prAll, seats ? Tr("Ver sus servicios") : Tr("Ver todos sus servicios"));
            foreach (var b in new[] { _prAnnul, _prMark, _prAll }) b.Enabled = it != null && it.Kind != 2;
        }

        // El recorrido del servicio (y el detalle de la vía si la ruta está instalada).
        async Task PaxRevMap(PaxRevItem it)
        {
            it.MapState = 1;
            try
            {
                var (j, err) = await Supa.RpcAsync("admin_service_trail", new { p_service = it.Id });
                ServiceResultDialog.TripMap m = null;
                if (err == null && !string.IsNullOrWhiteSpace(j))
                    try { using var d = JsonDocument.Parse(j); m = ParseTrail(d.RootElement); } catch { }
                if (m != null)
                {
                    var ruta = _routesAll.Find(x => string.Equals(x.Name, it.Route, StringComparison.OrdinalIgnoreCase));
                    if (ruta != null) try { m.Detail = await TripDetailFor(ruta.Path); } catch { }
                }
                it.Map = m; it.MapState = m != null ? 2 : 3;
            }
            catch { it.MapState = 3; }
            if (_prDetail?.Item == it) _prDetail.Invalidate();
        }

        // «Anular servicio» / «Anular sus N servicios»
        async void PaxRevAnnul()
        {
            if (!Supa.IsSuperadmin || _prList?.SelectedItem is not PaxRevItem it || it.Kind == 2) return;
            var ids = it.Kind == 0 ? new List<string> { it.Id } : it.Ids.ToList();
            if (ids.Count == 0) return;
            string label = it.Kind == 0
                ? string.Format(Tr("Servicio de {0} · {1} · {2}. Motivo de la anulación (lo verá el maquinista):"), it.Driver, it.Route, it.When)
                : string.Format(Tr("{0} servicios de {1} con «{2}» y {3} plazas. Motivo de la anulación (lo verá el maquinista):"), ids.Count, it.Driver, it.Consist, it.Cap.ToString("N0", EsEs));
            string reason;
            using (var dlg = new TextPromptDialog(Tr("Anular servicio"), label, "",
                       it.Kind == 0 ? Tr("Por ejemplo: viajeros que no cuadran con sus paradas") : Tr("Por ejemplo: plazas del tren falseadas"), Tr("Anular")))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                reason = (dlg.Value ?? "").Trim();
            }
            if (reason.Length < 3) { Msg(_prMsg, Tr("Escribe el motivo de la anulación."), true); return; }
            Msg(_prMsg, Tr("Anulando…"), false);
            var (j, err) = await Supa.RpcAsync("admin_annul_services", new { p_ids = ids, p_reason = reason });
            if (err != null) { Msg(_prMsg, MissingSql(err) ? Tr("El servidor aún no tiene la revisión de viajeros y plazas (falta revision-viajeros.sql).") : Tr("Error: ") + err, true); return; }
            int n = 0; double removed = 0;
            try { using var d = JsonDocument.Parse(j); n = (int)Num(d.RootElement, "annulled"); removed = Num(d.RootElement, "removed"); } catch { }
            Msg(_prMsg, n == 1 ? string.Format(Tr("Servicio anulado ({0} € fuera de la tesorería). El maquinista ha recibido un aviso."), removed.ToString("N0", EsEs))
                               : string.Format(Tr("{0} servicios anulados ({1} € fuera de la tesorería). El maquinista ha recibido un aviso."), n, removed.ToString("N0", EsEs)), false);
            _prCoLoaded = false;
            LoadPaxReview();
            if (_revTab == 7) LoadPaxByCompany();
            LoadCompanies(soft: true);
        }

        // «Marcar como revisado» / «Quitar de revisados»
        async void PaxRevMark()
        {
            if (!Supa.IsSuperadmin || _prList?.SelectedItem is not PaxRevItem it || it.Kind == 2) return;
            var ids = it.Kind == 0 ? new List<string> { it.Id } : it.Ids.ToList();
            bool undo = it.Reviewed;
            var (_, err) = await Supa.RpcAsync("admin_mark_reviewed", new { p_kind = it.Kind == 0 ? "pax" : "seats", p_ids = ids, p_note = (string)null, p_undo = undo });
            if (err != null) { Msg(_prMsg, MissingSql(err) ? Tr("El servidor aún no tiene la revisión de viajeros y plazas (falta revision-viajeros.sql).") : Tr("Error: ") + err, true); return; }
            it.Reviewed = !undo; it.Reviewer = undo ? "" : Supa.Username ?? "";
            Msg(_prMsg, undo ? Tr("Vuelve a «Sin revisar».") : Tr("Marcado como revisado."), false);
            _prCoLoaded = false;
            PaxRevTabCounts();
            FillPaxReview();
        }

        // «Por empresa» → doble clic: sus servicios en Viajeros.
        void PaxRevOpenCompany(PaxRevItem co)
        {
            if (_revSearch != null) _revSearch.Box.Text = co.Company;   // (filtra con la búsqueda)
            RevSelectTab(5);
        }

        // «Ver todos sus servicios»: los del maquinista (90 días), para comparar.
        async void PaxRevDriverServices()
        {
            if (!Supa.IsSuperadmin || _prList?.SelectedItem is not PaxRevItem it || it.Kind == 2 || string.IsNullOrEmpty(it.DriverId)) return;
            Msg(_prMsg, Tr("Cargando…"), false);
            var (j, err) = await Supa.RpcAsync("admin_driver_services", new { p_driver = it.DriverId, p_days = 90 });
            if (err != null) { Msg(_prMsg, MissingSql(err) ? Tr("El servidor aún no tiene la revisión de viajeros y plazas (falta revision-viajeros.sql).") : Tr("Error: ") + err, true); return; }
            Msg(_prMsg, "", false);
            var rows = new List<DriverSvcList.Row>();
            try
            {
                using var d = JsonDocument.Parse(j);
                foreach (var e in d.RootElement.EnumerateArray())
                    rows.Add(new DriverSvcList.Row
                    {
                        Id = Str(e, "id"), When = FmtDate(Str(e, "started_at")), Company = Str(e, "company_name"), Route = Str(e, "route"), Consist = Str(e, "consist"),
                        Km = Num(e, "km"), Min = Num(e, "duration_s") / 60.0, Pax = Num(e, "pax"), PaxClaimed = Num(e, "pax_claimed"), Cap = NumOrNaN(e, "capacity"),
                        Net = Num(e, "net"), Stops = (int)Num(e, "stops"),
                        Recovered = e.TryGetProperty("recovered", out var r) && r.ValueKind == JsonValueKind.True,
                        Annulled = e.TryGetProperty("annulled", out var a) && a.ValueKind == JsonValueKind.True
                    });
            }
            catch { }
            var mark = new HashSet<string>(it.Kind == 0 ? new[] { it.Id } : it.Ids, StringComparer.OrdinalIgnoreCase);
            using var dlg = new Form
            {
                Text = Tr("Servicios de ") + it.Driver, StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.Sizable,
                MinimizeBox = false, ShowInTaskbar = false, ClientSize = new Size(Theme.Px(820), Theme.Px(560))
            };
            DialogKit.Dress(dlg);
            var list = new DriverSvcList { Dock = DockStyle.Fill, Marked = mark, EmptyText = Tr("Ningún servicio en los últimos 90 días.") };
            list.Items.AddRange(rows);
            var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(Theme.Px(16), Theme.Px(10), Theme.Px(16), Theme.Px(10)), BackColor = Theme.Bg };
            host.Controls.Add(list);
            var foot = new DialogKit.Footer();
            var close = DialogKit.Primary(Tr("Cerrar"), Theme.Px(130)); close.Click += (s, e) => dlg.Close();
            foot.AddRight(close);
            dlg.Controls.Add(host); dlg.Controls.Add(foot);
            double sumPax = rows.Where(r => !r.Annulled).Sum(r => r.Pax), sumKm = rows.Where(r => !r.Annulled && r.Pax > 0).Sum(r => r.Km);
            string sub = string.Format(Tr("{0} servicios en 90 días · {1} viajeros por km"), rows.Count, sumKm > 0 ? (sumPax / sumKm).ToString("N1", EsEs) : "—");
            DialogKit.Crown(dlg, Tr("Servicios de ") + it.Driver, sub, "🧾");
            list.Relayout();
            dlg.ShowDialog(this);
        }
    }

    // Lista de los servicios de un maquinista («Ver todos sus servicios»): los que están en revisión, resaltados.
    public sealed class DriverSvcList : CardListBase
    {
        public sealed class Row { public string Id, When, Company, Route, Consist; public double Km, Min, Pax, PaxClaimed, Cap, Net; public int Stops; public bool Recovered, Annulled; }
        public HashSet<string> Marked = new();
        readonly Font _fTitle = Theme.Font(9.5f, FontStyle.Bold), _fText = Theme.Font(9f), _fPill = Theme.Font(8f, FontStyle.Bold);
        protected override int MinCardW => Theme.Px(400);
        protected override int MaxCols => 1;
        protected override int CardH => Theme.Px(52);
        protected override int Gap => Theme.Px(4);
        protected override void Dispose(bool disposing) { if (disposing) { _fTitle.Dispose(); _fText.Dispose(); _fPill.Dispose(); } base.Dispose(disposing); }
        protected override void PaintCard(Graphics g, int i, Rectangle rc, bool selected, bool hover)
        {
            if (Items[i] is not Row r) return;
            var es = System.Globalization.CultureInfo.GetCultureInfo("es-ES");
            bool mk = r.Id != null && Marked.Contains(r.Id);
            Fill(g, rc, Theme.Px(8), selected || hover ? Color.FromArgb(50, 54, 57) : Theme.Surface);
            if (mk) Stroke(g, rc, Theme.Px(8), PaxRevList.Hi);
            int x = rc.X + Theme.Px(12), right = rc.Right - Theme.Px(12), y = rc.Y + Theme.Px(6);
            // a la derecha: anulado / recuperado
            int px = right;
            void Pill(string t, Color c)
            {
                int w = TW(t, _fPill) + Theme.Px(14), h = Theme.Px(18);
                var pr = new Rectangle(px - w, y + 2, w, h); px -= w + Theme.Px(6);
                Fill(g, pr, h / 2, Color.FromArgb(50, c));
                TextRenderer.DrawText(g, t, _fPill, pr, c, C1);
            }
            if (r.Annulled) Pill(I18n.T("Anulado"), Theme.Subtle);
            if (r.Recovered) Pill(I18n.T("Recuperado"), PaxRevList.Mid);
            if (mk) Pill(I18n.T("En revisión"), PaxRevList.Hi);
            TextRenderer.DrawText(g, r.When + " · " + r.Company + (string.IsNullOrEmpty(r.Route) ? "" : " · " + r.Route), _fTitle,
                new Rectangle(x, y, px - x - Theme.Px(6), Theme.Px(20)), r.Annulled ? Theme.Subtle : Theme.Text, L1);
            string pax = r.Pax.ToString("N0", es) + (double.IsNaN(r.Cap) || r.Cap <= 0 ? "" : " / " + r.Cap.ToString("N0", es)) + " " + I18n.T("viajeros/plazas");
            if (r.PaxClaimed > r.Pax) pax += " (" + string.Format(I18n.T("declaraba {0}"), r.PaxClaimed.ToString("N0", es)) + ")";
            string l2 = r.Consist + " · " + r.Km.ToString("N1", es) + " km · " + Math.Round(r.Min).ToString("N0", es) + " min · " + pax
                        + " · " + string.Format(I18n.T("{0} paradas"), r.Stops) + " · " + I18n.T("neto") + " " + r.Net.ToString("N0", es) + " €";
            TextRenderer.DrawText(g, l2, _fText, new Rectangle(x, y + Theme.Px(22), right - x, Theme.Px(20)), Theme.Subtle, L1);
        }
    }
}
