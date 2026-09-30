// Aviso de premios de la Liga de empresas: trofeo, el mes, y una tarjeta por cada empresa tuya con sus
// premios (medalla del puesto o icono de la categoría) y lo cobrado. «Ver campeones» lleva a Ranking →
// Campeones; «Cerrar» (o Esc) lo cierra.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SelectOR
{
    public sealed class LeagueAwardsDialog : FancyDialog
    {
        public sealed class Prize { public string Icon, Label; public double Amount; public Color Tint; }
        public sealed class CompanyPrizes { public string Name; public double Total; public List<Prize> Prizes = new(); }

        public static readonly Color Silver = Color.FromArgb(196, 202, 208), Bronze = Color.FromArgb(214, 146, 88);

        readonly string _month;
        readonly List<CompanyPrizes> _cos;
        readonly Func<double, string> _eur;
        readonly int _more;
        public bool WantsChampions { get; private set; }

        static int W => Theme.Px(540); static int Pad => Theme.Px(24); static int Trophy => Theme.Px(64);
        static int CardPad => Theme.Px(14); static int LineH => Theme.Px(26); static int NameH => Theme.Px(28); static int Gap => Theme.Px(10);
        const int MaxCompanies = 5;

        public LeagueAwardsDialog(string month, List<CompanyPrizes> companies, Func<double, string> eur)
        {
            _month = month ?? ""; _eur = eur ?? (v => v.ToString("N0"));
            _cos = companies.Count > MaxCompanies ? companies.GetRange(0, MaxCompanies) : companies;
            _more = companies.Count - _cos.Count;
            AccentColor = Theme.Gold;
            Text = I18n.T("Liga de empresas");

            int h = HeaderBottom() + 6;
            foreach (var c in _cos) h += CardHeight(c) + Gap;
            if (_more > 0) h += 22;
            if (_cos.Count > 1 || _more > 0) h += 34;                  // total de todas
            h += 30;                                                     // nota
            h += 38 + Pad;                                               // botones
            ClientSize = new Size(W, h);

            var ver = PrimaryButton(I18n.T("Ver campeones"), 180);
            var close = SecondaryButton(I18n.T("Cerrar"), 120);
            ver.Location = new Point(W - Pad - ver.Width, h - Pad - ver.Height);
            close.Location = new Point(ver.Left - 10 - close.Width, ver.Top);
            ver.Click += (s, e) => { WantsChampions = true; DialogResult = DialogResult.OK; Close(); };
            close.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(ver); Controls.Add(close);
        }

        static int HeaderBottom() => Band + Pad + Trophy + 16;
        static int CardHeight(CompanyPrizes c) => CardPad + NameH + c.Prizes.Count * LineH + CardPad - 4;

        protected override void PaintContent(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var flL = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
            var flR = TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;

            // ---- Cabecera: trofeo con confeti, enhorabuena y mes ----
            int y0 = Band + Pad;
            var tr = new Rectangle(Pad, y0, Trophy, Trophy);
            Confetti(g, tr);
            DrawIconPill(g, tr, "🏆", Theme.Gold);
            int tx = tr.Right + 18, tw = W - tx - Pad;
            using (var fT = Theme.Font(16f, FontStyle.Bold))
                TextRenderer.DrawText(g, I18n.T("¡Enhorabuena!"), fT, new Rectangle(tx, y0 + 4, tw, 32), Theme.Text, flL);
            using (var fS = Theme.Font(10f))
                TextRenderer.DrawText(g, string.Format(I18n.T("Tus empresas han ganado premios en la Liga de {0}."), _month), fS,
                    new Rectangle(tx, y0 + 38, tw, Theme.Px(42)), Theme.Subtle,
                    TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);

            // ---- Una tarjeta por empresa ----
            int y = HeaderBottom() + 6;
            using var fName = Theme.Font(11f, FontStyle.Bold);
            using var fLine = Theme.Font(9.75f);
            using var fAmt = Theme.Font(9.75f, FontStyle.Bold);
            using var fEmoji = new Font("Segoe UI Emoji", 13f, GraphicsUnit.Pixel);
            foreach (var c in _cos)
            {
                var card = new Rectangle(Pad, y, W - Pad * 2, CardHeight(c));
                Theme.FillRound(g, card, 10, Theme.Surface2);
                using (var b = new SolidBrush(Theme.Gold)) g.FillRectangle(b, card.X, card.Y + 10, 3, card.Height - 20);
                int cx = card.X + CardPad + 4, cw = card.Width - CardPad * 2 - 4;
                int cy = card.Y + CardPad - 4;
                TextRenderer.DrawText(g, c.Name, fName, new Rectangle(cx, cy, cw - 150, NameH), Theme.Text, flL);
                TextRenderer.DrawText(g, "+" + _eur(c.Total), fName, new Rectangle(cx, cy, cw, NameH), Theme.AccentHi, flR);
                cy += NameH;
                foreach (var p in c.Prizes)
                {
                    var chip = new Rectangle(cx, cy + 3, LineH - 6, LineH - 6);
                    using (var pth = Theme.Round(chip, 6)) using (var b = new SolidBrush(Color.FromArgb(70, p.Tint))) g.FillPath(b, pth);
                    TextRenderer.DrawText(g, p.Icon, fEmoji, chip, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                    TextRenderer.DrawText(g, p.Label, fLine, new Rectangle(chip.Right + 10, cy, cw - chip.Width - 160, LineH), Theme.Text, flL);
                    TextRenderer.DrawText(g, _eur(p.Amount), fAmt, new Rectangle(cx, cy, cw, LineH), Theme.Subtle, flR);
                    cy += LineH;
                }
                y += card.Height + Gap;
            }
            if (_more > 0)
            {
                TextRenderer.DrawText(g, string.Format(I18n.T("… y {0} empresas más."), _more), fLine, new Rectangle(Pad + 4, y - 4, W - Pad * 2, 22), Theme.Subtle, flL);
                y += 22;
            }

            // ---- Total de todas ----
            if (_cos.Count > 1 || _more > 0)
            {
                double tot = 0; foreach (var c in _cos) tot += c.Total;
                using var pen = new Pen(Theme.Border);
                g.DrawLine(pen, Pad, y + 2, W - Pad, y + 2);
                TextRenderer.DrawText(g, I18n.T("Total cobrado"), fName, new Rectangle(Pad + 4, y + 6, W - Pad * 2, 26), Theme.Text, flL);
                TextRenderer.DrawText(g, _eur(tot), fName, new Rectangle(Pad, y + 6, W - Pad * 2 - 4, 26), Theme.Gold, flR);
                y += 34;
            }

            using var fNote = Theme.Font(9f);
            TextRenderer.DrawText(g, I18n.T("El dinero ya está en la tesorería de la empresa."), fNote, new Rectangle(Pad + 4, y, W - Pad * 2, 24), Theme.Subtle, flL);
        }

        // Confeti fijo alrededor del trofeo (siempre igual: no «baila» al repintar).
        static void Confetti(Graphics g, Rectangle around)
        {
            var rnd = new Random(7);
            Color[] cols = { Theme.Gold, Theme.AccentHi, Color.FromArgb(120, 170, 235), Color.FromArgb(235, 120, 140), Silver };
            for (int i = 0; i < 28; i++)
            {
                double a = rnd.NextDouble() * Math.PI * 2, d = around.Width * (0.62 + rnd.NextDouble() * 0.28);
                float x = (float)(around.X + around.Width / 2.0 + Math.Cos(a) * d), y = (float)(around.Y + around.Height / 2.0 + Math.Sin(a) * d * 0.8);
                if (y < Band + 4 || x > around.Right + 6) continue;   // no pisar el texto de la derecha
                var st = g.Save();
                g.TranslateTransform(x, y); g.RotateTransform((float)(rnd.NextDouble() * 180));
                using var b = new SolidBrush(Color.FromArgb(200, cols[i % cols.Length]));
                if (i % 3 == 0) g.FillEllipse(b, -2.5f, -2.5f, 5, 5); else g.FillRectangle(b, -4, -1.5f, 8, 3);
                g.Restore(st);
            }
        }
    }
}
