// Revisión (superadmin): las infracciones del carné en tarjetas, una por fila, con todo a la vista:
// código y nombre, maquinista, empresa, fecha, tren, recorrido, detalle, puntos, estado y quién la revisó.

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SelectOR
{
    public sealed class ReviewItem
    {
        public string Id = "", Status = "", DriverId = "", Code = "";
        public string Driver = "", Company = "", When = "", Train = "", Route = "", Detail = "", Reviewer = "";
        public int Points;
        public double AtS = double.NaN;   // segundos desde el inicio del servicio hasta la infracción (NaN = no se sabe)
        // «a los 12 min 30 s del inicio del servicio»
        public string AtText
        {
            get
            {
                if (double.IsNaN(AtS) || AtS < 0) return "";
                int t = (int)Math.Round(AtS), h = t / 3600, m = t % 3600 / 60, s = t % 60;
                string d = h > 0 ? $"{h} h {m:00} min" : m > 0 ? $"{m} min {s:00} s" : $"{s} s";
                return string.Format(I18n.T("a los {0} del inicio del servicio"), d);
            }
        }
        internal string Search;
        public string SearchText => Search ??= string.Join(" ", Driver, Company, When, Train, Route, Detail, Reviewer,
                                                            Carne.Label(Code), Carne.StatusName(Status)).ToLowerInvariant();
    }

    public sealed class ReviewCardList : CardListBase
    {
        readonly Font _fCode = Theme.Font(11f, FontStyle.Bold), _fTitle = Theme.Font(10f, FontStyle.Bold), _fText = Theme.Font(9f),
                      _fBold = Theme.Font(9f, FontStyle.Bold), _fPts = Theme.Font(15f, FontStyle.Bold), _fPill = Theme.Font(8.5f, FontStyle.Bold),
                      _fSmall = Theme.Font(8.25f);
        static readonly Color Amber = Color.FromArgb(232, 178, 80), Blue = Color.FromArgb(120, 144, 226);

        public ReviewCardList() { EmptyText = I18n.T("No hay infracciones."); }

        protected override int MinCardW => Theme.Px(600);
        protected override int MaxCols => 1;
        protected override int CardH => Theme.Px(94);
        protected override int Gap => Theme.Px(8);

        protected override void Dispose(bool disposing)
        {
            if (disposing) foreach (var f in new[] { _fCode, _fTitle, _fText, _fBold, _fPts, _fPill, _fSmall }) f.Dispose();
            base.Dispose(disposing);
        }

        protected override void PaintCard(Graphics g, int i, Rectangle rc, bool selected, bool hover)
        {
            if (Items[i] is not ReviewItem it) return;
            bool annulled = it.Status == "annulled", pending = it.Status == "pending";
            Fill(g, rc, Theme.Px(10), selected || hover ? Color.FromArgb(50, 54, 57) : Theme.Surface);
            if (selected) Stroke(g, rc, Theme.Px(10), Theme.Accent);
            // Franja: ámbar pendiente, gris anulada, rojo aplicada
            var stripe = pending ? Amber : annulled ? Theme.Subtle : Carne.Red;
            using (var sb = new SolidBrush(stripe)) g.FillRectangle(sb, rc.X, rc.Y + Theme.Px(12), Theme.Px(3), rc.Height - Theme.Px(24));

            int pad = Theme.Px(14), x = rc.X + pad + Theme.Px(2), y = rc.Y + Theme.Px(12);
            // Código (A1…B7) en su cuadro
            bool isA = it.Code is "jump" or "speed_avg" or "time_accel" or "autopilot";
            var codeR = new Rectangle(x, y + Theme.Px(2), Theme.Px(46), Theme.Px(46));
            var codeC = isA ? Carne.Red : Amber;
            Fill(g, codeR, Theme.Px(8), Color.FromArgb(annulled ? 30 : 45, codeC));
            Stroke(g, codeR, Theme.Px(8), Color.FromArgb(annulled ? 90 : 200, codeC), 1.2f);
            TextRenderer.DrawText(g, Carne.Code(it.Code), _fCode, codeR, annulled ? Theme.Subtle : codeC, C1);
            int tx = codeR.Right + Theme.Px(14);

            // Derecha: puntos y estado
            int rightW = Theme.Px(190), right = rc.Right - rightW;
            var pr = new Rectangle(right, y - Theme.Px(2), rightW - pad, Theme.Px(30));
            TextRenderer.DrawText(g, Carne.PointsText(it.Points), _fPts, pr, annulled ? Theme.Subtle : Carne.Red, R1);
            string st = Carne.StatusName(it.Status);
            int pw = TW(st, _fPill) + Theme.Px(20), ph = Theme.Px(22);
            var pill = new Rectangle(rc.Right - pad - pw, pr.Bottom + Theme.Px(4), pw, ph);
            var sc = Carne.StatusColor(it.Status);
            Fill(g, pill, ph / 2, Color.FromArgb(45, sc));
            TextRenderer.DrawText(g, st, _fPill, pill, sc, C1);
            if (!pending && it.Reviewer.Length > 0)
                TextRenderer.DrawText(g, string.Format(I18n.T("revisada por {0}"), it.Reviewer), _fSmall,
                    new Rectangle(right, pill.Bottom + Theme.Px(2), rightW - pad, Theme.Px(18)), Theme.Subtle, R1);
            using (var sep = new Pen(Theme.Surface2)) g.DrawLine(sep, right - Theme.Px(8), rc.Y + Theme.Px(12), right - Theme.Px(8), rc.Bottom - Theme.Px(12));

            int tw = right - Theme.Px(18) - tx;
            // 1) nombre de la infracción
            int nw = Math.Min(TW(Carne.Name(it.Code), _fTitle), tw);
            TextRenderer.DrawText(g, Carne.Name(it.Code), _fTitle, new Rectangle(tx, y, nw + 2, Theme.Px(22)), annulled ? Theme.Subtle : Theme.Text, L1);
            // Cuándo ocurrió: tiempo desde el inicio del servicio
            string at = it.AtText;
            if (at.Length > 0 && tx + nw + Theme.Px(24) < tx + tw)
            {
                int aw = TW("⏱ " + at, _fBold) + Theme.Px(16), ah = Theme.Px(20);
                var ar = new Rectangle(tx + nw + Theme.Px(12), y + 1, Math.Min(aw, tx + tw - (tx + nw + Theme.Px(12))), ah);
                Fill(g, ar, ah / 2, Color.FromArgb(40, Blue));
                TextRenderer.DrawText(g, "⏱ " + at, _fBold, ar, annulled ? Theme.Subtle : Blue, C1);
            }
            // 2) maquinista · empresa · fecha
            int ly = y + Theme.Px(24), lx = tx;
            int dw = Math.Min(TW(it.Driver, _fBold), tw / 2);
            TextRenderer.DrawText(g, it.Driver, _fBold, new Rectangle(lx, ly, dw, Theme.Px(20)), Theme.Text, L1);
            lx += dw + Theme.Px(8);
            string meta = "🏢 " + it.Company + "    🕒 " + it.When;
            if (lx < tx + tw) TextRenderer.DrawText(g, meta, _fText, new Rectangle(lx, ly, tx + tw - lx, Theme.Px(20)), Theme.Subtle, L1);
            // 3) tren · recorrido · detalle
            string l3 = "🚆 " + (it.Train.Length > 0 ? it.Train : "—");
            if (it.Route.Length > 0) l3 += "    🛤 " + it.Route;
            if (it.Detail.Length > 0) l3 += "    ·  " + it.Detail;
            TextRenderer.DrawText(g, l3, _fText, new Rectangle(tx, ly + Theme.Px(22), tw, Theme.Px(20)), Theme.Subtle, L1);
        }
    }
}
