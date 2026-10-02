// HISTORIAL DEL CARNÉ (Mi perfil): línea de tiempo en tarjetas, la más reciente arriba. Cada infracción con su
// código (A1, B6…), el detalle, la empresa, los puntos y el estado; las recuperaciones en verde.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SelectOR
{
    public class LicenseTimeline : CanvasPanel
    {
        public sealed class Entry
        {
            public DateTime Local; public string Code = "", CodeTag = "", Title = "", Detail = "", Company = "", Status = "", StatusText = "";
            public int Points;
        }
        public List<Entry> Entries = new List<Entry>();
        public string Empty = "Sin infracciones. ¡Buena conducción!", EmptySub = "", LblRecovery = "Recuperado";
        Font _fDate, _fTime, _fTag, _fTitle, _fDet, _fPts, _fPill, _fEmpty, _fEmptyIc;
        readonly List<(Entry e, Rectangle r)> _rows = new();

        public LicenseTimeline()
        {
            AutoHeight = true;
            _fDate = F(9f, FontStyle.Bold); _fTime = F(8f); _fTag = F(8f, FontStyle.Bold); _fTitle = F(9.75f, FontStyle.Bold); _fDet = F(8.25f);
            _fPts = F(11f, FontStyle.Bold); _fPill = F(7.75f, FontStyle.Bold); _fEmpty = F(10f, FontStyle.Bold); _fEmptyIc = EmojiPicker.EmojiFont(22f);
        }
        protected override void Dispose(bool disposing) { if (disposing) _fEmptyIc.Dispose(); base.Dispose(disposing); }

        public void SetEntries(List<Entry> list) { Entries = list; Relayout(true); }

        protected override int DoLayout(int w)
        {
            _rows.Clear();
            if (Entries.Count == 0) return Theme.Px(84);
            int y = 0, h = Theme.Px(66), gap = Theme.Px(8);
            foreach (var e in Entries) { _rows.Add((e, new Rectangle(0, y, w, h))); y += h + gap; }
            return y;
        }

        static Color Tint(Entry e) => e.Code == "recovery" ? Carne.Green : e.Status == "annulled" ? Color.FromArgb(150, 156, 160) : e.Status == "pending" ? Carne.Gold : Carne.Red;

        protected override void DoPaint(Graphics g, int top)
        {
            if (Entries.Count == 0)
            {
                var r = new Rectangle(0, 0, W - 1, Theme.Px(84) - 1);
                Round(g, r, Theme.Px(12), Color.FromArgb(40, 58, 46));
                Border(g, r, Theme.Px(12), Color.FromArgb(70, Carne.Green));
                var ic = new Rectangle(r.X + Theme.Px(18), r.Y + (r.Height - Theme.Px(40)) / 2, Theme.Px(40), Theme.Px(40));
                ColorText.DrawCells(g, new[] { ("🛡️", ic) }, _fEmptyIc, Theme.Text, r);
                int tx = ic.Right + Theme.Px(14);
                Text(g, Empty, _fEmpty, new Rectangle(tx, r.Y + Theme.Px(18), r.Width - tx - Theme.Px(14), Theme.Px(22)), Carne.Green, L1);
                Text(g, EmptySub, _fDet, new Rectangle(tx, r.Y + Theme.Px(42), r.Width - tx - Theme.Px(14), Theme.Px(18)), Theme.Subtle, L1);
                return;
            }
            int lineX = Theme.Px(86);
            // raíl de la línea de tiempo
            if (_rows.Count > 1)
                using (var p = new Pen(Color.FromArgb(78, 84, 90), Theme.Px(2)))
                    g.DrawLine(p, lineX, _rows[0].r.Y + _rows[0].r.Height / 2, lineX, _rows[_rows.Count - 1].r.Y + _rows[_rows.Count - 1].r.Height / 2);
            var ci = System.Globalization.CultureInfo.GetCultureInfo(I18n.English ? "en-GB" : "es-ES");
            foreach (var (e, r0) in _rows)
            {
                var r = r0; r.Offset(0, -top);
                Color tint = Tint(e);
                int cy = r.Y + r.Height / 2;
                // fecha a la izquierda
                Text(g, e.Local == DateTime.MinValue ? "—" : e.Local.ToString("d MMM", ci).TrimEnd('.'), _fDate, new Rectangle(0, cy - Theme.Px(17), lineX - Theme.Px(14), Theme.Px(18)), Theme.Text, R1);
                Text(g, e.Local == DateTime.MinValue ? "" : e.Local.ToString(e.Local.Year == DateTime.Now.Year ? "HH:mm" : "yyyy", ci), _fTime, new Rectangle(0, cy + Theme.Px(1), lineX - Theme.Px(14), Theme.Px(16)), Theme.Subtle, R1);
                // punto
                int d = Theme.Px(12);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var b = new SolidBrush(Theme.Bg)) g.FillEllipse(b, lineX - d / 2 - 2, cy - d / 2 - 2, d + 4, d + 4);
                using (var b = new SolidBrush(tint)) g.FillEllipse(b, lineX - d / 2, cy - d / 2, d, d);
                g.SmoothingMode = SmoothingMode.None;
                // tarjeta
                var c = new Rectangle(lineX + Theme.Px(16), r.Y, r.Right - lineX - Theme.Px(17), r.Height);
                Round(g, c, Theme.Px(10), CanvasPanel_Mix(CardC, tint, e.Status == "annulled" ? 0.02f : 0.07f));
                using (var b = new SolidBrush(tint)) g.FillRectangle(b, c.X, c.Y + Theme.Px(10), Theme.Px(3), c.Height - Theme.Px(20));
                int x = c.X + Theme.Px(16);
                // puntos y estado a la derecha
                string pts = Carne.PointsText(e.Points);
                int pw = Math.Max(TW(pts, _fPts), TW(e.StatusText, _fPill) + Theme.Px(16)) + Theme.Px(4);
                int rx = c.Right - Theme.Px(14) - pw;
                bool struck = e.Status == "annulled";
                Text(g, pts, struck ? _fDet : _fPts, new Rectangle(rx, c.Y + Theme.Px(10), pw, Theme.Px(22)), e.Points > 0 ? Carne.Green : struck ? Theme.Subtle : Carne.Red, R1);
                if (e.StatusText.Length > 0)
                {
                    int sw = TW(e.StatusText, _fPill) + Theme.Px(16);
                    var sr = new Rectangle(c.Right - Theme.Px(14) - sw, c.Bottom - Theme.Px(28), sw, Theme.Px(18));
                    Round(g, sr, Theme.Px(9), Color.FromArgb(44, tint)); Text(g, e.StatusText, _fPill, sr, tint, C1);
                }
                // código + título
                int tw = TW(e.CodeTag, _fTag) + Theme.Px(14);
                var tr = new Rectangle(x, c.Y + Theme.Px(12), tw, Theme.Px(20));
                Round(g, tr, Theme.Px(6), Color.FromArgb(56, tint)); Text(g, e.CodeTag, _fTag, tr, tint, C1);
                Text(g, e.Title, _fTitle, new Rectangle(tr.Right + Theme.Px(10), tr.Y, rx - tr.Right - Theme.Px(18), tr.Height), struck ? Theme.Subtle : Theme.Text, L1);
                string det = e.Detail;
                if (e.Company.Length > 0) det = det.Length > 0 ? e.Company + "  ·  " + det : e.Company;
                Text(g, det, _fDet, new Rectangle(x, c.Y + Theme.Px(38), rx - x - Theme.Px(10), Theme.Px(18)), Theme.Subtle, L1);
            }
        }

        static Color CanvasPanel_Mix(Color a, Color b, float t) => Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
    }
}
