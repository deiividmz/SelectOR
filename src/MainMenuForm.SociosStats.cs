// Socios → Estadísticas (gerente, gestores y superadmin): una tarjeta por socio con lo que ha hecho en el
// periodo elegido (servicios, km, horas, neto, viajeros, velocidad media, infracciones, anulados), su
// actividad mes a mes (12 meses) y cuándo condujo por última vez. Filtros: periodo, rol, actividad
// (activos, inactivos, sin servicio en 30 días o nunca), búsqueda y orden.
// Servidor: normas-y-estadisticas.sql.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

namespace SelectOR
{
    public sealed class DriverStat
    {
        public string UserId = "", Name = "", Role = "";
        public DateTime Joined = DateTime.MinValue, First = DateTime.MinValue, Last = DateTime.MinValue, LastEver = DateTime.MinValue;
        public long Services, Pax, Infractions, Annulled;
        public double Km, Hours, Net, Income, AvgKmh;
        public int Points = -1; public DateTime? Suspended;
        public int[] Months = new int[12];          // servicios por mes, del más antiguo (0) al actual (11)
        public DateTime MonthZero;                  // primer mes de Months
    }

    public sealed class DriverStatsList : CardListBase
    {
        static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-ES");
        static readonly Color Gold = Color.FromArgb(240, 196, 90), Blue = Color.FromArgb(120, 144, 226), Red = Color.FromArgb(229, 115, 115), Amber = Color.FromArgb(232, 178, 80);
        readonly Font _fName = Theme.Font(10.5f, FontStyle.Bold), _fVal = Theme.Font(11f, FontStyle.Bold), _fCap = Theme.Font(7.75f, FontStyle.Bold),
                      _fText = Theme.Font(8.75f), _fPill = Theme.Font(8f, FontStyle.Bold), _fTiny = Theme.Font(7.25f);
        public DriverStatsList() { EmptyText = I18n.T("Ningún socio coincide con los filtros."); }
        protected override int MinCardW => Theme.Px(640);
        protected override int MaxCols => 1;
        protected override int CardH => Theme.Px(112);
        protected override int Gap => Theme.Px(8);
        protected override void Dispose(bool disposing)
        {
            if (disposing) foreach (var f in new[] { _fName, _fVal, _fCap, _fText, _fPill, _fTiny }) f.Dispose();
            base.Dispose(disposing);
        }

        public static string RoleText(string r) => I18n.T(r == "owner" ? "Gerente" : r == "manager" ? "Gestor" : r == "former" ? "Antiguo socio" : "Maquinista");
        static Color RoleColor(string r) => r == "owner" ? Gold : r == "manager" ? Blue : r == "former" ? Theme.Subtle : Theme.AccentHi;

