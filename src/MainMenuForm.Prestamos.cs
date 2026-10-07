// Préstamos, como en un banco de verdad (servidor: prestamos.sql).
//  · Banca → Préstamos (socios): préstamos de la empresa en tarjetas, con su cuadro de amortización.
//    El gerente y los gestores los solicitan (importe, plazo, finalidad; con la cuota orientativa), retiran
//    una solicitud pendiente o amortizan antes de tiempo.
//  · Administración → Préstamos (superadmin): todas las solicitudes, con los datos de la empresa para
//    estudiarlas (saldo, beneficio y servicios de los últimos 90 días, deuda viva). Aprueba (TIN y
//    comisión de apertura) o deniega.
//  · Las cuotas vencidas se cobran al abrir la Banca o la sección de préstamos (loan_collect).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SelectOR
{
    public sealed class LoanItem
    {
        public string Id = "", Status = "", Purpose = "", RequestedBy = "", DecidedBy = "", Note = "";
        public string CompanyId = "", Company = "", CompanyCode = "";
        public double Amount, Tin, FeePct, Payment, Outstanding, LateTotal, InterestTotal;
        public int Months, Paid, Late;
        public DateTime Created = DateTime.MinValue, Decided = DateTime.MinValue, Closed = DateTime.MinValue, NextDue = DateTime.MinValue;
        // Para estudiarlo (superadmin)
        public double CoBalance = double.NaN, CoNet90 = double.NaN, CoDebt = double.NaN; public long CoServices90 = -1;
        public bool ShowCompany;
    }

    public sealed class LoanCardList : CardListBase
    {
        static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-ES");
        static readonly Color Amber = Color.FromArgb(232, 178, 80), Blue = Color.FromArgb(120, 144, 226), Red = Color.FromArgb(229, 115, 115);
        readonly Font _fAmt = Theme.Font(15f, FontStyle.Bold), _fTitle = Theme.Font(10f, FontStyle.Bold), _fText = Theme.Font(9f),
                      _fBold = Theme.Font(9f, FontStyle.Bold), _fPill = Theme.Font(8.5f, FontStyle.Bold), _fSmall = Theme.Font(8.25f);

        public LoanCardList() { EmptyText = I18n.T("Sin préstamos."); }
        protected override int MinCardW => Theme.Px(560);
        protected override int MaxCols => 1;
        protected override int CardH => Theme.Px(108);
        protected override int Gap => Theme.Px(8);
        protected override void Dispose(bool disposing)
        {
            if (disposing) foreach (var f in new[] { _fAmt, _fTitle, _fText, _fBold, _fPill, _fSmall }) f.Dispose();
            base.Dispose(disposing);
        }

        public static (string text, Color color) StatusOf(LoanItem l) => l.Status switch
        {
            "pending" => (I18n.T("En estudio"), Amber),
            "active" => l.Late > 0 ? (I18n.T("Con impagos"), Red) : (I18n.T("Vivo"), Blue),
            "paid" => (I18n.T("Devuelto"), Theme.AccentHi),
            "rejected" => (I18n.T("Denegado"), Red),
            "cancelled" => (I18n.T("Retirado"), Theme.Subtle),
            _ => (l.Status, Theme.Subtle)
        };

        static string Eur(double v) => v.ToString(v % 1 == 0 ? "N0" : "N2", Es) + " €";

        protected override void PaintCard(Graphics g, int i, Rectangle rc, bool selected, bool hover)
        {
            if (Items[i] is not LoanItem l) return;
            var (st, sc) = StatusOf(l);
            Fill(g, rc, Theme.Px(10), selected || hover ? Color.FromArgb(50, 54, 57) : Theme.Surface);
            if (selected) Stroke(g, rc, Theme.Px(10), Theme.Accent);
            using (var sb = new SolidBrush(sc)) g.FillRectangle(sb, rc.X, rc.Y + Theme.Px(12), Theme.Px(3), rc.Height - Theme.Px(24));
            int pad = Theme.Px(16), x = rc.X + pad, y = rc.Y + Theme.Px(12), right = rc.Right - pad;

            // Línea 1: importe · (empresa) · estado
            int aw = TW(Eur(l.Amount), _fAmt);
            TextRenderer.DrawText(g, Eur(l.Amount), _fAmt, new Rectangle(x, y - Theme.Px(2), aw + 4, Theme.Px(30)), Theme.Text, L1);
            int lx = x + aw + Theme.Px(14);
            string what = (l.ShowCompany ? l.Company + (l.CompanyCode.Length > 0 ? " (" + l.CompanyCode + ")" + "  ·  " : "  ·  ") : "")
                          + string.Format(I18n.T("{0} meses"), l.Months);
            int pw = TW(st, _fPill) + Theme.Px(20), ph = Theme.Px(22);
            var pill = new Rectangle(right - pw, y + Theme.Px(2), pw, ph);
            Fill(g, pill, ph / 2, Color.FromArgb(45, sc));
            TextRenderer.DrawText(g, st, _fPill, pill, sc, C1);
            TextRenderer.DrawText(g, what, _fTitle, new Rectangle(lx, y, pill.Left - lx - Theme.Px(10), Theme.Px(26)), Theme.Text, L1);
            y += Theme.Px(32);

            // Línea 2: condiciones
            string l2;
            if (l.Status == "pending" || l.Status == "rejected" || (l.Status == "cancelled" && l.Tin == 0 && l.Payment == 0))
                l2 = string.Format(I18n.T("Solicitado el {0} por {1}"), l.Created == DateTime.MinValue ? "—" : l.Created.ToString("dd/MM/yyyy", Es), l.RequestedBy.Length > 0 ? l.RequestedBy : "—")
                     + (l.Purpose.Length > 0 ? "  ·  «" + l.Purpose + "»" : "");
            else
                l2 = string.Format(I18n.T("TIN {0} %  ·  TAE {1} %  ·  cuota {2}/mes  ·  intereses {3}"),
                         l.Tin.ToString("N2", Es), (MainMenuForm.LoanTae(l.Amount, l.FeePct, l.Tin, l.Months) * 100).ToString("N2", Es),
                         Eur(l.Payment), Eur(l.InterestTotal));
            TextRenderer.DrawText(g, l2, _fText, new Rectangle(x, y, right - x, Theme.Px(20)), Theme.Subtle, L1);
            y += Theme.Px(24);

            // Línea 3: progreso de la devolución (o nota del banco)
            if (l.Status == "active" || l.Status == "paid")
            {
                int bw = Math.Min(Theme.Px(260), (right - x) / 3), bh = Theme.Px(8);
                var bar = new Rectangle(x, y + Theme.Px(6), bw, bh);
                Fill(g, bar, bh / 2, Theme.Surface2);
                double frac = l.Months > 0 ? Math.Min(1, l.Paid / (double)l.Months) : 0;
                if (l.Status == "paid") frac = 1;
                if (frac > 0) Fill(g, new Rectangle(bar.X, bar.Y, Math.Max(bh, (int)(bw * frac)), bh), bh / 2, Theme.Accent);
                string l3 = string.Format(I18n.T("{0}/{1} cuotas"), l.Paid, l.Months) + "   ·   " + string.Format(I18n.T("pendiente {0}"), Eur(l.Outstanding));
                if (l.Status == "active" && l.NextDue != DateTime.MinValue) l3 += "   ·   " + string.Format(I18n.T("próxima cuota el {0}"), l.NextDue.ToString("dd/MM/yyyy", Es));
                if (l.Late > 0) l3 += "   ·   " + string.Format(I18n.T(l.Late == 1 ? "{0} cuota impagada" : "{0} cuotas impagadas"), l.Late);
                TextRenderer.DrawText(g, l3, l.Late > 0 ? _fBold : _fText, new Rectangle(bar.Right + Theme.Px(12), y, right - bar.Right - Theme.Px(12), Theme.Px(20)), l.Late > 0 ? Red : Theme.Text, L1);
            }
            else if (l.ShowCompany && l.Status == "pending")
            {
                string co = string.Format(I18n.T("Saldo {0}  ·  neto 90 días {1} ({2} servicios)  ·  deuda viva {3}"),
                    double.IsNaN(l.CoBalance) ? "—" : Eur(Math.Round(l.CoBalance)), double.IsNaN(l.CoNet90) ? "—" : Eur(Math.Round(l.CoNet90)),
                    l.CoServices90 < 0 ? "—" : l.CoServices90.ToString("N0", Es), double.IsNaN(l.CoDebt) ? "—" : Eur(Math.Round(l.CoDebt)));
                TextRenderer.DrawText(g, co, _fText, new Rectangle(x, y, right - x, Theme.Px(20)), Theme.Text, L1);
            }
            else if (l.Note.Length > 0)
                TextRenderer.DrawText(g, "🏦  " + l.Note, _fText, new Rectangle(x, y, right - x, Theme.Px(20)), Theme.Text, L1);
        }
    }

    public partial class MainMenuForm
    {
        // ---------------------------------------------------------------- cálculo
        public static double LoanPayment(double amount, double tin, int months)
        {
            if (months <= 0) return 0;
            double i = tin / 1200.0;
            return Math.Round(i == 0 ? amount / months : amount * i / (1 - Math.Pow(1 + i, -months)), 2);
        }

        // TAE: el interés efectivo anual de lo que de verdad se recibe (el capital menos la comisión de
        // apertura) frente a las cuotas.
        public static double LoanTae(double amount, double feePct, double tin, int months)
        {
            if (amount <= 0 || months <= 0) return 0;
            double pay = LoanPayment(amount, tin, months), got = amount * (1 - feePct / 100.0);
            double lo = 0, hi = 1;
            for (int k = 0; k < 80; k++)
            {
                double mid = (lo + hi) / 2, pv = 0;
                for (int n = 1; n <= months; n++) pv += pay / Math.Pow(1 + mid, n);
                if (pv > got) lo = mid; else hi = mid;
            }
            return Math.Pow(1 + (lo + hi) / 2, 12) - 1;
        }

        static string EurC(double v) => v.ToString(Math.Abs(v % 1) < 0.005 ? "N0" : "N2", EsEs) + " €";   // con céntimos si los hay

        // ---------------------------------------------------------------- Banca → Préstamos
        Panel _loansPage;
        LoanCardList _loanCards;
        InstallmentCardList _loanSched;
        Label _loanSummary, _loanMsg, _loanSchedTitle;
        RoundButton _loanReqBtn, _loanPrepayBtn, _loanCancelBtn;
        int _loansSeq;

        Panel BuildLoansPage()
        {
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, BackColor = Theme.Bg, Visible = false };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // resumen
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 48));    // préstamos
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // título del cuadro
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 52));    // cuadro de amortización
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // acciones
            _loanSummary = new Label { AutoSize = true, ForeColor = Theme.Text, Font = Theme.Font(10f), Margin = new Padding(2, 8, 2, 8), MaximumSize = new Size(1100, 0) };
            t.Controls.Add(_loanSummary);
            _loanCards = new LoanCardList { Dock = DockStyle.Fill, Margin = new Padding(2, 2, 2, 4) };
            _loanCards.SelectedIndexChanged += (s, e) => { LoadLoanSchedule(); UpdateLoanButtons(); };
            t.Controls.Add(_loanCards);
            _loanSchedTitle = new Label { AutoSize = true, ForeColor = Theme.Subtle, Font = Theme.Font(9f, FontStyle.Bold), Margin = new Padding(2, 6, 2, 4), Text = Tr("CUADRO DE AMORTIZACIÓN") };
            t.Controls.Add(_loanSchedTitle);
            _loanSched = NewScheduleCards();
            t.Controls.Add(_loanSched);

            var btns = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Theme.Bg, Margin = new Padding(0) };
            _loanReqBtn = EmpButton(Tr("Solicitar préstamo"), primary: true); _loanReqBtn.Width = 200;
            _loanReqBtn.Click += (s, e) => RequestLoan();
            _loanPrepayBtn = EmpButton(Tr("Amortizar…")); _loanPrepayBtn.Width = 150; _loanPrepayBtn.Margin = new Padding(8, 10, 2, 2);
            _loanPrepayBtn.Click += (s, e) => PrepayLoan();
            _loanCancelBtn = EmpButton(Tr("Retirar solicitud")); _loanCancelBtn.Width = 170; _loanCancelBtn.Margin = new Padding(8, 10, 2, 2);
            _loanCancelBtn.BaseColor = Theme.Surface2; _loanCancelBtn.HoverColor = Color.FromArgb(150, 60, 60); _loanCancelBtn.TextColor = RedC;
            _loanCancelBtn.Click += (s, e) => CancelLoanRequest();
            btns.Controls.AddRange(new Control[] { _loanReqBtn, _loanPrepayBtn, _loanCancelBtn });
            _loanMsg = EmpMsg(); _loanMsg.Margin = new Padding(12, 20, 2, 2); _loanMsg.MaximumSize = new Size(560, 0);
            btns.Controls.Add(_loanMsg);
            t.Controls.Add(btns);
            _loansPage = t;
            return t;
        }

        static InstallmentCardList NewScheduleCards() => new InstallmentCardList { Dock = DockStyle.Fill, Margin = new Padding(2, 0, 2, 4) };

        void UpdateLoanButtons()
        {
            bool can = CanManage() || Supa.IsSuperadmin;
            var l = _loanCards?.SelectedItem as LoanItem;
            if (_loanReqBtn != null) _loanReqBtn.Visible = can;
            if (_loanPrepayBtn != null) _loanPrepayBtn.Visible = can && l?.Status == "active";
            if (_loanCancelBtn != null) _loanCancelBtn.Visible = can && l?.Status == "pending";
        }

        // Banca abierta: primero se cobran las cuotas vencidas y luego se lee el extracto (ya con ellas).
        async void OnBankShown()
        {
            await CollectLoans();
            LoadLedger();
            if (_loansPage != null && _loansPage.Visible) LoadLoans();
        }

        DateTime _loanCollectAt = DateTime.MinValue;
        // Cobra las cuotas vencidas (de todas las empresas). Como mucho una vez por minuto.
        async Task CollectLoans(bool force = false)
        {
            if (!Supa.IsLoggedIn) return;
            if (!force && (DateTime.UtcNow - _loanCollectAt).TotalSeconds < 60) return;
            _loanCollectAt = DateTime.UtcNow;
            try { await Supa.RpcAsync("loan_collect", new { }); } catch { }
        }

        static List<LoanItem> ParseLoans(string json, bool admin)
        {
            var list = new List<LoanItem>();
            try
            {
                using var d = JsonDocument.Parse(json);
                foreach (var e in d.RootElement.EnumerateArray())
                {
                    DateTime Dt(string k) => DateTimeOffset.TryParse(Str(e, k), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var o) ? o.LocalDateTime : DateTime.MinValue;
                    double NaN(string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : double.NaN;
                    list.Add(new LoanItem
                    {
                        Id = Str(e, "id"), Status = Str(e, "status"), Purpose = Str(e, "purpose"), Note = Str(e, "admin_note"),
                        RequestedBy = Str(e, "requested_by_name"), DecidedBy = Str(e, "decided_by_name"),
                        CompanyId = Str(e, "company_id"), Company = Str(e, "company_name"), CompanyCode = Str(e, "company_code"),
                        Amount = Num(e, "amount"), Tin = Num(e, "tin"), FeePct = Num(e, "fee_pct"), Payment = Num(e, "payment"),
                        Outstanding = Num(e, "outstanding"), LateTotal = Num(e, "late_total"), InterestTotal = Num(e, "interest_total"),
                        Months = (int)Num(e, "months"), Paid = (int)Num(e, "paid_count"), Late = (int)Num(e, "late_count"),
                        Created = Dt("created_at"), Decided = Dt("decided_at"), Closed = Dt("closed_at"), NextDue = Dt("next_due"),
                        CoBalance = NaN("company_balance"), CoNet90 = NaN("company_net_90d"), CoDebt = NaN("company_debt"),
                        CoServices90 = e.TryGetProperty("company_services_90d", out var cs) && cs.ValueKind == JsonValueKind.Number ? cs.GetInt64() : -1,
                        ShowCompany = admin
                    });
                }
            }
            catch { }
            return list;
        }

        async void LoadLoans()
        {
            var c = _empSel;
            if (c == null || _loanCards == null) return;
            int seq = ++_loansSeq;
            await CollectLoans();
            var (json, err) = await Supa.RpcAsync("list_company_loans", new { p_company = c.Id });
            if (seq != _loansSeq || _empSel?.Id != c.Id) return;
            if (err != null)
            {
                _loanCards.EmptyText = err.Contains("PGRST202") ? Tr("El servidor aún no tiene los préstamos (falta prestamos.sql).") : Tr("Error: ") + err;
                SetLoanItems(_loanCards, new List<LoanItem>());
                _loanSummary.Text = "";
                UpdateLoanButtons();
                return;
            }
            var list = ParseLoans(json, false);
            _loanCards.EmptyText = Tr("La empresa no tiene préstamos.") + ((CanManage() || Supa.IsSuperadmin) ? "\n" + Tr("Pulsa «Solicitar préstamo» para pedir uno al banco.") : "");
            SetLoanItems(_loanCards, list);
            var live = list.Where(l => l.Status == "active").ToList();
            double debt = live.Sum(l => l.Outstanding), monthly = live.Sum(l => l.Payment);
            var next = live.Where(l => l.NextDue != DateTime.MinValue).Select(l => l.NextDue).DefaultIfEmpty(DateTime.MinValue).Min();
            int pend = list.Count(l => l.Status == "pending"), late = live.Sum(l => l.Late);
            _loanSummary.Text = live.Count == 0 && pend == 0
                ? Tr("🏦  El banco presta a la empresa entre 10.000 € y 50.000.000 €, a devolver en 6 a 60 meses con cuotas mensuales. El administrador estudia cada solicitud y fija el interés.")
                : "🏦  " + string.Format(Tr("Deuda viva: {0}  ·  cuotas: {1}/mes"), EurC(debt), EurC(monthly))
                  + (next != DateTime.MinValue ? "  ·  " + string.Format(Tr("próximo cobro: {0}"), next.ToString("dd/MM/yyyy", EsEs)) : "")
                  + (pend > 0 ? "  ·  " + string.Format(Tr(pend == 1 ? "{0} solicitud en estudio" : "{0} solicitudes en estudio"), pend) : "")
                  + (late > 0 ? "  ·  ⚠ " + string.Format(Tr(late == 1 ? "{0} cuota impagada" : "{0} cuotas impagadas"), late) : "");
            _loanSummary.ForeColor = late > 0 ? RedC : Theme.Text;
            LoadLoanSchedule();
            UpdateLoanButtons();
        }

        static void SetLoanItems(LoanCardList cards, List<LoanItem> list)
        {
            string keep = (cards.SelectedItem as LoanItem)?.Id;
            cards.BeginUpdate();
            cards.Items.Clear(); cards.Items.AddRange(list);
            cards.EndUpdate();
            int k = keep == null ? -1 : list.FindIndex(l => l.Id == keep);
            if (k < 0 && list.Count > 0) k = 0;
            if (k >= 0) cards.SelectedIndex = k;
        }

        async void LoadLoanSchedule() => await FillLoanSchedule(_loanCards, _loanSched, _loanSchedTitle);

        async Task FillLoanSchedule(LoanCardList cards, InstallmentCardList table, Label title)
        {
            if (cards == null || table == null) return;
            var l = cards.SelectedItem as LoanItem;
            void Show(string msg) { table.EmptyText = msg; table.BeginUpdate(); table.Items.Clear(); table.EndUpdate(); }
            if (l == null || l.Status == "pending" || l.Status == "rejected" || (l.Status == "cancelled" && l.Payment == 0))
            {
                Show(l == null ? Tr("Elige un préstamo.")
                     : l.Status == "pending" ? string.Format(Tr("En estudio. Cuota orientativa con un TIN del 6 %: {0}/mes."), EurC(LoanPayment(l.Amount, 6, l.Months)))
                     : Tr("Sin cuadro de amortización."));
                if (title != null) title.Text = Tr("CUADRO DE AMORTIZACIÓN");
                return;
            }
            var (json, err) = await Supa.RpcAsync("loan_schedule", new { p_loan = l.Id });
            if (cards.SelectedItem != l) return;
            if (err != null) { Show(Tr("Error: ") + err); return; }
            if (title != null) title.Text = Tr("CUADRO DE AMORTIZACIÓN") + "  ·  " + string.Format(Tr("{0} a {1} meses · TIN {2} % · TAE {3} %"),
                EurC(l.Amount), l.Months, l.Tin.ToString("N2", EsEs), (LoanTae(l.Amount, l.FeePct, l.Tin, l.Months) * 100).ToString("N2", EsEs));
            var list = new List<InstallmentItem>();
            try
            {
                using var d = JsonDocument.Parse(json);
                foreach (var e in d.RootElement.EnumerateArray())
                {
                    DateTime Dt(string k) => DateTimeOffset.TryParse(Str(e, k), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var o) ? o.LocalDateTime : DateTime.MinValue;
                    list.Add(new InstallmentItem
                    {
                        N = (int)Num(e, "n"), Months = l.Months, Due = Dt("due_at"), PaidAt = Dt("paid_at"),
                        Payment = Num(e, "payment"), Interest = Num(e, "interest"), Principal = Num(e, "principal"),
                        LateFee = Num(e, "late_fee"), Status = Str(e, "status")
                    });
                }
            }
            catch { }
            table.EmptyText = Tr("Sin cuadro de amortización.");
            table.BeginUpdate(); table.Items.Clear(); table.Items.AddRange(list); table.EndUpdate();
            // a la vista, la cuota que toca (la primera sin pagar)
            int k = list.FindIndex(x => x.Status == "late" || x.Status == "pending");
            if (k > 0) table.TopIndex = k;
        }

        void RequestLoan()
        {
            var c = _empSel;
            if (c == null || !(CanManage() || Supa.IsSuperadmin)) return;
            int[] plazos = { 6, 12, 18, 24, 36, 48, 60 };
            using var dlg = new FormDialog(Tr("Solicitar préstamo"), Tr("Enviar solicitud"), 560);
            dlg.AddInfo(string.Format(Tr("{0} pide un préstamo al banco. El administrador estudiará la solicitud y fijará el tipo de interés; si lo aprueba, el dinero se ingresa en la tesorería y cada mes se cobra la cuota."), c.Name));
            var amt = dlg.AddText("amount", Tr("Importe (€) · entre 10.000 y 50.000.000"), "", Tr("Por ejemplo: 500000"));
            var term = dlg.AddCombo("months", Tr("Plazo"), plazos.Select(p => string.Format(Tr("{0} meses"), p)), 3);
            dlg.AddText("purpose", Tr("Finalidad (opcional)"), "", Tr("Por ejemplo: compra de dos unidades nuevas"));
            var sim = dlg.AddInfo(" ", Theme.AccentHi);
            void Sim()
            {
                double a = ParseMoney(amt.Box.Text);
                int m = plazos[Math.Max(0, term.SelectedIndex)];
                sim.Text = a >= 10000 && a <= 50000000
                    ? string.Format(Tr("Cuota orientativa con un TIN del 6 %: {0}/mes · total a devolver {1}"), EurC(LoanPayment(a, 6, m)), EurC(LoanPayment(a, 6, m) * m))
                    : Tr("Escribe un importe para ver la cuota orientativa.");
            }
            amt.Box.TextChanged += (s, e) => Sim(); term.SelectedIndexChanged += (s, e) => Sim(); Sim();
            dlg.Validate = async f =>
            {
                double a = ParseMoney(f.Get("amount"));
                if (double.IsNaN(a) || a < 10000 || a > 50000000) return Tr("El importe debe estar entre 10.000 € y 50.000.000 €.");
                int m = plazos[Math.Max(0, f.Index("months"))];
                var (_, err) = await Supa.RpcAsync("request_loan", new { p_company = c.Id, p_amount = Math.Round(a, 2), p_months = m, p_purpose = f.Get("purpose") });
                return err == null ? null : Tr("Error: ") + err;
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            Msg(_loanMsg, Tr("Solicitud enviada al banco. Te llegará un aviso cuando se resuelva."), false);
            LoadLoans();
        }

        static double ParseMoney(string s)
        {
            s = (s ?? "").Replace("€", "").Replace(" ", "").Trim();
            if (s.Length == 0) return double.NaN;
            // «1.500.000» o «1500000,50» (es) y «1,500,000.50» (en)
            if (s.Contains(',') && s.LastIndexOf(',') > s.LastIndexOf('.')) s = s.Replace(".", "").Replace(',', '.');
            else s = s.Replace(",", "");
            if (s.Count(ch => ch == '.') > 1) s = s.Replace(".", "");
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN;
        }

        void PrepayLoan()
        {
            var l = _loanCards?.SelectedItem as LoanItem;
            if (l == null || l.Status != "active") return;
            using var dlg = new FormDialog(Tr("Amortización anticipada"), Tr("Amortizar"), 540);
            dlg.AddInfo(string.Format(Tr("Capital pendiente: {0}. Puedes devolver una parte (se mantiene el plazo y baja la cuota) o todo (se cancela el préstamo). Comisión: 0,5 % de lo que se devuelve."), EurC(l.Outstanding)));
            var amt = dlg.AddText("amount", Tr("Importe a devolver (€)"), l.Outstanding.ToString("0.##", CultureInfo.InvariantCulture).Replace('.', ','));
            var info = dlg.AddInfo(" ", Theme.AccentHi);
            void Upd()
            {
                double a = Math.Min(ParseMoney(amt.Box.Text), l.Outstanding);
                if (double.IsNaN(a) || a <= 0) { info.Text = Tr("Escribe un importe."); return; }
                int left = Math.Max(1, l.Months - l.Paid);
                info.Text = a >= l.Outstanding - 0.005
                    ? string.Format(Tr("Se cancela el préstamo. Se cobran {0} + {1} de comisión."), EurC(l.Outstanding), EurC(Math.Round(l.Outstanding * 0.005, 2)))
                    : string.Format(Tr("Nueva cuota: {0}/mes (antes {1}). Se cobran {2} + {3} de comisión."),
                          EurC(LoanPayment(l.Outstanding - a, l.Tin, left)), EurC(l.Payment), EurC(a), EurC(Math.Round(a * 0.005, 2)));
            }
            amt.Box.TextChanged += (s, e) => Upd(); Upd();
            dlg.Validate = async f =>
            {
                double a = ParseMoney(f.Get("amount"));
                if (double.IsNaN(a) || a <= 0) return Tr("Importe no válido.");
                var (_, err) = await Supa.RpcAsync("loan_prepay", new { p_loan = l.Id, p_amount = Math.Round(Math.Min(a, l.Outstanding), 2) });
                return err == null ? null : Tr("Error: ") + err;
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            Msg(_loanMsg, Tr("Amortización hecha."), false);
            LoadLoans(); LoadLedger(); LoadCompanies(true);
        }

        async void CancelLoanRequest()
        {
            var l = _loanCards?.SelectedItem as LoanItem;
            if (l == null || l.Status != "pending") return;
            if (MessageBox.Show(this, Tr("¿Retirar la solicitud de préstamo?"), "SelectOR", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            var (_, err) = await Supa.RpcAsync("cancel_loan_request", new { p_loan = l.Id });
            if (err != null) { Msg(_loanMsg, Tr("Error: ") + err, true); return; }
            Msg(_loanMsg, Tr("Solicitud retirada."), false);
            LoadLoans();
        }

        // ---------------------------------------------------------------- Administración → Préstamos (superadmin)
        Panel _loansAdminPanel;
        LoanCardList _loanAdminCards;
        InstallmentCardList _loanAdminSched;
        Label _loanAdminMsg, _loanAdminSchedTitle;
        readonly List<LoanItem> _loanAdminAll = new();
        int _loanAdminTab;

        Panel BuildLoansAdminSubpanel()
        {
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, BackColor = Theme.Bg };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var intro = EmpIntro("Solicitudes de préstamo de todas las empresas. Estudia cada una con los datos de la empresa y apruébala fijando el tipo de interés (TIN) y la comisión de apertura, o deniégala. Al aprobarla se ingresa el capital y empiezan a cobrarse las cuotas cada mes.");
            intro.MaximumSize = new Size(900, 0);
            t.Controls.Add(intro);
            var tabs = MakeSubTabs(new[] { "En estudio", "Vivos", "Todos" }, i => { _loanAdminTab = i; FillLoanAdmin(); });
            tabs.Margin = new Padding(0, 0, 0, 6);
            t.Controls.Add(tabs);
            _loanAdminCards = new LoanCardList { Dock = DockStyle.Fill, Margin = new Padding(2, 2, 2, 4) };
            _loanAdminCards.SelectedIndexChanged += async (s, e) => await FillLoanSchedule(_loanAdminCards, _loanAdminSched, _loanAdminSchedTitle);
            t.Controls.Add(_loanAdminCards);
            _loanAdminSchedTitle = new Label { AutoSize = true, ForeColor = Theme.Subtle, Font = Theme.Font(9f, FontStyle.Bold), Margin = new Padding(2, 6, 2, 4), Text = Tr("CUADRO DE AMORTIZACIÓN") };
            t.Controls.Add(_loanAdminSchedTitle);
            _loanAdminSched = NewScheduleCards();
            t.Controls.Add(_loanAdminSched);
            var btns = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Theme.Bg, Margin = new Padding(0) };
            var ok = EmpButton(Tr("Aprobar…"), primary: true); ok.Width = 160; ok.Click += (s, e) => DecideLoan(true);
            var no = EmpButton(Tr("Denegar…")); no.Width = 150; no.Margin = new Padding(8, 10, 2, 2);
            no.BaseColor = Theme.Surface2; no.HoverColor = Color.FromArgb(150, 60, 60); no.TextColor = RedC; no.Click += (s, e) => DecideLoan(false);
            btns.Controls.Add(ok); btns.Controls.Add(no);
            _loanAdminMsg = EmpMsg(); _loanAdminMsg.Margin = new Padding(12, 20, 2, 2); _loanAdminMsg.MaximumSize = new Size(600, 0);
            btns.Controls.Add(_loanAdminMsg);
            t.Controls.Add(btns);
            _loansAdminPanel = t;
            return t;
        }

        bool _loanAdminLoading;
        async void LoadLoansAdmin(bool onlyCount = false)
        {
            if (!Supa.IsSuperadmin) { SetLoansAdminCount(0); return; }
            if (_loanAdminLoading && onlyCount) return;
            _loanAdminLoading = true;
            string json, err;
            try { if (!onlyCount) await CollectLoans(); (json, err) = await Supa.RpcAsync("list_all_loans", new { }); }
            finally { _loanAdminLoading = false; }
            var list = err == null ? ParseLoans(json, true) : new List<LoanItem>();
            SetLoansAdminCount(list.Count(l => l.Status == "pending"));
            if (onlyCount || _loanAdminCards == null) return;
            _loanAdminAll.Clear(); _loanAdminAll.AddRange(list);
            _loanAdminCards.EmptyText = err != null
                ? (err.Contains("PGRST202") ? Tr("El servidor aún no tiene los préstamos (falta prestamos.sql).") : Tr("Error: ") + err)
                : Tr("No hay préstamos en esta lista.");
            FillLoanAdmin();
        }

        void FillLoanAdmin()
        {
            if (_loanAdminCards == null) return;
            var list = _loanAdminAll.Where(l => _loanAdminTab switch { 0 => l.Status == "pending", 1 => l.Status == "active", _ => true }).ToList();
            SetLoanItems(_loanAdminCards, list);
            _ = FillLoanSchedule(_loanAdminCards, _loanAdminSched, _loanAdminSchedTitle);
        }

        void SetLoansAdminCount(int n)
        {
            if (_empSubtabs == null || _empSubtabs.Length <= PrestamosSubtab || _empSubtabs[PrestamosSubtab] == null) return;
            string txt = Tr(SubNames[PrestamosSubtab]) + (n > 0 ? "  (" + n + ")" : "");
            if (_empSubtabs[PrestamosSubtab].Text != txt) { _empSubtabs[PrestamosSubtab].Text = txt; _empSubtabs[PrestamosSubtab].Invalidate(); }
        }

        void DecideLoan(bool approve)
        {
            var l = _loanAdminCards?.SelectedItem as LoanItem;
            if (l == null) { Msg(_loanAdminMsg, Tr("Elige una solicitud."), true); return; }
            if (l.Status != "pending") { Msg(_loanAdminMsg, Tr("Esa solicitud ya está resuelta."), true); return; }
            using var dlg = new FormDialog(approve ? Tr("Aprobar préstamo") : Tr("Denegar préstamo"), approve ? Tr("Aprobar y pagar") : Tr("Denegar"), 560);
            dlg.AddInfo(string.Format(Tr("{0} pide {1} a {2} meses."), l.Company, EurC(l.Amount), l.Months) + (l.Purpose.Length > 0 ? "\n«" + l.Purpose + "»" : ""));
            RoundedInput tin = null, fee = null; Label sim = null;
            if (approve)
            {
                tin = dlg.AddText("tin", Tr("TIN anual (%) · entre 0 y 30"), "6");
                fee = dlg.AddText("fee", Tr("Comisión de apertura (%) · entre 0 y 5"), "0,5");
                sim = dlg.AddInfo(" ", Theme.AccentHi);
                void Sim()
                {
                    double ti = ParseMoney(tin.Box.Text), fe = ParseMoney(fee.Box.Text);
                    if (double.IsNaN(ti) || double.IsNaN(fe)) { sim.Text = Tr("Escribe el TIN y la comisión."); return; }
                    double pay = LoanPayment(l.Amount, ti, l.Months);
                    sim.Text = string.Format(Tr("Cuota: {0}/mes · intereses totales {1} · comisión {2} · TAE {3} %"),
                        EurC(pay), EurC(Math.Round(pay * l.Months - l.Amount, 2)), EurC(Math.Round(l.Amount * fe / 100, 2)), (LoanTae(l.Amount, fe, ti, l.Months) * 100).ToString("N2", EsEs));
                }
                tin.Box.TextChanged += (s, e) => Sim(); fee.Box.TextChanged += (s, e) => Sim(); Sim();
            }
            dlg.AddText("note", approve ? Tr("Nota para la empresa (opcional)") : Tr("Motivo (lo verá la empresa)"), "");
            dlg.Validate = async f =>
            {
                double ti = approve ? ParseMoney(f.Get("tin")) : 0, fe = approve ? ParseMoney(f.Get("fee")) : 0;
                if (approve && (double.IsNaN(ti) || ti < 0 || ti > 30)) return Tr("El TIN debe estar entre 0 y 30 %.");
                if (approve && (double.IsNaN(fe) || fe < 0 || fe > 5)) return Tr("La comisión de apertura debe estar entre 0 y 5 %.");
                var (_, err) = await Supa.RpcAsync("decide_loan", new { p_loan = l.Id, p_approve = approve, p_tin = approve ? Math.Round(ti, 3) : (double?)null, p_fee_pct = Math.Round(fe, 2), p_note = f.Get("note") });
                return err == null ? null : Tr("Error: ") + err;
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            Msg(_loanAdminMsg, approve ? Tr("Préstamo aprobado: el capital ya está en la tesorería de la empresa.") : Tr("Préstamo denegado."), false);
            LoadLoansAdmin();
            LoadCompanies(true);
        }
    }
}
