// RANKING (podios), SOCIOS (carnets) y MI PERFIL (carnet de maquinista, insignias, trenes y rutas), dibujados
// a mano: solo lo visible, en doble búfer y sin un control por elemento.
//
//  · CanvasPanel: base común. Con AutoHeight = false se desplaza (Ranking, Socios); con true toma el alto
//    de su contenido (bloques dentro de la página de Mi perfil, que es la que se desplaza).
//  · PodiumBoard: título, chips, podio de los tres primeros, tarjetas del resto y tarjetas de líderes.
//  · ChampionsBoard: una tarjeta por mes con los premiados, más el palmarés de tus empresas.
//  · MemberCardGrid: los socios como carnets (rol, carné por puntos, rango, servicios, km y neto).
//  · ProfileHero, BadgeGrid, TrainTopList, RouteBars: los bloques de Mi perfil.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace SelectOR
{
    public abstract class CanvasPanel : Panel
    {
        public bool AutoHeight;
        protected int ContentH;
        int _layoutW = -1;
        protected static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-ES");
        protected const TextFormatFlags L1 = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;
        protected const TextFormatFlags C1 = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis;
        protected const TextFormatFlags R1 = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.EndEllipsis;
        protected const TextFormatFlags Wrap = TextFormatFlags.NoPadding | TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl;
        readonly List<Font> _fonts = new List<Font>();
        protected Font F(float size, FontStyle st = FontStyle.Regular) { var f = Theme.Font(size, st); _fonts.Add(f); return f; }
        public string EmptyText;

        protected CanvasPanel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = Theme.Bg;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (!AutoHeight) { AutoScroll = true; Native.UseDarkScrollBars(this); }
            Relayout(true);
        }

        protected override void Dispose(bool disposing) { if (disposing) foreach (var f in _fonts) f.Dispose(); base.Dispose(disposing); }

        protected int ScrollY => AutoHeight ? 0 : -AutoScrollPosition.Y;
        protected int W => Math.Max(120, ClientSize.Width - (AutoHeight ? 0 : Theme.Px(4)));

        // El contenido ha cambiado: se vuelve a maquetar.
        public void Relayout(bool force = false)
        {
            if (!force && W == _layoutW) return;
            _layoutW = W;
            ContentH = DoLayout(W);
            if (AutoHeight) { if (Height != ContentH) Height = Math.Max(1, ContentH); }
            else AutoScrollMinSize = new Size(0, ContentH);
            Invalidate();
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); Relayout(); }
        protected override void OnScroll(ScrollEventArgs se) { base.OnScroll(se); Invalidate(); }
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (AutoHeight) { base.OnMouseWheel(e); return; }
            int max = Math.Max(0, ContentH - ClientSize.Height);
            int y = Math.Max(0, Math.Min(max, ScrollY - e.Delta * Theme.Px(120) / 120));
            if (y != ScrollY) { AutoScrollPosition = new Point(0, y); Invalidate(); }
        }

        protected abstract int DoLayout(int w);
        protected abstract void DoPaint(Graphics g, int top);

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            if (EmptyText != null)
            {
                TextRenderer.DrawText(g, EmptyText, Font, new Rectangle(0, Theme.Px(24), ClientSize.Width, Theme.Px(30)), Theme.Subtle, TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak);
                return;
            }
            DoPaint(g, ScrollY);
        }

        // --- ayudas de dibujo ---
        protected static void Round(Graphics g, Rectangle r, int radius, Color c) { g.SmoothingMode = SmoothingMode.AntiAlias; Theme.FillRound(g, r, radius, c); g.SmoothingMode = SmoothingMode.None; }
        protected static void Border(Graphics g, Rectangle r, int radius, Color c, float w = 1f) { g.SmoothingMode = SmoothingMode.AntiAlias; Theme.DrawRoundBorder(g, r, radius, c, w); g.SmoothingMode = SmoothingMode.None; }
        protected static void Text(Graphics g, string s, Font f, Rectangle r, Color c, TextFormatFlags fl) { if (!string.IsNullOrEmpty(s) && r.Width > 0) TextRenderer.DrawText(g, s, f, r, c, fl); }
        protected static int TW(string s, Font f) => string.IsNullOrEmpty(s) ? 0 : TextRenderer.MeasureText(s, f, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
        protected static string Initials(string name)
        {
            var p = (name ?? "").Split(new[] { ' ', '_', '.', '-' }, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length == 0) return "?";
            return (p.Length == 1 ? p[0].Substring(0, Math.Min(2, p[0].Length)) : "" + p[0][0] + p[1][0]).ToUpperInvariant();
        }
        // Color estable por nombre (para el círculo de iniciales)
        protected static Color NameColor(string name)
        {
            Color[] pal = { Color.FromArgb(58, 90, 68), Color.FromArgb(58, 74, 96), Color.FromArgb(96, 64, 80), Color.FromArgb(92, 80, 52), Color.FromArgb(52, 86, 92), Color.FromArgb(84, 64, 104) };
            int h = 0; foreach (char ch in name ?? "") h = h * 31 + ch;
            return pal[Math.Abs(h) % pal.Length];
        }
        protected void Avatar(Graphics g, Rectangle r, Image logo, string name, Font f)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (logo != null)
            {
                using var path = new GraphicsPath(); path.AddEllipse(r);
                var st = g.Save(); g.SetClip(path, CombineMode.Intersect);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                if (logo.Height > logo.Width)
                {
                    // Foto de perfil (3:4): recortada para llenar el círculo sin deformarla, un poco hacia
                    // arriba, donde suele estar la cara.
                    int h = (int)Math.Ceiling(logo.Height * (r.Width / (double)logo.Width));
                    g.DrawImage(logo, r.X, r.Y - (h - r.Height) * 3 / 10, r.Width, h);
                }
                else g.DrawImage(logo, r);   // logotipo de empresa
                g.Restore(st);
            }
            else
            {
                using (var b = new SolidBrush(NameColor(name))) g.FillEllipse(b, r);
                TextRenderer.DrawText(g, Initials(name), f, r, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
            g.SmoothingMode = SmoothingMode.None;
        }
        // Foto de carnet recortada para llenar el hueco (sin deformar), con las esquinas redondeadas.
        protected static void DrawPhoto(Graphics g, Rectangle r, Image img, int radius)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Theme.Round(r, radius))
            {
                var st = g.Save(); g.SetClip(path, CombineMode.Intersect);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                double k = Math.Max(r.Width / (double)img.Width, r.Height / (double)img.Height);
                int w = (int)Math.Ceiling(img.Width * k), h = (int)Math.Ceiling(img.Height * k);
                g.DrawImage(img, r.X + (r.Width - w) / 2, r.Y + (r.Height - h) / 2, w, h);
                g.Restore(st);
            }
            g.SmoothingMode = SmoothingMode.None;
        }
        protected static readonly Color CardC = Color.FromArgb(52, 56, 60), Gold = Color.FromArgb(240, 196, 90), Silver = Color.FromArgb(196, 204, 212),
                                        Bronze = Color.FromArgb(205, 140, 90), Red = Color.FromArgb(229, 115, 115), Blue = Color.FromArgb(120, 144, 226), Orange = Color.FromArgb(251, 146, 60);
    }

    // =============================================================== RANKING: podio + tarjetas
    public class PodiumBoard : CanvasPanel
    {
        public sealed class Entry
        {
            public string Id = "", Name = "", Value = "", Detail = "", Prize = "";
            public int Pos;                      // 0 = no clasifica
            public Image Logo;
            public bool Mine, Out;
            public string OutNote = ""; public double OutProgress = -1;
            public (string cap, string val, Color? col)[] Cols = Array.Empty<(string, string, Color?)>();
            public Color? ValueColor;
        }
        public string Title = "", Footnote = "";
        public List<string> Pills = new List<string>();
        public List<(string amount, string label)> Prizes = new List<(string, string)>();
        public List<(string icon, string cap, string name, string val)> Leaders = new List<(string, string, string, string)>();
        public List<Entry> Entries = new List<Entry>();
        public string MineTag = "";

        Font _fTitle, _fPill, _fPillB, _fPrize, _fPrizeL, _fName, _fVal, _fSmall, _fStep, _fPos, _fColC, _fCol, _fMedal, _fAv, _fAvBig, _fLeadIc;
        // maqueta
        int _yPills, _yPrizes, _yPodium, _podH, _yRest, _yLeaders, _yFoot;
        readonly List<Rectangle> _pills = new(), _prizeR = new(), _leadR = new();
        readonly List<(Entry e, Rectangle r, int step)> _pod = new();
        readonly List<(Entry e, Rectangle r)> _rest = new();

        public PodiumBoard()
        {
            _fTitle = F(12.5f, FontStyle.Bold); _fPill = F(8.5f); _fPillB = F(8.5f, FontStyle.Bold); _fPrize = F(10.5f, FontStyle.Bold); _fPrizeL = F(8f);
            _fName = F(10.5f, FontStyle.Bold); _fVal = F(15f, FontStyle.Bold); _fSmall = F(8.5f); _fStep = F(18f, FontStyle.Bold); _fPos = F(12f, FontStyle.Bold);
            _fColC = F(7.25f, FontStyle.Bold); _fCol = F(9.5f); _fAv = F(9f, FontStyle.Bold); _fAvBig = F(12f, FontStyle.Bold);
            _fMedal = EmojiPicker.EmojiFont(20f); _fLeadIc = EmojiPicker.EmojiFont(18f);
        }
        protected override void Dispose(bool disposing) { if (disposing) { _fMedal.Dispose(); _fLeadIc.Dispose(); } base.Dispose(disposing); }

        protected override int DoLayout(int w)
        {
            _pills.Clear(); _prizeR.Clear(); _pod.Clear(); _rest.Clear(); _leadR.Clear();
            int pad = Theme.Px(2), y = Theme.Px(4);
            if (Title.Length > 0) y += Theme.Px(30);
            // chips (envuelven)
            _yPills = y; int x = pad;
            foreach (var p in Pills)
            {
                int pw = TW(p, _fPill) + Theme.Px(22);
                if (x + pw > w && x > pad) { x = pad; y += Theme.Px(30); }
                _pills.Add(new Rectangle(x, y, pw, Theme.Px(24))); x += pw + Theme.Px(8);
            }
            if (Pills.Count > 0) y += Theme.Px(34);
            _yPrizes = y; x = pad;
            foreach (var (a, l) in Prizes)
            {
                int pw = Math.Max(TW(a, _fPrize), TW(l, _fPrizeL)) + Theme.Px(24);
                if (x + pw > w && x > pad) { x = pad; y += Theme.Px(52); }
                _prizeR.Add(new Rectangle(x, y, pw, Theme.Px(44))); x += pw + Theme.Px(8);
            }
            if (Prizes.Count > 0) y += Theme.Px(56);
            // podio: 2 · 1 · 3
            _yPodium = y;
            var top = new Entry[3];
            foreach (var e in Entries) if (e.Pos >= 1 && e.Pos <= 3 && !e.Out && top[e.Pos - 1] == null) top[e.Pos - 1] = e;
            int gap = Theme.Px(12), colW = (w - gap * 2) * 100 / 315;
            int[] order = { 1, 0, 2 }; int[] steps = { Theme.Px(70), Theme.Px(48), Theme.Px(34) };
            int bodyH = Theme.Px(190);
            _podH = bodyH + steps[0];
            bool any = top[0] != null || top[1] != null || top[2] != null;
            if (any)
            {
                int cx = pad;
                for (int k = 0; k < 3; k++)
                {
                    int i = order[k];
                    int cw = k == 1 ? (w - pad * 2 - gap * 2) - colW * 2 : colW;
                    if (top[i] != null)
                    {
                        int h = bodyH + steps[i];   // mismo cuerpo para los tres: el escalón marca la altura
                        _pod.Add((top[i], new Rectangle(cx, y + _podH - h, cw, h), steps[i]));
                    }
                    cx += cw + gap;
                }
                y += _podH + Theme.Px(14);
            }
            // resto
            _yRest = y;
            foreach (var e in Entries)
            {
                if (_pod.Exists(p => p.e == e)) continue;
                _rest.Add((e, new Rectangle(pad, y, w - pad * 2, Theme.Px(e.Out && e.OutProgress >= 0 ? 58 : 52))));
                y += _rest[_rest.Count - 1].r.Height + Theme.Px(6);
            }
            // líderes
            if (Leaders.Count > 0)
            {
                y += Theme.Px(8); _yLeaders = y;
                int lw = (w - pad * 2 - gap * (Leaders.Count - 1)) / Leaders.Count;
                for (int i = 0; i < Leaders.Count; i++) _leadR.Add(new Rectangle(pad + i * (lw + gap), y, lw, Theme.Px(62)));
                y += Theme.Px(70);
            }
            _yFoot = y;
            if (Footnote.Length > 0) y += Theme.Px(26);
            return y + Theme.Px(6);
        }

        protected override void DoPaint(Graphics g, int top)
        {
            int pad = Theme.Px(2);
            if (Title.Length > 0)
            {
                var tr = new Rectangle(pad, Theme.Px(4) - top, W, Theme.Px(26));
                if (ColorText.HasEmoji(Title)) ColorText.Draw(g, Title, _fTitle, tr, Theme.Text);
                else Text(g, Title, _fTitle, tr, Theme.Text, L1);
            }
            for (int i = 0; i < _pills.Count; i++)
            {
                var r = _pills[i]; r.Offset(0, -top);
                Round(g, r, r.Height / 2, Theme.Surface2);
                Text(g, Pills[i], _fPill, r, Theme.Text, C1);
            }
            for (int i = 0; i < _prizeR.Count; i++)
            {
                var r = _prizeR[i]; r.Offset(0, -top);
                Round(g, r, Theme.Px(9), Theme.Surface);
                Text(g, Prizes[i].amount, _fPrize, new Rectangle(r.X + Theme.Px(12), r.Y + Theme.Px(4), r.Width - Theme.Px(16), Theme.Px(20)), Gold, L1);
                Text(g, Prizes[i].label, _fPrizeL, new Rectangle(r.X + Theme.Px(12), r.Y + Theme.Px(24), r.Width - Theme.Px(16), Theme.Px(16)), Theme.Subtle, L1);
            }
            foreach (var (e, r0, step) in _pod) PaintPodium(g, e, Offs(r0, top), step);
            foreach (var (e, r0) in _rest) PaintRow(g, e, Offs(r0, top));
            for (int i = 0; i < _leadR.Count; i++)
            {
                var r = Offs(_leadR[i], top); var (ic, cap, name, val) = Leaders[i];
                Round(g, r, Theme.Px(12), CardC);
                ColorText.DrawCells(g, new[] { (ic, new Rectangle(r.X + Theme.Px(10), r.Y, Theme.Px(40), r.Height)) }, _fLeadIc, Theme.Text, new Rectangle(r.X, r.Y, Theme.Px(56), r.Height));
                int tx = r.X + Theme.Px(58);
                Text(g, cap, _fColC, new Rectangle(tx, r.Y + Theme.Px(8), r.Right - tx - 8, Theme.Px(14)), Theme.Subtle, L1);
                Text(g, name, _fName, new Rectangle(tx, r.Y + Theme.Px(22), r.Right - tx - 8, Theme.Px(20)), Theme.Text, L1);
                Text(g, val, _fSmall, new Rectangle(tx, r.Y + Theme.Px(41), r.Right - tx - 8, Theme.Px(16)), Theme.AccentHi, L1);
            }
            if (Footnote.Length > 0) Text(g, Footnote, _fSmall, new Rectangle(pad, _yFoot - top + Theme.Px(4), W, Theme.Px(18)), Theme.Subtle, L1);
        }

        static Rectangle Offs(Rectangle r, int top) { r.Offset(0, -top); return r; }

        void PaintPodium(Graphics g, Entry e, Rectangle r, int step)
        {
            Color tint = e.Pos == 1 ? Gold : e.Pos == 2 ? Silver : Bronze;
            Round(g, r, Theme.Px(12), CardC);
            if (e.Pos == 1) Border(g, r, Theme.Px(12), Color.FromArgb(130, tint), 1.2f);
            var sr = new Rectangle(r.X, r.Bottom - step, r.Width, step);
            using (var b = new LinearGradientBrush(new Rectangle(sr.X, sr.Y - 1, sr.Width, sr.Height + 2), Color.FromArgb(95, tint), Color.FromArgb(30, tint), LinearGradientMode.Vertical))
            {
                g.SetClip(r); using var path = Theme.Round(r, Theme.Px(12)); g.SetClip(path);
                g.FillRectangle(b, sr); g.ResetClip();
            }
            Text(g, e.Pos.ToString(), _fStep, sr, Color.FromArgb(225, 255, 255, 255), C1);
            int y = r.Y + Theme.Px(12), cx = r.X + r.Width / 2;
            if (e.Mine && MineTag.Length > 0)
            {
                int tw = TW(MineTag, _fSmall) + Theme.Px(12);
                var tr = new Rectangle(r.Right - tw - Theme.Px(8), r.Y + Theme.Px(8), tw, Theme.Px(18));
                Round(g, tr, Theme.Px(5), Color.FromArgb(44, 72, 48)); Text(g, MineTag, _fSmall, tr, Theme.AccentHi, C1);
            }
            string medal = e.Pos == 1 ? "🥇" : e.Pos == 2 ? "🥈" : "🥉";
            ColorText.DrawCells(g, new[] { (medal, new Rectangle(cx - Theme.Px(20), y, Theme.Px(40), Theme.Px(34))) }, _fMedal, Theme.Text, new Rectangle(cx - Theme.Px(22), y, Theme.Px(44), Theme.Px(36)));
            y += Theme.Px(36);
            int av = Theme.Px(e.Pos == 1 ? 54 : 48);
            Avatar(g, new Rectangle(cx - av / 2, y, av, av), e.Logo, e.Name, _fAvBig);
            y += av + Theme.Px(6);
            int tw2 = r.Width - Theme.Px(16);
            Text(g, e.Name, _fName, new Rectangle(r.X + Theme.Px(8), y, tw2, Theme.Px(20)), Theme.Text, C1); y += Theme.Px(21);
            Text(g, e.Value, _fVal, new Rectangle(r.X + Theme.Px(8), y, tw2, Theme.Px(26)), e.ValueColor ?? Theme.AccentHi, C1); y += Theme.Px(26);
            Text(g, e.Detail, _fSmall, new Rectangle(r.X + Theme.Px(8), y, tw2, Theme.Px(16)), Theme.Subtle, C1); y += Theme.Px(17);
            if (e.Prize.Length > 0) Text(g, e.Prize, _fSmall, new Rectangle(r.X + Theme.Px(8), y, tw2, Theme.Px(16)), Gold, C1);
        }

        void PaintRow(Graphics g, Entry e, Rectangle r)
        {
            Round(g, r, Theme.Px(10), CardC);
            var oldC = Theme.Text; Color sub = Theme.Subtle;
            Color main = e.Out ? Theme.Subtle : Theme.Text;
            int x = r.X + Theme.Px(10);
            Text(g, e.Pos > 0 ? e.Pos.ToString() : "—", _fPos, new Rectangle(x, r.Y, Theme.Px(30), r.Height), Theme.Subtle, C1);
            x += Theme.Px(36);
            int av = Theme.Px(34);
            Avatar(g, new Rectangle(x, r.Y + (r.Height - av) / 2, av, av), e.Logo, e.Name, _fAv);
            x += av + Theme.Px(10);
            int colW = Theme.Px(104), cols = e.Cols.Length;
            int right = r.Right - Theme.Px(12) - cols * colW;
            int nameTop = r.Y + Theme.Px(8);
            Text(g, e.Name, _fName, new Rectangle(x, nameTop, right - x - 8, Theme.Px(20)), e.Mine ? Theme.AccentHi : main, L1);
            string under = e.Out ? e.OutNote : e.Prize;
            Text(g, under, _fSmall, new Rectangle(x, nameTop + Theme.Px(20), right - x - 8, Theme.Px(16)), e.Out ? sub : Gold, L1);
            if (e.Out && e.OutProgress >= 0)
            {
                var br = new Rectangle(x, nameTop + Theme.Px(38), Math.Min(Theme.Px(220), right - x - 12), Theme.Px(5));
                Round(g, br, 2, Theme.Surface2);
                int fw = (int)(br.Width * Math.Max(0, Math.Min(1, e.OutProgress)));
                if (fw > 2) Round(g, new Rectangle(br.X, br.Y, fw, br.Height), 2, Orange);
            }
            for (int i = 0; i < cols; i++)
            {
                var (cap, val, col) = e.Cols[i];
                var cr = new Rectangle(right + i * colW, r.Y, colW - Theme.Px(6), r.Height);
                Text(g, cap, _fColC, new Rectangle(cr.X, cr.Y + Theme.Px(8), cr.Width, Theme.Px(14)), sub, R1);
                Text(g, val, _fCol, new Rectangle(cr.X, cr.Y + Theme.Px(23), cr.Width, Theme.Px(20)), col ?? main, R1);
            }
        }
    }

    // =============================================================== RANKING: campeones por mes
    public class ChampionsBoard : CanvasPanel
    {
        public sealed class Month { public string Title = ""; public List<(string icon, string name, string amount, bool mine)> Rows = new(); }
        public List<Month> Months = new List<Month>();
        public string SummaryTitle = "", SummaryText = "";
        Font _fT, _fR, _fA, _fIc, _fBig, _fS;
        readonly List<(Month m, Rectangle r)> _cards = new();
        Rectangle _sum;

        public ChampionsBoard()
        {
            _fT = F(10.5f, FontStyle.Bold); _fR = F(9f); _fA = F(9f, FontStyle.Bold); _fS = F(9f);
            _fIc = EmojiPicker.EmojiFont(10f); _fBig = EmojiPicker.EmojiFont(24f);
        }
        protected override void Dispose(bool disposing) { if (disposing) { _fIc.Dispose(); _fBig.Dispose(); } base.Dispose(disposing); }

        protected override int DoLayout(int w)
        {
            _cards.Clear();
            int gap = Theme.Px(12), cols = Math.Max(1, Math.Min(3, (w + gap) / (Theme.Px(300) + gap)));
            int cw = (w - gap * (cols - 1)) / cols, y = Theme.Px(2);
            int idx = 0, rowH = 0;
            var all = new List<Month>(Months);
            int n = all.Count + (SummaryTitle.Length > 0 ? 1 : 0);
            for (int i = 0; i < n; i++)
            {
                int c = idx % cols;
                if (c == 0 && i > 0) { y += rowH + gap; rowH = 0; }
                int h = i < all.Count ? Theme.Px(40) + all[i].Rows.Count * Theme.Px(24) + Theme.Px(10) : Theme.Px(130);
                var r = new Rectangle(c * (cw + gap), y, cw, h);
                if (i < all.Count) _cards.Add((all[i], r)); else _sum = r;
                rowH = Math.Max(rowH, h); idx++;
            }
            return y + rowH + Theme.Px(8);
        }

        protected override void DoPaint(Graphics g, int top)
        {
            foreach (var (m, r0) in _cards)
            {
                var r = r0; r.Offset(0, -top);
                if (r.Bottom < 0 || r.Y > ClientSize.Height) continue;
                Round(g, r, Theme.Px(12), CardC);
                Text(g, m.Title, _fT, new Rectangle(r.X + Theme.Px(14), r.Y + Theme.Px(8), r.Width - Theme.Px(50), Theme.Px(24)), Theme.Text, L1);
                ColorText.DrawCells(g, new[] { ("🏆", new Rectangle(r.Right - Theme.Px(36), r.Y + Theme.Px(6), Theme.Px(26), Theme.Px(26))) }, _fIc, Theme.Text, new Rectangle(r.Right - Theme.Px(38), r.Y + Theme.Px(4), Theme.Px(30), Theme.Px(30)));
                int y = r.Y + Theme.Px(38);
                var icons = new List<(string, Rectangle)>();
                foreach (var (ic, name, amt, mine) in m.Rows)
                {
                    using (var p = new Pen(Theme.Surface2)) g.DrawLine(p, r.X + Theme.Px(12), y, r.Right - Theme.Px(12), y);
                    icons.Add((ic, new Rectangle(r.X + Theme.Px(12), y, Theme.Px(22), Theme.Px(24))));
                    int aw = TW(amt, _fA) + 4;
                    Text(g, name, _fR, new Rectangle(r.X + Theme.Px(38), y, r.Width - Theme.Px(50) - aw, Theme.Px(24)), mine ? Theme.AccentHi : Theme.Text, L1);
                    Text(g, amt, _fA, new Rectangle(r.Right - Theme.Px(12) - aw, y, aw, Theme.Px(24)), Gold, R1);
                    y += Theme.Px(24);
                }
                ColorText.DrawCells(g, icons, _fIc, Theme.Text, r);
            }
            if (SummaryTitle.Length > 0)
            {
                var r = _sum; r.Offset(0, -top);
                Round(g, r, Theme.Px(12), CardC);
                ColorText.DrawCells(g, new[] { ("🏅", new Rectangle(r.X, r.Y + Theme.Px(10), r.Width, Theme.Px(40))) }, _fBig, Theme.Text, r);
                Text(g, SummaryTitle, _fT, new Rectangle(r.X + 8, r.Y + Theme.Px(54), r.Width - 16, Theme.Px(22)), Theme.Text, C1);
                TextRenderer.DrawText(g, SummaryText, _fS, new Rectangle(r.X + Theme.Px(16), r.Y + Theme.Px(78), r.Width - Theme.Px(32), Theme.Px(44)), Theme.Subtle, Wrap | TextFormatFlags.HorizontalCenter);
            }
        }
    }

    // =============================================================== SOCIOS: carnets
    public class MemberCardGrid : CanvasPanel
    {
        public sealed class Member
        {
            public string UserId = "", Name = "", Role = "", RoleLabel = "", Meta = "";
            public int Points = -1;                    // −1 = no se sabe
            public string PointsText = ""; public Color PointsColor = Theme.Subtle; public bool Suspended;
            public string Services = "—", Km = "—", Net = "—"; public bool NetNeg;
            public Image Photo;                         // foto de carnet (null = iniciales)
        }
        public List<Member> Members = new List<Member>();
        public string CompanyName = ""; public Image CompanyLogo;
        public string SelectedId { get; private set; }
        public event Action SelectionChanged;
        public string LblLicense = "Carné", LblServices = "servicios", LblKm = "en la empresa", LblNet = "neto", LblSuspended = "SUSPENDIDO";
        Font _fHead, _fRole, _fName, _fMeta, _fPts, _fFootB, _fFoot, _fPhoto, _fStamp, _fLogo;
        readonly List<(Member m, Rectangle r)> _cards = new();
        string _hover;

        public MemberCardGrid()
        {
            _fHead = F(7.5f, FontStyle.Bold); _fRole = F(8f, FontStyle.Bold); _fName = F(11f, FontStyle.Bold); _fMeta = F(8.25f);
            _fPts = F(8.25f, FontStyle.Bold); _fFootB = F(10f, FontStyle.Bold); _fFoot = F(7.75f); _fPhoto = F(15f, FontStyle.Bold); _fStamp = F(9f, FontStyle.Bold); _fLogo = F(6.5f, FontStyle.Bold);
        }

        protected override int DoLayout(int w)
        {
            _cards.Clear();
            int gap = Theme.Px(14), cols = Math.Max(1, (w + gap) / (Theme.Px(300) + gap));
            int cw = (w - gap * (cols - 1)) / cols, ch = Theme.Px(176), y = Theme.Px(2);
            for (int i = 0; i < Members.Count; i++)
            {
                int c = i % cols; if (c == 0 && i > 0) y += ch + gap;
                _cards.Add((Members[i], new Rectangle(c * (cw + gap), y, cw, ch)));
            }
            return Members.Count == 0 ? 0 : y + ch + Theme.Px(6);
        }

        protected override void DoPaint(Graphics g, int top)
        {
            foreach (var (m, r0) in _cards)
            {
                var r = r0; r.Offset(0, -top);
                if (r.Bottom < 0 || r.Y > ClientSize.Height) continue;
                PaintCard(g, m, r);
            }
        }

        void PaintCard(Graphics g, Member m, Rectangle r)
        {
            bool sel = m.UserId == SelectedId, hov = m.UserId == _hover;
            int rad = Theme.Px(14);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Theme.Round(r, rad))
            using (var b = new LinearGradientBrush(r, sel || hov ? Color.FromArgb(60, 66, 70) : Color.FromArgb(54, 60, 64), Color.FromArgb(42, 46, 50), LinearGradientMode.ForwardDiagonal))
                g.FillPath(b, path);
            // cabecera: logo + empresa + rol
            var hr = new Rectangle(r.X, r.Y, r.Width, Theme.Px(32));
            using (var path = Theme.Round(r, rad))
            {
                var st = g.Save(); g.SetClip(path);
                using (var b = new SolidBrush(Color.FromArgb(70, 0, 0, 0))) g.FillRectangle(b, hr);
                g.Restore(st);
            }
            g.SmoothingMode = SmoothingMode.None;
            int lx = r.X + Theme.Px(10), ls = Theme.Px(20);
            Avatar(g, new Rectangle(lx, hr.Y + (hr.Height - ls) / 2, ls, ls), CompanyLogo, CompanyName, _fLogo);
            string role = m.RoleLabel;
            int rw = TW(role, _fRole) + Theme.Px(14);
            var rr = new Rectangle(r.Right - rw - Theme.Px(10), hr.Y + Theme.Px(7), rw, Theme.Px(18));
            Color rc = m.Role == "owner" ? Gold : m.Role == "manager" ? Blue : Theme.Text;
            Round(g, rr, Theme.Px(6), m.Role == "owner" ? Color.FromArgb(70, 62, 38) : m.Role == "manager" ? Color.FromArgb(48, 56, 82) : Theme.Surface2);
            Text(g, role, _fRole, rr, rc, C1);
            Text(g, (CompanyName ?? "").ToUpperInvariant(), _fHead, new Rectangle(lx + ls + Theme.Px(8), hr.Y, rr.X - lx - ls - Theme.Px(14), hr.Height), Theme.Subtle, L1);
            // foto (iniciales) + nombre + datos + carné
            int bx = r.X + Theme.Px(12), by = hr.Bottom + Theme.Px(12);
            var ph = new Rectangle(bx, by, Theme.Px(62), Theme.Px(74));
            Round(g, ph, Theme.Px(9), Color.FromArgb(28, 31, 36));
            if (m.Photo != null) DrawPhoto(g, ph, m.Photo, Theme.Px(9));
            else Text(g, Initials(m.Name), _fPhoto, ph, Theme.AccentHi, C1);
            Border(g, ph, Theme.Px(9), Theme.Surface2);
            int tx = ph.Right + Theme.Px(12), tw = r.Right - tx - Theme.Px(12);
            Text(g, m.Name, _fName, new Rectangle(tx, by - 2, tw, Theme.Px(22)), Theme.Text, L1);
            Text(g, m.Meta, _fMeta, new Rectangle(tx, by + Theme.Px(21), tw, Theme.Px(16)), Theme.Subtle, L1);
            // barra de 15 casillas del carné
            int py = by + Theme.Px(44), cells = 15, cg = Theme.Px(2), cwid = Math.Max(2, (tw - cg * (cells - 1)) / cells);
            Color on = m.Suspended || (m.Points >= 0 && m.Points < 6) ? Red : m.Points >= 0 && m.Points < 10 ? Gold : Theme.Accent;
            for (int i = 0; i < cells; i++)
            {
                var c = new Rectangle(tx + i * (cwid + cg), py, cwid, Theme.Px(6));
                using var b = new SolidBrush(!m.Suspended && i < m.Points ? on : Theme.Surface2);
                g.FillRectangle(b, c);
            }
            Text(g, LblLicense, _fMeta, new Rectangle(tx, py + Theme.Px(9), tw / 2, Theme.Px(16)), Theme.Subtle, L1);
            Text(g, m.PointsText, _fPts, new Rectangle(tx + tw / 3, py + Theme.Px(9), tw - tw / 3, Theme.Px(16)), m.PointsColor, R1);
            // pie: servicios · km · neto
            int fy = r.Bottom - Theme.Px(44);
            using (var p = new Pen(Color.FromArgb(18, 255, 255, 255))) { g.DrawLine(p, r.X, fy, r.Right, fy); g.DrawLine(p, r.X + r.Width / 3, fy, r.X + r.Width / 3, r.Bottom - 4); g.DrawLine(p, r.X + 2 * r.Width / 3, fy, r.X + 2 * r.Width / 3, r.Bottom - 4); }
            var vals = new[] { (m.Services, LblServices, Theme.Text), (m.Km, LblKm, Theme.Text), (m.Net, LblNet, m.NetNeg ? Red : Theme.AccentHi) };
            for (int i = 0; i < 3; i++)
            {
                int fx = r.X + i * r.Width / 3 + Theme.Px(10), fw = r.Width / 3 - Theme.Px(14);
                Text(g, vals[i].Item1, _fFootB, new Rectangle(fx, fy + Theme.Px(5), fw, Theme.Px(18)), vals[i].Item3, L1);
                Text(g, vals[i].Item2, _fFoot, new Rectangle(fx, fy + Theme.Px(23), fw, Theme.Px(14)), Theme.Subtle, L1);
            }
            if (m.Suspended)
            {
                // sello inclinado
                var st = g.Save();
                g.TranslateTransform(r.Right - Theme.Px(70), hr.Bottom + Theme.Px(22));
                g.RotateTransform(-12);
                int sw = TW(LblSuspended, _fStamp) + Theme.Px(14);
                var srect = new Rectangle(-sw / 2, -Theme.Px(11), sw, Theme.Px(22));
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var pen = new Pen(Color.FromArgb(220, Red), 2f)) using (var path = Theme.Round(srect, Theme.Px(5))) g.DrawPath(pen, path);
                using (var b = new SolidBrush(Color.FromArgb(220, Red))) using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString(LblSuspended, _fStamp, b, srect, sf);
                g.Restore(st);
            }
            if (sel) Border(g, r, rad, Theme.Accent, 1.6f);
        }

        Member At(Point p)
        {
            int y = p.Y + ScrollY;
            foreach (var (m, r) in _cards) if (r.Contains(p.X, y)) return m;
            return null;
        }
        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); var m = At(e.Location); Cursor = m != null ? Cursors.Hand : Cursors.Default; if (m?.UserId != _hover) { _hover = m?.UserId; Invalidate(); } }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (_hover != null) { _hover = null; Invalidate(); } }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); Focus(); var m = At(e.Location); if (m != null) Select(m.UserId); }
        public void Select(string id) { if (SelectedId == id) return; SelectedId = id; Invalidate(); SelectionChanged?.Invoke(); }
        public void SetMembers(List<Member> list)
        {
            Members = list;
            if (SelectedId != null && !list.Exists(x => x.UserId == SelectedId)) SelectedId = null;
            Relayout(true);
        }
    }

    // =============================================================== MI PERFIL
    public class ProfileHero : CanvasPanel
    {
        public string Caption = "CARNÉ DE MAQUINISTA · SELECTOR", Name_ = "", Meta = "", Rank = "—", Step = "", Next = "";
        public double Pct; public bool Frozen;
        public Image Photo;                              // foto de carnet (null = iniciales)
        public bool CanEditPhoto;                        // con sesión: la foto se puede cambiar
        public string LblAddPhoto = "Añadir foto", LblChangePhoto = "Cambiar foto";
        public event Action<Point> PhotoClicked;         // punto (en pantalla) donde abrir el menú
        Rectangle _ph; bool _phHover;
        Font _fCap, _fName, _fMeta, _fRank, _fStep, _fNext, _fPhoto, _fTrain, _fPhHint;
        public ProfileHero()
        {
            AutoHeight = true;
            _fCap = F(7.5f, FontStyle.Bold); _fName = F(15f, FontStyle.Bold); _fMeta = F(8.75f); _fRank = F(13f, FontStyle.Bold); _fStep = F(8.5f);
            _fNext = F(8.5f); _fPhoto = F(19f, FontStyle.Bold); _fTrain = EmojiPicker.EmojiFont(70f); _fPhHint = F(7.5f, FontStyle.Bold);
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool h = CanEditPhoto && _ph.Contains(e.Location);
            if (h != _phHover) { _phHover = h; Cursor = h ? Cursors.Hand : Cursors.Default; Invalidate(); }
        }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (_phHover) { _phHover = false; Cursor = Cursors.Default; Invalidate(); } }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (CanEditPhoto && e.Button == MouseButtons.Left && _ph.Contains(e.Location)) PhotoClicked?.Invoke(PointToScreen(new Point(_ph.X, _ph.Bottom + 2)));
        }
        protected override void Dispose(bool disposing) { if (disposing) _fTrain.Dispose(); base.Dispose(disposing); }
        protected override int DoLayout(int w) => Theme.Px(180);
        protected override void DoPaint(Graphics g, int top)
        {
            var r = new Rectangle(0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
            int rad = Theme.Px(16);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Theme.Round(r, rad))
            {
                using (var b = new LinearGradientBrush(r, Color.FromArgb(40, 70, 48), Color.FromArgb(32, 44, 38), LinearGradientMode.ForwardDiagonal)) g.FillPath(b, path);
                using (var p = new Pen(Color.FromArgb(80, 102, 197, 106))) g.DrawPath(p, path);
            }
            g.SmoothingMode = SmoothingMode.None;
            // marca de agua
            TextRenderer.DrawText(g, "🚆", _fTrain, new Rectangle(r.Right - Theme.Px(150), r.Bottom - Theme.Px(120), Theme.Px(160), Theme.Px(130)), Color.FromArgb(52, 78, 60), TextFormatFlags.NoPadding);
            int x = Theme.Px(16), y = Theme.Px(12);
            Text(g, Caption, _fCap, new Rectangle(x, y, r.Width - 2 * x, Theme.Px(16)), Theme.AccentHi, L1);
            y += Theme.Px(24);
            var ph = new Rectangle(x, y, Theme.Px(84), Theme.Px(112));
            _ph = ph;
            Round(g, ph, Theme.Px(10), Color.FromArgb(26, 30, 34));
            if (Photo != null) DrawPhoto(g, ph, Photo, Theme.Px(10));
            else Text(g, Initials(Name_), _fPhoto, ph, Theme.AccentHi, C1);
            if (CanEditPhoto && (_phHover || Photo == null))
            {
                // franja inferior: «📷 Añadir foto» (sin foto, siempre) o «Cambiar foto» (al pasar el ratón)
                var hr = new Rectangle(ph.X, ph.Bottom - Theme.Px(22), ph.Width, Theme.Px(22));
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var path = Theme.Round(ph, Theme.Px(10)))
                {
                    var st = g.Save(); g.SetClip(path, CombineMode.Intersect);
                    using (var b = new SolidBrush(Color.FromArgb(_phHover ? 210 : 150, 16, 20, 18))) g.FillRectangle(b, hr);
                    g.Restore(st);
                }
                g.SmoothingMode = SmoothingMode.None;
                Text(g, Photo == null ? LblAddPhoto : LblChangePhoto, _fPhHint, hr, _phHover ? Color.White : Theme.AccentHi, C1);
            }
            Border(g, ph, Theme.Px(10), _phHover ? Theme.Accent : Color.FromArgb(70, 102, 197, 106), _phHover ? 1.5f : 1f);
            int tx = ph.Right + Theme.Px(16), tw = r.Width - tx - Theme.Px(16);
            Text(g, Name_, _fName, new Rectangle(tx, y - 2, tw, Theme.Px(28)), Theme.Text, L1);
            Text(g, Meta, _fMeta, new Rectangle(tx, y + Theme.Px(27), tw, Theme.Px(18)), Theme.Subtle, L1);
            int rw = TW(Rank, _fRank);
            Text(g, Rank, _fRank, new Rectangle(tx, y + Theme.Px(48), rw + 4, Theme.Px(24)), Frozen ? Blue : Theme.AccentHi, L1);
            Text(g, Step, _fStep, new Rectangle(tx + rw + Theme.Px(10), y + Theme.Px(50), tw - rw - 10, Theme.Px(22)), Theme.Subtle, L1);
            var br = new Rectangle(tx, y + Theme.Px(76), tw, Theme.Px(7));
            Round(g, br, 3, Color.FromArgb(70, 0, 0, 0));
            int fw = (int)(br.Width * Math.Max(0, Math.Min(1, Pct)));
            if (fw > 3) Round(g, new Rectangle(br.X, br.Y, fw, br.Height), 3, Frozen ? Blue : Theme.Accent);
            Text(g, Next, _fNext, new Rectangle(tx, y + Theme.Px(87), tw, Theme.Px(16)), Theme.Subtle, L1);
        }
    }

    public class BadgeGrid : CanvasPanel
    {
        public List<(string icon, string text, bool earned, Color color, double progress)> Items = new();
        Font _fT, _fTB, _fIc;
        readonly List<Rectangle> _cells = new();
        public BadgeGrid() { AutoHeight = true; _fT = F(8f); _fTB = F(8f, FontStyle.Bold); _fIc = EmojiPicker.EmojiFont(18f); }
        protected override void Dispose(bool disposing) { if (disposing) _fIc.Dispose(); base.Dispose(disposing); }
        protected override int DoLayout(int w)
        {
            _cells.Clear();
            int gap = Theme.Px(8), cw = Theme.Px(112), cols = Math.Max(1, (w + gap) / (cw + gap));
            cw = (w - gap * (cols - 1)) / cols; int ch = Theme.Px(92);
            for (int i = 0; i < Items.Count; i++) _cells.Add(new Rectangle((i % cols) * (cw + gap), (i / cols) * (ch + gap), cw, ch));
            return Items.Count == 0 ? 0 : ((Items.Count - 1) / cols + 1) * (ch + gap);
        }
        protected override void DoPaint(Graphics g, int top)
        {
            var icons = new List<(string, Rectangle)>();
            for (int i = 0; i < Items.Count && i < _cells.Count; i++)
            {
                var (ic, text, earned, color, prog) = Items[i]; var r = _cells[i];
                Round(g, r, Theme.Px(12), earned ? CanvasPanel_Blend(CardC, color, 0.12f) : Color.FromArgb(46, 50, 54));
                if (earned) Border(g, r, Theme.Px(12), Color.FromArgb(110, color));
                int d = Theme.Px(38);
                var dr = new Rectangle(r.X + (r.Width - d) / 2, r.Y + Theme.Px(10), d, d);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var b = new SolidBrush(earned ? Color.FromArgb(70, color) : Color.FromArgb(56, 60, 64))) g.FillEllipse(b, dr);
                g.SmoothingMode = SmoothingMode.None;
                if (earned) icons.Add((ic, dr));
                else TextRenderer.DrawText(g, ic, _fIc, dr, Color.FromArgb(110, 116, 122), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, text, earned ? _fTB : _fT, new Rectangle(r.X + 4, dr.Bottom + Theme.Px(4), r.Width - 8, Theme.Px(30)), earned ? Theme.Text : Theme.Subtle,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
                if (!earned && prog > 0)
                {
                    var br = new Rectangle(r.X + Theme.Px(14), r.Bottom - Theme.Px(10), r.Width - Theme.Px(28), Theme.Px(4));
                    Round(g, br, 2, Theme.Surface2);
                    int fw = (int)(br.Width * Math.Min(1, prog)); if (fw > 2) Round(g, new Rectangle(br.X, br.Y, fw, br.Height), 2, Theme.Accent);
                }
            }
            ColorText.DrawCells(g, icons, _fIc, Theme.Text, new Rectangle(0, 0, ClientSize.Width, ClientSize.Height));
        }
        static Color CanvasPanel_Blend(Color a, Color b, float t) => Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        public void SetItems(List<(string, string, bool, Color, double)> items) { Items = items; Relayout(true); }
    }

    public class TrainTopList : CanvasPanel
    {
        public List<(string name, string sub, string count, string path)> Items = new();
        public string Title = "";
        public VehicleThumbs Thumbs;
        readonly ThumbMemory _mem = new ThumbMemory(20);
        Font _fT, _fN, _fS, _fC;
        public TrainTopList() { AutoHeight = true; _fT = F(7.5f, FontStyle.Bold); _fN = F(9.5f, FontStyle.Bold); _fS = F(8.25f); _fC = F(11f, FontStyle.Bold); }
        protected override void Dispose(bool disposing) { if (disposing) _mem.Dispose(); base.Dispose(disposing); }
        protected override int DoLayout(int w) => Theme.Px(40) + Math.Max(1, Items.Count) * Theme.Px(56) + Theme.Px(8);
        protected override void DoPaint(Graphics g, int top)
        {
            var r = new Rectangle(0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
            Round(g, r, Theme.Px(14), CardC);
            Text(g, Title, _fT, new Rectangle(Theme.Px(14), Theme.Px(12), r.Width, Theme.Px(16)), Theme.Subtle, L1);
            int y = Theme.Px(38);
            foreach (var (name, sub, count, path) in Items)
            {
                var rail = new Rectangle(Theme.Px(14), y + Theme.Px(4), Theme.Px(150), Theme.Px(46));
                CardPaint.Rail_(g, rail, _mem.Get(path), string.IsNullOrEmpty(path) || _mem.Failed(path) ? "🚆" : null, _fS);
                _mem.Request(Thumbs, path, Theme.Px(34), rail.Width - Theme.Px(14), () => Invalidate(), k => true);
                int cw = TW(count, _fC) + 6, tx = rail.Right + Theme.Px(12);
                Text(g, name, _fN, new Rectangle(tx, y + Theme.Px(8), r.Width - tx - cw - Theme.Px(20), Theme.Px(20)), Theme.Text, L1);
                Text(g, sub, _fS, new Rectangle(tx, y + Theme.Px(28), r.Width - tx - cw - Theme.Px(20), Theme.Px(16)), Theme.Subtle, L1);
                Text(g, count, _fC, new Rectangle(r.Right - Theme.Px(14) - cw, y, cw, Theme.Px(56)), Theme.AccentHi, R1);
                y += Theme.Px(56);
            }
        }
        public void SetItems(List<(string, string, string, string)> items) { Items = items; Relayout(true); }
    }

    public class RouteBars : CanvasPanel
    {
        public List<(string name, string value, double frac)> Items = new();
        public string Title = "";
        Font _fT, _fN, _fV;
        public RouteBars() { AutoHeight = true; _fT = F(7.5f, FontStyle.Bold); _fN = F(9.5f, FontStyle.Bold); _fV = F(9f, FontStyle.Bold); }
        protected override int DoLayout(int w) => Theme.Px(40) + Math.Max(1, Items.Count) * Theme.Px(44) + Theme.Px(8);
        protected override void DoPaint(Graphics g, int top)
        {
            var r = new Rectangle(0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
            Round(g, r, Theme.Px(14), CardC);
            Text(g, Title, _fT, new Rectangle(Theme.Px(14), Theme.Px(12), r.Width, Theme.Px(16)), Theme.Subtle, L1);
            int y = Theme.Px(40), x = Theme.Px(14), w = r.Width - Theme.Px(28);
            foreach (var (name, value, frac) in Items)
            {
                int vw = TW(value, _fV) + 4;
                Text(g, name, _fN, new Rectangle(x, y, w - vw - 8, Theme.Px(20)), Theme.Text, L1);
                Text(g, value, _fV, new Rectangle(x + w - vw, y, vw, Theme.Px(20)), Blue, R1);
                var br = new Rectangle(x, y + Theme.Px(24), w, Theme.Px(6));
                Round(g, br, 3, Theme.Surface2);
                int fw = (int)(w * Math.Max(0, Math.Min(1, frac))); if (fw > 3) Round(g, new Rectangle(br.X, br.Y, fw, br.Height), 3, Blue);
                y += Theme.Px(44);
            }
        }
        public void SetItems(List<(string, string, double)> items) { Items = items; Relayout(true); }
    }
}
