// BANCA: resumen (tarjeta de tesorería, indicadores con tendencia, en qué se va el dinero y meses) y
// extracto (agrupado por día, con icono por concepto y el saldo tras cada movimiento). Dibujado a mano.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SelectOR
{
    public class BankSummary : CanvasPanel
    {
        public string Company = "", Holder = "", AccountNo = "", Period = "";
        public Image Logo;
        public double Balance, MonthNet, Income, Expense;
        public double[] IncomeTrend = Array.Empty<double>(), ExpenseTrend = Array.Empty<double>();
        public string BestMonth = "—", BestMonthSub = "";
        public List<(string icon, string label, double value, Color col)> Costs = new();
        public List<(string label, double inc, double exp)> Months = new();
        public string LblTreasury = "TESORERÍA", LblAvail = "Saldo disponible", LblHolder = "Titular", LblMonth = "Movimientos del mes",
                      LblIncome = "INGRESOS", LblExpense = "GASTOS", LblResult = "RESULTADO", LblBest = "MEJOR MES", LblIncomeSub = "servicios y premios",
                      LblExpenseSub = "flota, vía, energía y salarios", LblMargin = "margen del {0} %", LblCosts = "EN QUÉ SE VA EL DINERO", LblMonths = "INGRESOS Y GASTOS POR MES",
                      LblInc = "Ingresos", LblExp = "Gastos", LblNoData = "Sin datos todavía";
        Font _fCap, _fBal, _fNum, _fSmall, _fSmallB, _fKpi, _fKpiCap, _fRow, _fRowB, _fIc, _fAx;
        Rectangle _card, _cost, _months; readonly Rectangle[] _k = new Rectangle[4];

        public BankSummary()
        {
            _fCap = F(7.5f, FontStyle.Bold); _fBal = F(22f, FontStyle.Bold); _fNum = new Font("Consolas", 10f * Theme.UiScale * Theme.DpiComp); _fSmall = F(8.25f); _fSmallB = F(9.5f, FontStyle.Bold);
            _fKpi = F(15f, FontStyle.Bold); _fKpiCap = F(7.5f, FontStyle.Bold); _fRow = F(9.25f); _fRowB = F(9.25f, FontStyle.Bold); _fIc = EmojiPicker.EmojiFont(11f); _fAx = F(7.75f);
        }
        protected override void Dispose(bool disposing) { if (disposing) { _fNum.Dispose(); _fIc.Dispose(); } base.Dispose(disposing); }

        protected override int DoLayout(int w)
        {
            int gap = Theme.Px(14), y = Theme.Px(2);
            int cw = (int)((w - gap) * 0.52);
            _card = new Rectangle(0, y, cw, Theme.Px(206));
            int kx = cw + gap, kw = (w - kx - Theme.Px(10)) / 2, kh = (Theme.Px(206) - Theme.Px(10)) / 2;
            for (int i = 0; i < 4; i++) _k[i] = new Rectangle(kx + (i % 2) * (kw + Theme.Px(10)), y + (i / 2) * (kh + Theme.Px(10)), kw, kh);
            y += Theme.Px(206) + gap;
            int lw = (int)((w - gap) * 0.45);
            int ch = Theme.Px(56) + Math.Max(1, Costs.Count) * Theme.Px(38);
            ch = Math.Max(ch, Theme.Px(250));
            _cost = new Rectangle(0, y, lw, ch); _months = new Rectangle(lw + gap, y, w - lw - gap, ch);
            return y + ch + Theme.Px(8);
        }

        static string Eur(double v, bool sign = false) => (sign ? (v >= 0 ? "+" : "−") : (v < 0 ? "−" : "")) + Math.Abs(v).ToString("N0", Es) + " €";

        protected override void DoPaint(Graphics g, int top)
        {
            var card = _card; card.Offset(0, -top);
            PaintCard(g, card);
            string margin = Income > 0 ? string.Format(LblMargin, ((Income - Expense) / Income * 100).ToString("N0", Es)) : "";
            PaintKpi(g, Off(_k[0], top), LblIncome, Eur(Income), LblIncomeSub, Theme.AccentHi, IncomeTrend);
            PaintKpi(g, Off(_k[1], top), LblExpense, Eur(Expense), LblExpenseSub, Red, ExpenseTrend);
            PaintKpi(g, Off(_k[2], top), LblResult, Eur(Income - Expense, true), margin, Income - Expense < 0 ? Red : Theme.AccentHi, null);
            PaintKpi(g, Off(_k[3], top), LblBest, BestMonth, BestMonthSub, Theme.Text, null);
            PaintCosts(g, Off(_cost, top));
            PaintMonths(g, Off(_months, top));
        }
        static Rectangle Off(Rectangle r, int top) { r.Offset(0, -top); return r; }

        void PaintCard(Graphics g, Rectangle r)
        {
            int rad = Theme.Px(18);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Theme.Round(r, rad))
            {
                using (var b = new LinearGradientBrush(r, Color.FromArgb(28, 92, 60), Color.FromArgb(18, 30, 30), LinearGradientMode.ForwardDiagonal)) g.FillPath(b, path);
                var st = g.Save(); g.SetClip(path);
                using (var gp = new GraphicsPath())
                {
                    var c = new Rectangle(r.Right - Theme.Px(170), r.Y - Theme.Px(90), Theme.Px(260), Theme.Px(260));
                    gp.AddEllipse(c);
                    using var pb = new PathGradientBrush(gp) { CenterColor = Color.FromArgb(34, 255, 255, 255), SurroundColors = new[] { Color.FromArgb(0, 255, 255, 255) } };
                    g.FillEllipse(pb, c);
                }
                g.Restore(st);
                using (var p = new Pen(Color.FromArgb(20, 255, 255, 255))) g.DrawPath(p, path);
            }
            g.SmoothingMode = SmoothingMode.None;
            int x = r.X + Theme.Px(20), w = r.Width - Theme.Px(40), y = r.Y + Theme.Px(16);
            Color soft = Color.FromArgb(200, 255, 255, 255);
            int tw = TW("🏦 " + LblTreasury, _fCap) + 4;
            Text(g, (Company ?? "").ToUpperInvariant(), _fCap, new Rectangle(x, y, w - tw - 10, Theme.Px(16)), soft, L1);
            Text(g, "🏦 " + LblTreasury, _fCap, new Rectangle(x + w - tw, y, tw, Theme.Px(16)), soft, R1);
            y += Theme.Px(28);
            var chip = new Rectangle(x, y, Theme.Px(40), Theme.Px(30));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Theme.Round(chip, Theme.Px(6))) using (var b = new LinearGradientBrush(chip, Color.FromArgb(231, 199, 102), Color.FromArgb(168, 134, 42), LinearGradientMode.ForwardDiagonal)) g.FillPath(b, path);
            using (var p = new Pen(Color.FromArgb(90, 80, 60, 20))) { g.DrawLine(p, chip.X + chip.Width / 3, chip.Y + 4, chip.X + chip.Width / 3, chip.Bottom - 4); g.DrawLine(p, chip.X + 4, chip.Y + chip.Height / 2, chip.Right - 4, chip.Y + chip.Height / 2); }
            g.SmoothingMode = SmoothingMode.None;
            if (Logo != null) Avatar(g, new Rectangle(x + w - Theme.Px(30), y, Theme.Px(30), Theme.Px(30)), Logo, Company, _fCap);
            y += Theme.Px(38);
            Text(g, LblAvail, _fSmall, new Rectangle(x, y, w, Theme.Px(16)), soft, L1); y += Theme.Px(16);
            Text(g, Balance.ToString("N2", Es) + " €", _fBal, new Rectangle(x, y, w, Theme.Px(38)), Color.White, L1); y += Theme.Px(40);
            Text(g, AccountNo, _fNum, new Rectangle(x, y, w, Theme.Px(18)), Color.FromArgb(215, 255, 255, 255), L1);
            int fy = r.Bottom - Theme.Px(40);
            Text(g, LblHolder, _fSmall, new Rectangle(x, fy, w / 2, Theme.Px(14)), soft, L1);
            Text(g, Holder, _fSmallB, new Rectangle(x, fy + Theme.Px(14), w / 2, Theme.Px(18)), Color.White, L1);
            Text(g, LblMonth, _fSmall, new Rectangle(x + w / 2, fy, w / 2, Theme.Px(14)), soft, R1);
            Text(g, Eur(MonthNet, true), _fSmallB, new Rectangle(x + w / 2, fy + Theme.Px(14), w / 2, Theme.Px(18)), Color.White, R1);
        }

        void PaintKpi(Graphics g, Rectangle r, string cap, string val, string sub, Color col, double[] trend)
        {
            Round(g, r, Theme.Px(12), CardC);
            int x = r.X + Theme.Px(14);
            Text(g, cap, _fKpiCap, new Rectangle(x, r.Y + Theme.Px(12), r.Width - Theme.Px(100), Theme.Px(14)), Theme.Subtle, L1);
            Text(g, val, _fKpi, new Rectangle(x, r.Y + Theme.Px(28), r.Width - Theme.Px(24), Theme.Px(28)), col, L1);
            Text(g, sub, _fSmall, new Rectangle(x, r.Y + Theme.Px(58), r.Width - Theme.Px(24), Theme.Px(16)), Theme.Subtle, L1);
            if (trend != null && trend.Length >= 2)
            {
                var sr = new Rectangle(r.Right - Theme.Px(84), r.Y + Theme.Px(12), Theme.Px(70), Theme.Px(24));
                double mx = 1; foreach (var v in trend) mx = Math.Max(mx, v);
                var pts = new PointF[trend.Length];
                for (int i = 0; i < trend.Length; i++) pts[i] = new PointF(sr.X + sr.Width * i / (float)(trend.Length - 1), sr.Bottom - (float)(trend[i] / mx) * sr.Height);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var p = new Pen(col, 2f)) g.DrawLines(p, pts);
                g.SmoothingMode = SmoothingMode.None;
            }
        }

        void PaintCosts(Graphics g, Rectangle r)
        {
            Round(g, r, Theme.Px(14), CardC);
            int x = r.X + Theme.Px(14), w = r.Width - Theme.Px(28), y = r.Y + Theme.Px(14);
            double total = 0; foreach (var c in Costs) total += c.value;
            string tt = Eur(total); int ttw = TW(tt, _fKpiCap) + 4;
            Text(g, LblCosts, _fKpiCap, new Rectangle(x, y, w - ttw, Theme.Px(14)), Theme.Subtle, L1);
            Text(g, tt, _fKpiCap, new Rectangle(x + w - ttw, y, ttw, Theme.Px(14)), Theme.Subtle, R1);
            y += Theme.Px(30);
            if (Costs.Count == 0) { Text(g, LblNoData, _fRow, new Rectangle(x, y, w, Theme.Px(20)), Theme.Subtle, L1); return; }
            var icons = new List<(string, Rectangle)>();
            foreach (var (ic, label, value, col) in Costs)
            {
                var ir = new Rectangle(x, y, Theme.Px(26), Theme.Px(26));
                Round(g, ir, Theme.Px(8), Color.FromArgb(50, col));
                icons.Add((ic, ir));
                string v = Eur(value) + "  ·  " + (total > 0 ? (value / total * 100).ToString("N1", Es) : "0") + " %";
                int vw = TW(v, _fRowB) + 4;
                Text(g, label, _fRow, new Rectangle(ir.Right + Theme.Px(10), y, w - vw - Theme.Px(40), Theme.Px(20)), Theme.Text, L1);
                Text(g, v, _fRowB, new Rectangle(x + w - vw, y, vw, Theme.Px(20)), Theme.Text, R1);
                var br = new Rectangle(ir.Right + Theme.Px(10), y + Theme.Px(22), w - Theme.Px(36), Theme.Px(6));
                Round(g, br, 3, Theme.Surface2);
                int fw = total > 0 ? (int)(br.Width * value / total) : 0; if (fw > 3) Round(g, new Rectangle(br.X, br.Y, fw, br.Height), 3, col);
                y += Theme.Px(38);
            }
            ColorText.DrawCells(g, icons, _fIc, Theme.Text, r);
        }

        void PaintMonths(Graphics g, Rectangle r)
        {
            Round(g, r, Theme.Px(14), CardC);
            int x = r.X + Theme.Px(14), w = r.Width - Theme.Px(28), y = r.Y + Theme.Px(14);
            int pw = TW(Period, _fKpiCap) + 4;
            Text(g, LblMonths, _fKpiCap, new Rectangle(x, y, w - pw, Theme.Px(14)), Theme.Subtle, L1);
            Text(g, Period, _fKpiCap, new Rectangle(x + w - pw, y, pw, Theme.Px(14)), Theme.Subtle, R1);
            var plot = new Rectangle(x + Theme.Px(56), y + Theme.Px(28), w - Theme.Px(60), r.Bottom - y - Theme.Px(80));
            if (Months.Count == 0) { Text(g, LblNoData, _fRow, new Rectangle(x, y + Theme.Px(30), w, Theme.Px(20)), Theme.Subtle, L1); return; }
            double mx = 1; foreach (var m in Months) mx = Math.Max(mx, Math.Max(m.inc, m.exp));
            mx = NiceMax(mx);
            using (var p = new Pen(Color.FromArgb(40, 255, 255, 255)))
                for (int i = 0; i <= 3; i++)
                {
                    int gy = plot.Bottom - plot.Height * i / 3;
                    g.DrawLine(p, plot.X, gy, plot.Right, gy);
                    Text(g, Eur(mx * i / 3), _fAx, new Rectangle(x, gy - Theme.Px(8), Theme.Px(52), Theme.Px(16)), Theme.Subtle, R1);
                }
            int n = Months.Count, slot = plot.Width / n, bw = Math.Max(4, Math.Min(Theme.Px(18), slot / 4));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            for (int i = 0; i < n; i++)
            {
                var (label, inc, exp) = Months[i];
                int cx = plot.X + slot * i + slot / 2;
                int hi = (int)(plot.Height * inc / mx), he = (int)(plot.Height * exp / mx);
                if (hi > 0) using (var b = new SolidBrush(Theme.Accent)) using (var pth = TopRound(new Rectangle(cx - bw - 2, plot.Bottom - hi, bw, hi), 4)) g.FillPath(b, pth);
                if (he > 0) using (var b = new SolidBrush(Red)) using (var pth = TopRound(new Rectangle(cx + 2, plot.Bottom - he, bw, he), 4)) g.FillPath(b, pth);
                Text(g, label, _fAx, new Rectangle(cx - slot / 2, plot.Bottom + Theme.Px(6), slot, Theme.Px(16)), Theme.Subtle, C1);
            }
            g.SmoothingMode = SmoothingMode.None;
            int ly = r.Bottom - Theme.Px(28), lx = plot.X;
            foreach (var (lab, c) in new[] { (LblInc, Theme.Accent), (LblExp, Red) })
            {
                using (var b = new SolidBrush(c)) g.FillRectangle(b, lx, ly + Theme.Px(4), Theme.Px(10), Theme.Px(10));
                Text(g, lab, _fAx, new Rectangle(lx + Theme.Px(14), ly, Theme.Px(90), Theme.Px(18)), Theme.Subtle, L1);
                lx += Theme.Px(100);
            }
        }

        static GraphicsPath TopRound(Rectangle r, int rad)
        {
            var p = new GraphicsPath(); rad = Math.Min(rad, Math.Min(r.Width / 2, r.Height));
            if (rad <= 0) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, rad * 2, rad * 2, 180, 90); p.AddArc(r.Right - rad * 2, r.Y, rad * 2, rad * 2, 270, 90);
            p.AddLine(r.Right, r.Y + rad, r.Right, r.Bottom); p.AddLine(r.Right, r.Bottom, r.X, r.Bottom); p.CloseFigure();
            return p;
        }

        static double NiceMax(double v)
        {
            double p = Math.Pow(10, Math.Floor(Math.Log10(v)));
            foreach (var k in new[] { 1, 1.5, 2, 2.5, 3, 4, 5, 6, 8, 10 }) if (k * p >= v) return k * p;
            return 10 * p;
        }
    }

    public class BankStatement : CanvasPanel
    {
        public sealed class Move { public string Id = "", Concept = "", Label = "", Desc = ""; public DateTime Local; public double Amount, BalanceAfter; }
        public List<Move> Moves = new List<Move>();
        public string SelectedId { get; private set; }
        public bool Selectable;
        public string LblBalance = "saldo {0}";
        public Func<DateTime, string> DayTitle = d => d.ToString("d MMMM yyyy");
        Font _fDay, _fT, _fS, _fA, _fB, _fIc;
        readonly List<(bool head, string day, double dayTotal, Move m, int y, int h)> _rows = new();

        public BankStatement()
        {
            _fDay = F(8.5f, FontStyle.Bold); _fT = F(9.75f, FontStyle.Bold); _fS = F(8.25f); _fA = F(10f, FontStyle.Bold); _fB = F(8.25f); _fIc = EmojiPicker.EmojiFont(13f);
            BackColor = Theme.Bg;
        }
        protected override void Dispose(bool disposing) { if (disposing) _fIc.Dispose(); base.Dispose(disposing); }

        public static (string icon, Color col) IconOf(string concept) => concept switch
        {
            "income" => ("🎫", Theme.Accent), "canon" => ("🛤", Color.FromArgb(251, 146, 60)), "energy" => ("⚡", Color.FromArgb(167, 139, 250)),
            "salary" => ("👷", Color.FromArgb(45, 212, 191)), "maintenance" => ("🔧", Color.FromArgb(120, 144, 226)), "purchase" => ("🚆", Theme.Accent),
            "other" => ("🔑", Color.FromArgb(120, 144, 226)), "prize" => ("🏆", Color.FromArgb(240, 196, 90)), "loan" => ("🏦", Color.FromArgb(170, 176, 181)),
            "adjustment" => ("⚙", Color.FromArgb(170, 176, 181)), _ => ("•", Color.FromArgb(170, 176, 181))
        };

        protected override int DoLayout(int w)
        {
            _rows.Clear();
            int y = Theme.Px(2);
            DateTime cur = DateTime.MinValue; int headIdx = -1; double tot = 0;
            foreach (var m in Moves)
            {
                if (m.Local.Date != cur)
                {
                    if (headIdx >= 0) { var hr = _rows[headIdx]; _rows[headIdx] = (true, hr.day, tot, null, hr.y, hr.h); }
                    cur = m.Local.Date; tot = 0;
                    headIdx = _rows.Count;
                    _rows.Add((true, DayTitle(cur), 0, null, y, Theme.Px(34))); y += Theme.Px(34);
                }
                tot += m.Amount;
                _rows.Add((false, null, 0, m, y, Theme.Px(52))); y += Theme.Px(52);
            }
            if (headIdx >= 0) { var hr = _rows[headIdx]; _rows[headIdx] = (true, hr.day, tot, null, hr.y, hr.h); }
            return Moves.Count == 0 ? 0 : y + Theme.Px(8);
        }

        protected override void DoPaint(Graphics g, int top)
        {
            int w = W, bottom = ClientSize.Height;
            var icons = new List<(string, Rectangle)>();
            foreach (var (head, day, dayTotal, m, y0, h) in _rows)
            {
                int y = y0 - top;
                if (y + h < 0 || y > bottom) continue;
                if (head)
                {
                    string t = (dayTotal >= 0 ? "+" : "−") + Math.Abs(dayTotal).ToString("N2", Es) + " €";
                    int tw = TW(t, _fDay) + 4;
                    Text(g, day, _fDay, new Rectangle(Theme.Px(4), y + Theme.Px(10), w - tw - 12, Theme.Px(20)), Theme.Subtle, L1);
                    Text(g, t, _fDay, new Rectangle(w - tw - Theme.Px(4), y + Theme.Px(10), tw, Theme.Px(20)), Theme.Subtle, R1);
                    using (var p = new Pen(Theme.Surface2)) g.DrawLine(p, 0, y + h - 2, w, y + h - 2);
                    continue;
                }
                var r = new Rectangle(0, y + Theme.Px(3), w, h - Theme.Px(6));
                bool sel = Selectable && m.Id == SelectedId;
                if (sel) { Round(g, r, Theme.Px(8), Color.FromArgb(52, 60, 56)); Border(g, r, Theme.Px(8), Theme.Accent); }
                var (ic, col) = IconOf(m.Concept);
                var ir = new Rectangle(Theme.Px(6), r.Y + (r.Height - Theme.Px(34)) / 2, Theme.Px(34), Theme.Px(34));
                Round(g, ir, Theme.Px(10), Color.FromArgb(48, col));
                icons.Add((ic, ir));
                string amt = (m.Amount >= 0 ? "+" : "−") + Math.Abs(m.Amount).ToString("N2", Es) + " €";
                string bal = string.Format(LblBalance, m.BalanceAfter.ToString("N2", Es) + " €");
                int bw = Theme.Px(150), aw = TW(amt, _fA) + 6;
                int tx = ir.Right + Theme.Px(12), tw2 = w - tx - bw - aw - Theme.Px(20);
                Text(g, m.Label, _fT, new Rectangle(tx, r.Y + Theme.Px(6), tw2, Theme.Px(20)), Theme.Text, L1);
                Text(g, m.Desc, _fS, new Rectangle(tx, r.Y + Theme.Px(25), tw2, Theme.Px(16)), Theme.Subtle, L1);
                Text(g, amt, _fA, new Rectangle(w - bw - aw - Theme.Px(10), r.Y, aw, r.Height), m.Amount < 0 ? Red : Theme.AccentHi, R1);
                Text(g, bal, _fB, new Rectangle(w - bw, r.Y, bw - Theme.Px(6), r.Height), Theme.Subtle, R1);
            }
            ColorText.DrawCells(g, icons, _fIc, Theme.Text, new Rectangle(0, 0, w, bottom));
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (!Selectable) return;
            int y = e.Y + ScrollY;
            foreach (var (head, _, _, m, y0, h) in _rows)
                if (!head && y >= y0 && y < y0 + h) { SelectedId = m.Id; Invalidate(); return; }
        }

        public void SetMoves(List<Move> moves) { Moves = moves; if (SelectedId != null && !moves.Exists(x => x.Id == SelectedId)) SelectedId = null; Relayout(true); }
    }
}
