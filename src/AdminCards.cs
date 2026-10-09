// Tarjetas en lugar de tablas: las cuotas de un préstamo (cuadro de amortización) y los usuarios
// (Usuarios, superadmin). Se dibujan a mano, solo lo visible, como el resto de listas en tarjetas.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace SelectOR
{
    public sealed class InstallmentItem
    {
        public int N, Months;
        public DateTime Due = DateTime.MinValue, PaidAt = DateTime.MinValue;
        public double Payment, Interest, Principal, LateFee;
        public string Status = "pending";
    }

    // Cuadro de amortización: una tarjeta por cuota, en rejilla.
    public sealed class InstallmentCardList : CardListBase
    {
        static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-ES");
        static readonly Color Red = Color.FromArgb(229, 115, 115);
        readonly Font _fN = Theme.Font(9f, FontStyle.Bold), _fPay = Theme.Font(12f, FontStyle.Bold), _fText = Theme.Font(8.5f),
                      _fPill = Theme.Font(8f, FontStyle.Bold);
        public InstallmentCardList() { EmptyText = ""; }
        protected override int MinCardW => Theme.Px(240);
        protected override int CardH => Theme.Px(86);
        protected override int Gap => Theme.Px(8);
        protected override void Dispose(bool disposing)
        {
            if (disposing) foreach (var f in new[] { _fN, _fPay, _fText, _fPill }) f.Dispose();
            base.Dispose(disposing);
        }
        static string Eur(double v) => v.ToString(Math.Abs(v % 1) < 0.005 ? "N0" : "N2", Es) + " €";

        protected override void PaintCard(Graphics g, int i, Rectangle rc, bool selected, bool hover)
        {
            if (Items[i] is not InstallmentItem it) return;
            var (st, sc) = it.Status switch
            {
                "paid" => (I18n.T("Pagada"), Theme.AccentHi),
                "late" => (I18n.T("Impagada"), Red),
                "cancelled" => (I18n.T("Cancelada"), Theme.Subtle),
                _ => (I18n.T("Pendiente"), Theme.Subtle)
            };
            bool next = it.Status == "pending" && i > 0 && Items[i - 1] is InstallmentItem p && p.Status != "pending" || (it.Status == "pending" && i == 0);
            Fill(g, rc, Theme.Px(8), selected || hover ? Color.FromArgb(50, 54, 57) : Theme.Surface);
            if (selected) Stroke(g, rc, Theme.Px(8), Theme.Accent);
            else if (next) Stroke(g, rc, Theme.Px(8), Color.FromArgb(120, Theme.Accent), 1.2f);
            using (var sb = new SolidBrush(sc)) g.FillRectangle(sb, rc.X, rc.Y + Theme.Px(10), Theme.Px(3), rc.Height - Theme.Px(20));
            int x = rc.X + Theme.Px(12), r = rc.Right - Theme.Px(10), y = rc.Y + Theme.Px(8);

            // 1) nº de cuota · estado
            string n = string.Format(I18n.T("Cuota {0}/{1}"), it.N, it.Months);
            TextRenderer.DrawText(g, n, _fN, new Rectangle(x, y, r - x, Theme.Px(20)), Theme.Subtle, L1);
            int pw = TW(st, _fPill) + Theme.Px(14), ph = Theme.Px(18);
            var pill = new Rectangle(r - pw, y + 1, pw, ph);
            Fill(g, pill, ph / 2, Color.FromArgb(40, sc));
            TextRenderer.DrawText(g, st, _fPill, pill, sc, C1);
            y += Theme.Px(22);
            // 2) importe · fecha
            TextRenderer.DrawText(g, Eur(it.Payment), _fPay, new Rectangle(x, y, r - x, Theme.Px(24)), it.Status == "cancelled" ? Theme.Subtle : Theme.Text, L1);
            string when = it.Status == "paid" && it.PaidAt != DateTime.MinValue
                ? string.Format(I18n.T("pagada el {0}"), it.PaidAt.ToString("dd/MM/yyyy", Es))
                : string.Format(I18n.T("vence el {0}"), it.Due == DateTime.MinValue ? "—" : it.Due.ToString("dd/MM/yyyy", Es));
            TextRenderer.DrawText(g, when, _fText, new Rectangle(x, y, r - x, Theme.Px(24)), it.Status == "late" ? Red : Theme.Subtle, R1);
            y += Theme.Px(28);
            // 3) capital · intereses · recargo
            string d = string.Format(I18n.T("capital {0} · intereses {1}"), Eur(it.Principal), Eur(it.Interest));
            if (it.LateFee > 0) d = string.Format(I18n.T("recargo {0}"), Eur(it.LateFee)) + " · " + d;   // lo primero, para que se vea siempre
            TextRenderer.DrawText(g, d, _fText, new Rectangle(x, y, r - x, Theme.Px(18)), it.LateFee > 0 ? Red : Theme.Subtle, L1);
        }
    }

    public sealed class UserItem
    {
        public int Index;                 // posición en las listas del formulario (ID, clave…)
        public string Id = "", Name = "", Email = "", Key = "";
        public int Companies;
        public DateTime Created = DateTime.MinValue;
        public bool Self;
        public int Points = -1;                       // puntos del carné (−1: el servidor no los da, falta usuarios-carne-empresas.sql)
        public DateTime? SuspendedUntil;              // carné suspendido hasta (UTC)
        public List<string> CompanyNames = new();     // sus empresas, por orden alfabético
        internal string Search;
        public string SearchText => Search ??= (Name + " " + Id + " " + Key + " " + Email + " " + string.Join(" ", CompanyNames)).ToLowerInvariant();
    }

    // Usuarios (superadmin): una tarjeta por usuario con su ID, su clave de recuperación, sus empresas y su alta.
    public sealed class UserCardList : CardListBase
    {
        static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-ES");
        readonly Font _fName = Theme.Font(10.5f, FontStyle.Bold), _fText = Theme.Font(8.5f), _fKey = new Font("Consolas", 10.5f, FontStyle.Bold),
                      _fAv = Theme.Font(10f, FontStyle.Bold), _fPill = Theme.Font(8f, FontStyle.Bold);
        public UserCardList() { EmptyText = I18n.T("No hay usuarios."); }
        protected override int MinCardW => Theme.Px(400);
        protected override int CardH => Theme.Px(116);
        protected override int Gap => Theme.Px(8);
        protected override void Dispose(bool disposing)
        {
            if (disposing) foreach (var f in new[] { _fName, _fText, _fKey, _fAv, _fPill }) f.Dispose();
            base.Dispose(disposing);
        }

        static string Initials(string name)
        {
            var p = (name ?? "").Split(new[] { ' ', '_', '.', '-' }, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length == 0) return "?";
            return (p.Length == 1 ? p[0].Substring(0, Math.Min(2, p[0].Length)) : "" + p[0][0] + p[1][0]).ToUpperInvariant();
        }

        protected override void PaintCard(Graphics g, int i, Rectangle rc, bool selected, bool hover)
        {
            if (Items[i] is not UserItem u) return;
            Fill(g, rc, Theme.Px(10), selected || hover ? Color.FromArgb(50, 54, 57) : Theme.Surface);
            if (selected) Stroke(g, rc, Theme.Px(10), Theme.Accent);
            int pad = Theme.Px(14), x = rc.X + pad, y = rc.Y + Theme.Px(12), r = rc.Right - pad;
            int av = Theme.Px(40);
            var ar = new Rectangle(x, rc.Y + (rc.Height - av) / 2, av, av);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var b = new SolidBrush(u.Self ? Color.FromArgb(60, Theme.Accent) : Theme.Surface2)) g.FillEllipse(b, ar);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
            TextRenderer.DrawText(g, Initials(u.Name), _fAv, ar, Theme.AccentHi, C1);
            int tx = ar.Right + Theme.Px(12);

            // derecha: empresas, carné y alta
            string co = string.Format(I18n.T(u.Companies == 1 ? "{0} empresa" : "{0} empresas"), u.Companies);
            int pw = TW(co, _fPill) + Theme.Px(16), ph = Theme.Px(20);
            var pill = new Rectangle(r - pw, y, pw, ph);
            Fill(g, pill, ph / 2, Color.FromArgb(40, u.Companies > 0 ? Theme.Accent : Theme.Subtle));
            TextRenderer.DrawText(g, co, _fPill, pill, u.Companies > 0 ? Theme.AccentHi : Theme.Subtle, C1);
            if (u.Points >= 0)
            {
                // carné: sus puntos con el color del carné (suspendido, en rojo)
                bool susp = u.SuspendedUntil.HasValue;
                string ct = susp ? I18n.T("Carné suspendido") : "🛡 " + u.Points + " " + I18n.T(u.Points == 1 ? "punto" : "puntos");
                var cc = susp ? Carne.PointsColor(0) : Carne.PointsColor(u.Points);
                int cw = TW(ct, _fPill) + Theme.Px(16);
                var cp = new Rectangle(pill.Left - Theme.Px(6) - cw, y, cw, ph);
                Fill(g, cp, ph / 2, Color.FromArgb(40, cc));
                TextRenderer.DrawText(g, ct, _fPill, cp, cc, C1);
                pill = Rectangle.Union(pill, cp);   // el nombre se corta antes de las dos pastillas
            }
            if (u.Created != DateTime.MinValue)
                TextRenderer.DrawText(g, string.Format(I18n.T("alta {0}"), u.Created.ToString("dd/MM/yyyy", Es)), _fText,
                    new Rectangle(r - Theme.Px(150), y + Theme.Px(24), Theme.Px(150), Theme.Px(18)), Theme.Subtle, R1);

            int tw = pill.Left - tx - Theme.Px(10);
            TextRenderer.DrawText(g, u.Name + (u.Self ? "  ★" : ""), _fName, new Rectangle(tx, y, tw, Theme.Px(22)), u.Self ? Theme.Accent : Theme.Text, L1);
            TextRenderer.DrawText(g, "ID " + u.Id, _fText, new Rectangle(tx, y + Theme.Px(24), r - Theme.Px(156) - tx, Theme.Px(18)), Theme.Subtle, L1);
            TextRenderer.DrawText(g, "🗝 " + (u.Key.Length > 0 ? u.Key : "—"), _fKey, new Rectangle(tx, y + Theme.Px(44), r - tx, Theme.Px(22)), Theme.AccentHi, L1);
            // sus empresas, por nombre
            if (u.Points >= 0)
                TextRenderer.DrawText(g, u.CompanyNames.Count > 0 ? "🏢 " + string.Join("  ·  ", u.CompanyNames) : I18n.T("Sin empresa"), _fText,
                    new Rectangle(tx, y + Theme.Px(70), r - tx, Theme.Px(18)), u.CompanyNames.Count > 0 ? Theme.Text : Theme.Subtle, L1 | TextFormatFlags.EndEllipsis);
        }
    }
}