        protected override void PaintCard(Graphics g, int i, Rectangle rc, bool selected, bool hover)
        {
            if (Items[i] is not DriverStat d) return;
            Fill(g, rc, Theme.Px(10), selected || hover ? Color.FromArgb(50, 54, 57) : Theme.Surface);
            if (selected) Stroke(g, rc, Theme.Px(10), Theme.Accent);
            bool active = d.Services > 0;
            using (var sb = new SolidBrush(active ? Theme.Accent : Theme.Subtle)) g.FillRectangle(sb, rc.X, rc.Y + Theme.Px(12), Theme.Px(3), rc.Height - Theme.Px(24));
            int pad = Theme.Px(16), x = rc.X + pad, y = rc.Y + Theme.Px(10);

            // Cabecera: nombre · rol · actividad · carné
            int nw = Math.Min(TW(d.Name, _fName), Theme.Px(220));
            TextRenderer.DrawText(g, d.Name, _fName, new Rectangle(x, y, nw + 2, Theme.Px(24)), Theme.Text, L1);
            int px = x + nw + Theme.Px(10);
            void Pill(string t, Color c)
            {
                int w = TW(t, _fPill) + Theme.Px(16), h = Theme.Px(20);
                var r = new Rectangle(px, y + Theme.Px(2), w, h);
                Fill(g, r, h / 2, Color.FromArgb(40, c));
                TextRenderer.DrawText(g, t, _fPill, r, c, C1);
                px += w + Theme.Px(6);
            }
            Pill(RoleText(d.Role), RoleColor(d.Role));
            if (d.Role != "former") Pill(active ? I18n.T("ACTIVO") : I18n.T("INACTIVO"), active ? Theme.AccentHi : Theme.Subtle);
            if (d.Suspended != null) Pill(I18n.T("SUSPENDIDO"), Red);
            else if (d.Points >= 0) Pill(string.Format(I18n.T("{0}/15 puntos"), d.Points), d.Points < 6 ? Red : d.Points < 10 ? Amber : Theme.AccentHi);
            string last = d.LastEver == DateTime.MinValue ? I18n.T("nunca ha conducido en la empresa")
                        : string.Format(I18n.T("último servicio: {0} ({1})"), d.LastEver.ToString("dd/MM/yyyy", Es), Ago(d.LastEver));
            int chartW = Theme.Px(230), chartX = rc.Right - pad - chartW;
            TextRenderer.DrawText(g, last, _fText, new Rectangle(px + Theme.Px(4), y, chartX - px - Theme.Px(12), Theme.Px(24)),
                d.LastEver == DateTime.MinValue || (DateTime.Now - d.LastEver).TotalDays > 30 ? Amber : Theme.Subtle, L1);

            // Cifras del periodo
            var cells = new (string cap, string val, Color col)[]
            {
                (I18n.T("SERVICIOS"), d.Services.ToString("N0", Es), Theme.Text),
                ("KM", d.Km.ToString("N0", Es), Theme.Text),
                (I18n.T("HORAS"), d.Hours.ToString("N1", Es), Theme.Text),
                (I18n.T("NETO"), d.Net.ToString("+#,##0;−#,##0", Es) + " €", d.Net < 0 ? Red : Theme.AccentHi),
                (I18n.T("VIAJEROS"), d.Pax.ToString("N0", Es), Theme.Text),
                (I18n.T("KM/H MEDIA"), d.AvgKmh > 0 ? d.AvgKmh.ToString("N0", Es) : "—", Theme.Text),
                (I18n.T("INFRACCIONES"), d.Infractions.ToString("N0", Es), d.Infractions > 0 ? Red : Theme.Text),
                (I18n.T("ANULADOS"), d.Annulled.ToString("N0", Es), d.Annulled > 0 ? Red : Theme.Text),
            };
            int gy = y + Theme.Px(34), cw = Math.Max(Theme.Px(70), (chartX - x - Theme.Px(10)) / cells.Length);
            for (int k = 0; k < cells.Length; k++)
            {
                int cx = x + k * cw;
                TextRenderer.DrawText(g, cells[k].cap, _fCap, new Rectangle(cx, gy, cw - 4, Theme.Px(16)), Theme.Subtle, L1);
                TextRenderer.DrawText(g, cells[k].val, _fVal, new Rectangle(cx, gy + Theme.Px(16), cw - 4, Theme.Px(24)), cells[k].col, L1);
            }
            string since = d.Joined == DateTime.MinValue ? "" : string.Format(I18n.T("socio desde {0}"), d.Joined.ToString("dd/MM/yyyy", Es));
            if (d.First != DateTime.MinValue) since += (since.Length > 0 ? "  ·  " : "") + string.Format(I18n.T("en el periodo: del {0} al {1}"), d.First.ToString("dd/MM", Es), d.Last.ToString("dd/MM", Es));
            TextRenderer.DrawText(g, since, _fText, new Rectangle(x, gy + Theme.Px(44), chartX - x - Theme.Px(10), Theme.Px(18)), Theme.Subtle, L1);

            // Actividad de los últimos 12 meses (servicios por mes)
            using (var sep = new Pen(Theme.Surface2)) g.DrawLine(sep, chartX - Theme.Px(10), rc.Y + Theme.Px(12), chartX - Theme.Px(10), rc.Bottom - Theme.Px(12));
            TextRenderer.DrawText(g, I18n.T("SERVICIOS POR MES"), _fCap, new Rectangle(chartX, y + Theme.Px(2), chartW, Theme.Px(16)), Theme.Subtle, L1);
            int max = Math.Max(1, d.Months.Max()), bw = chartW / 12, baseY = rc.Bottom - Theme.Px(24), hMax = Theme.Px(52);
            for (int m = 0; m < 12; m++)
            {
                int v = d.Months[m], h = v == 0 ? Theme.Px(2) : Math.Max(Theme.Px(4), hMax * v / max);
                var bar = new Rectangle(chartX + m * bw + 2, baseY - h, bw - 4, h);
                using (var b = new SolidBrush(v == 0 ? Theme.Surface2 : m == 11 ? Theme.AccentHi : Theme.Accent)) g.FillRectangle(b, bar);
                string ml = d.MonthZero.AddMonths(m).ToString(I18n.English ? "MMM" : "MMM", I18n.English ? CultureInfo.GetCultureInfo("en-GB") : Es);
                TextRenderer.DrawText(g, ml.Substring(0, 1).ToUpperInvariant(), _fTiny, new Rectangle(chartX + m * bw, baseY + 2, bw, Theme.Px(14)), Theme.Subtle, C1);
            }
        }

        static string Ago(DateTime t)
        {
            var d = DateTime.Now - t;
            if (d.TotalDays < 1) return I18n.T("hoy");
            if (d.TotalDays < 2) return I18n.T("ayer");
            if (d.TotalDays < 60) return string.Format(I18n.T("hace {0} días"), (int)d.TotalDays);
            return string.Format(I18n.T("hace {0} meses"), (int)(d.TotalDays / 30.4));
        }
    }

    public partial class MainMenuForm
    {
        Panel _statsPage;
        DriverStatsList _statsList;
        Label _statsSummary;
        readonly List<DriverStat> _statsAll = new();
        int _statsPeriod = 0, _statsRole = 0, _statsActivity = 0, _statsOrder = 0, _statsSeq;
        string _statsQuery = "";

        Panel BuildStatsPage()
        {
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, BackColor = Theme.Bg, Visible = false };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int k = 0; k < 3; k++) t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            // Fila 1: periodo + búsqueda
            var r1 = new TableLayoutPanel { Anchor = AnchorStyles.Left | AnchorStyles.Right, Height = 40, ColumnCount = 2, RowCount = 1, BackColor = Theme.Bg, Margin = new Padding(0) };
            r1.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); r1.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var period = ChipBar(new[] { "Este mes", "Mes anterior", "3 meses", "12 meses", "Todo" }, 0, i => { _statsPeriod = i; LoadStats(); });
            var search = new RoundedInput(I18n.T("🔎  Buscar socio…")) { Width = 240, Height = 34, Anchor = AnchorStyles.Right, Margin = new Padding(10, 3, 0, 3) };
            search.Box.TextChanged += (s, e) => { _statsQuery = search.Box.Text.Trim().ToLowerInvariant(); FillStats(); };
            r1.Controls.Add(period, 0, 0); r1.Controls.Add(search, 1, 0);
            t.Controls.Add(r1);

            // Fila 2: rol · actividad · orden
            var r2 = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, BackColor = Theme.Bg, Margin = new Padding(0, 0, 0, 2) };
            r2.Controls.Add(ChipBar(new[] { "Todos los roles", "Gerentes", "Gestores", "Maquinistas", "Antiguos socios" }, 0, i => { _statsRole = i; FillStats(); }));
            var sep = new Label { Text = "·", AutoSize = true, ForeColor = Theme.Subtle, Margin = new Padding(4, 9, 8, 0) };
            r2.Controls.Add(sep);
            r2.Controls.Add(ChipBar(new[] { "Todos", "Activos", "Inactivos", "Sin servicio en 30 días", "Nunca han conducido" }, 0, i => { _statsActivity = i; FillStats(); }));
            var ordLbl = new Label { Text = Tr("Ordenar por"), AutoSize = true, ForeColor = Theme.Subtle, Margin = new Padding(14, 10, 6, 0) };
            var order = NewCombo(); order.Dock = DockStyle.None; order.Width = 170; order.DropDownStyle = ComboBoxStyle.DropDownList; order.Margin = new Padding(0, 5, 0, 0);
            order.Items.AddRange(new object[] { Tr("Servicios"), Tr("Km"), Tr("Neto"), Tr("Horas"), Tr("Última actividad"), Tr("Infracciones"), Tr("Nombre") });
            order.SelectedIndex = 0;
            order.SelectedIndexChanged += (s, e) => { _statsOrder = order.SelectedIndex; FillStats(); };
            r2.Controls.Add(ordLbl); r2.Controls.Add(order);
            t.Controls.Add(r2);

            _statsSummary = new Label { AutoSize = true, ForeColor = Theme.Text, Font = Theme.Font(9.5f), Margin = new Padding(2, 6, 2, 6), MaximumSize = new Size(1200, 0) };
            t.Controls.Add(_statsSummary);
            _statsList = new DriverStatsList { Dock = DockStyle.Fill, Margin = new Padding(2, 2, 2, 4) };
            t.Controls.Add(_statsList);

            _statsPage = t;
            return t;
        }

        FlowLayoutPanel ChipBar(string[] names, int active, Action<int> onClick)
        {
            var bar = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Theme.Bg, Margin = new Padding(0) };
            for (int k = 0; k < names.Length; k++)
            {
                int idx = k;
                var b = BankChip(Tr(names[k]), k == active);
                b.Click += (s, e) => { SetChipActive(bar, idx); onClick(idx); };
                bar.Controls.Add(b);
            }
            return bar;
        }

        (DateTime? from, DateTime? to) StatsRange()
        {
            var m0 = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
            return _statsPeriod switch
            {
                0 => (m0, null),
                1 => (m0.AddMonths(-1), m0),
                2 => (m0.AddMonths(-2), null),
                3 => (m0.AddMonths(-11), null),
                _ => (null, null)
            };
        }

        async void LoadStats()
        {
            var c = _empSel;
            if (c == null || _statsList == null) return;
            int seq = ++_statsSeq;
            _statsList.EmptyText = Tr("Cargando…");
            var (from, to) = StatsRange();
            string Iso(DateTime? d) => d == null ? null : new DateTimeOffset(d.Value).ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
            var tStats = Supa.RpcAsync("company_driver_stats", new { p_company = c.Id, p_from = Iso(from), p_to = Iso(to) });
            var tMonth = Supa.RpcAsync("company_driver_monthly", new { p_company = c.Id, p_months = 12 });
            var (json, err) = await tStats;
            var (mj, me) = await tMonth;
            if (seq != _statsSeq || _empSel?.Id != c.Id) return;
            _statsAll.Clear();
            if (err != null)
            {
                _statsList.EmptyText = err.Contains("PGRST202") ? Tr("El servidor aún no tiene las estadísticas (falta normas-y-estadisticas.sql).") : Tr("Error: ") + err;
                FillStats();
                return;
            }
            var m0 = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(-11);
            var monthly = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (me == null)
                {
                    using var md = JsonDocument.Parse(mj);
                    foreach (var e in md.RootElement.EnumerateArray())
                    {
                        if (!DateTime.TryParse(Str(e, "month"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var mo)) continue;
                        int k = (mo.Year - m0.Year) * 12 + mo.Month - m0.Month;
                        if (k < 0 || k > 11) continue;
                        string uid = Str(e, "user_id");
                        if (!monthly.TryGetValue(uid, out var arr)) monthly[uid] = arr = new int[12];
                        arr[k] += (int)Num(e, "services");
                    }
                }
            }
            catch { }
            try
            {
                using var d = JsonDocument.Parse(json);
                foreach (var e in d.RootElement.EnumerateArray())
                {
                    DateTime Dt(string k) => DateTimeOffset.TryParse(Str(e, k), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var o) ? o.LocalDateTime : DateTime.MinValue;
                    var su = Dt("suspended_until");
                    string uid = Str(e, "user_id");
                    _statsAll.Add(new DriverStat
                    {
                        UserId = uid, Name = Str(e, "username").Length > 0 ? Str(e, "username") : "—", Role = Str(e, "role"),
                        Joined = Dt("joined_at"), First = Dt("first_at"), Last = Dt("last_at"), LastEver = Dt("last_ever"),
                        Services = (long)Num(e, "services"), Km = Num(e, "km"), Hours = Num(e, "hours"), Net = Num(e, "net"),
                        Income = Num(e, "income"), Pax = (long)Num(e, "pax"), AvgKmh = Num(e, "avg_kmh"),
                        Infractions = (long)Num(e, "infractions"), Annulled = (long)Num(e, "annulled"),
                        Points = e.TryGetProperty("points", out var pv) && pv.ValueKind == JsonValueKind.Number ? pv.GetInt32() : -1,
                        Suspended = su != DateTime.MinValue && su > DateTime.Now ? su : null,
                        Months = monthly.TryGetValue(uid, out var arr) ? arr : new int[12], MonthZero = m0
                    });
                }
            }
            catch { }
            _statsList.EmptyText = Tr("Ningún socio coincide con los filtros.");
            FillStats();
        }

        List<DriverStat> FilteredStats()
        {
            var words = _statsQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            IEnumerable<DriverStat> q = _statsAll.Where(d =>
                (_statsRole == 0 || d.Role == (_statsRole == 1 ? "owner" : _statsRole == 2 ? "manager" : _statsRole == 3 ? "driver" : "former"))
                && (_statsActivity switch
                {
                    1 => d.Services > 0,
                    2 => d.Services == 0,
                    3 => d.LastEver == DateTime.MinValue || (DateTime.Now - d.LastEver).TotalDays > 30,
                    4 => d.LastEver == DateTime.MinValue,
                    _ => true
                })
                && words.All(w => d.Name.ToLowerInvariant().Contains(w)));
            q = _statsOrder switch
            {
                1 => q.OrderByDescending(d => d.Km),
                2 => q.OrderByDescending(d => d.Net),
                3 => q.OrderByDescending(d => d.Hours),
                4 => q.OrderByDescending(d => d.LastEver),
                5 => q.OrderByDescending(d => d.Infractions),
                6 => q.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase),
                _ => q.OrderByDescending(d => d.Services).ThenByDescending(d => d.Km)
            };
            return q.ToList();
        }

        void FillStats()
        {
            if (_statsList == null) return;
            var list = FilteredStats();
            _statsList.BeginUpdate();
            _statsList.Items.Clear(); _statsList.Items.AddRange(list);
            _statsList.EndUpdate();
            // Los antiguos socios suman sus viajes, pero no cuentan como socios activos o inactivos.
            var members = _statsAll.Where(d => d.Role != "former").ToList();
            int act = members.Count(d => d.Services > 0), idle30 = members.Count(d => d.LastEver == DateTime.MinValue || (DateTime.Now - d.LastEver).TotalDays > 30);
            int former = _statsAll.Count - members.Count;
            long svc = _statsAll.Sum(d => d.Services); double km = _statsAll.Sum(d => d.Km), net = _statsAll.Sum(d => d.Net), hrs = _statsAll.Sum(d => d.Hours);
            _statsSummary.Text = _statsAll.Count == 0 ? "" :
                "📊  " + string.Format(Tr("{0} socios  ·  {1} activos y {2} inactivos en el periodo  ·  {3} sin servicio en 30 días"),
                    members.Count, act, members.Count - act, idle30)
                + (former > 0 ? "  ·  " + string.Format(Tr(former == 1 ? "{0} antiguo socio" : "{0} antiguos socios"), former) : "")
                + "      " + string.Format(Tr("{0} servicios  ·  {1} km  ·  {2} h  ·  neto {3}"),
                    svc.ToString("N0", EsEs), km.ToString("N0", EsEs), hrs.ToString("N0", EsEs), net.ToString("+#,##0;−#,##0", EsEs) + " €");
        }
    }
}
