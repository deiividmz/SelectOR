// RUTA y ACTIVIDAD con el estilo común: portada de la ruta (imagen, datos en pastillas y descripción con
// «Leer más»), accesos «Empezar a conducir» con sus cifras, actividades en tarjetas y la ficha de la elegida.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SelectOR
{
    // ------------------------------------------------------------------ portada de la ruta
    public class RouteHero : Control
    {
        public Func<Image> ImageOf;
        public string Caption = "RUTA", Title = "", Description = "", LblMore = "Leer más", LblLess = "Leer menos", Placeholder = "";
        public List<string> Chips = new List<string>();
        public bool Expanded;
        public event Action HeightChanged;
        readonly Font _fCap = Theme.Font(8f, FontStyle.Bold), _fTitle = Theme.Font(20f, FontStyle.Bold), _fChip = Theme.Font(9f, FontStyle.Bold), _fDesc = Theme.Font(9.5f), _fMore = Theme.Font(9f, FontStyle.Bold);
        Rectangle _more;
        bool _moreHover;
        const TextFormatFlags Wrap = TextFormatFlags.NoPadding | TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix;

        public RouteHero()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }
        protected override void Dispose(bool disposing) { if (disposing) { _fCap.Dispose(); _fTitle.Dispose(); _fChip.Dispose(); _fDesc.Dispose(); _fMore.Dispose(); _bg?.Dispose(); } base.Dispose(disposing); }

        static int Pad => Theme.Px(20);
        int TextW(int w) => Math.Max(Theme.Px(200), Math.Min(w - Pad * 2, Theme.Px(860)));
        int _fdW = -1, _fdH; string _fdText;
        int FullDescH(int w)   // medir un texto largo cuesta: se recuerda para el mismo ancho y texto
        {
            if (string.IsNullOrWhiteSpace(Description)) return 0;
            int tw = TextW(w);
            if (tw != _fdW || !ReferenceEquals(_fdText, Description)) { _fdW = tw; _fdText = Description; _fdH = TextRenderer.MeasureText(Description, _fDesc, new Size(tw, 100000), Wrap).Height; }
            return _fdH;
        }
        int LineH => _fDesc.Height;
        bool Long(int w) => FullDescH(w) > LineH * 3 + 2;
        int DescH(int w) => Expanded ? Math.Min(FullDescH(w), LineH * 18) : Math.Min(FullDescH(w), LineH * 3);

        public int NeededHeight(int w)
        {
            int h = Pad + _fCap.Height + Theme.Px(2) + _fTitle.Height + Theme.Px(10) + Theme.Px(28);
            int d = DescH(w);
            if (d > 0) h += Theme.Px(12) + d;
            if (Long(w)) h += Theme.Px(6) + _fMore.Height;
            return Math.Max(Theme.Px(150), h + Pad);
        }

        public void SetContent(string title, string desc, List<string> chips)
        {
            bool changed = title != Title || desc != Description;
            Title = title ?? ""; Description = (desc ?? "").Trim(); Chips = chips ?? new List<string>();
            if (changed) Expanded = false;
            Invalidate(); HeightChanged?.Invoke();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent?.BackColor ?? Theme.Bg);
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            int rad = Theme.Px(14);
            // Fondo (foto + velo) ya compuesto: solo se rehace si cambia la imagen o el tamaño.
            var img0 = ImageOf?.Invoke();
            if (_bg == null || _bgSize != Size || !ReferenceEquals(_bgImg, img0))
            {
                _bg?.Dispose();
                _bg = new Bitmap(Math.Max(1, Width), Math.Max(1, Height), System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                using (var gb = Graphics.FromImage(_bg)) { gb.Clear(Parent?.BackColor ?? Theme.Bg); PaintBackground(gb, r, rad, img0); }
                _bgSize = Size; _bgImg = img0;
            }
            g.DrawImageUnscaled(_bg, 0, 0);
            PaintText(g);
        }

        Bitmap _bg; Size _bgSize; Image _bgImg;

        void PaintBackground(Graphics g, Rectangle r, int rad, Image img)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Theme.Round(r, rad))
            {
                var st = g.Save(); g.SetClip(path, CombineMode.Intersect);
                if (img != null)
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    double k = Math.Max(r.Width / (double)img.Width, r.Height / (double)img.Height);
                    int w = (int)Math.Ceiling(img.Width * k), h = (int)Math.Ceiling(img.Height * k);
                    g.DrawImage(img, r.X + (r.Width - w) / 2, r.Y + (r.Height - h) / 2, w, h);
                }
                else using (var b = new LinearGradientBrush(r, Color.FromArgb(78, 104, 82), Color.FromArgb(34, 44, 38), LinearGradientMode.ForwardDiagonal)) g.FillRectangle(b, r);
                // velo oscuro: opaco a la izquierda (texto) y transparente a la derecha (se ve la foto)
                using (var b = new LinearGradientBrush(r, Color.FromArgb(244, 22, 25, 23), Color.FromArgb(70, 22, 25, 23), LinearGradientMode.Horizontal))
                {
                    var bl = new Blend { Positions = new[] { 0f, 0.45f, 1f }, Factors = new[] { 0f, 0.25f, 1f } };
                    b.Blend = bl; g.FillRectangle(b, r);
                }
                g.Restore(st);
                using (var p = new Pen(Color.FromArgb(30, 255, 255, 255))) g.DrawPath(p, path);
            }
            g.SmoothingMode = SmoothingMode.None;
        }

        void PaintText(Graphics g)
        {
            int x = Pad, y = Pad, tw = TextW(Width);
            const TextFormatFlags L = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
            TextRenderer.DrawText(g, Caption, _fCap, new Rectangle(x, y, tw, _fCap.Height), Theme.AccentHi, L);
            y += _fCap.Height + Theme.Px(2);
            TextRenderer.DrawText(g, string.IsNullOrEmpty(Title) ? Placeholder : Title, _fTitle, new Rectangle(x, y, Width - Pad * 2, _fTitle.Height), Color.White, L);
            y += _fTitle.Height + Theme.Px(10);
            // datos en pastillas (con emoji a color)
            int cx = x, ch = Theme.Px(28);
            foreach (var c in Chips)
            {
                int cw = ColorText.Measure(g, c, _fChip, 1000).Width + Theme.Px(22);
                if (cx + cw > Width - Pad) break;
                var cr = new Rectangle(cx, y, cw, ch);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Theme.FillRound(g, cr, ch / 2, Color.FromArgb(120, 0, 0, 0));
                Theme.DrawRoundBorder(g, cr, ch / 2, Color.FromArgb(40, 255, 255, 255), 1f);
                g.SmoothingMode = SmoothingMode.None;
                int th = ColorText.Measure(g, c, _fChip, 1000).Height;
                ColorText.Draw(g, c, _fChip, new Rectangle(cr.X + Theme.Px(11), cr.Y + (ch - th) / 2, cw, th + 2), Theme.Text);
                cx += cw + Theme.Px(6);
            }
            y += ch;
            int dh = DescH(Width);
            if (dh > 0)
            {
                y += Theme.Px(12);
                TextRenderer.DrawText(g, Description, _fDesc, new Rectangle(x, y, tw, dh), Color.FromArgb(214, 218, 220), Wrap | TextFormatFlags.EndEllipsis);
                y += dh;
            }
            _more = Rectangle.Empty;
            if (Long(Width))
            {
                y += Theme.Px(6);
                string t = Expanded ? LblLess : LblMore;
                int w = TextRenderer.MeasureText(t, _fMore, Size.Empty, TextFormatFlags.NoPadding).Width;
                _more = new Rectangle(x, y, w, _fMore.Height);
                using var fu = new Font(_fMore, _moreHover ? FontStyle.Bold | FontStyle.Underline : FontStyle.Bold);
                TextRenderer.DrawText(g, t, fu, _more, Theme.AccentHi, TextFormatFlags.NoPadding);
            }
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool h = _more.Contains(e.Location);
            if (h != _moreHover) { _moreHover = h; Cursor = h ? Cursors.Hand : Cursors.Default; Invalidate(); }
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (_more.Contains(e.Location)) { Expanded = !Expanded; Invalidate(); HeightChanged?.Invoke(); }
        }
    }

    // ------------------------------------------------------------------ bienvenida (sin ruta elegida)
    // Tarjeta centrada con el logotipo de SelectOR, «Elige una ruta para empezar», una flecha hacia la lista de
    // la izquierda y unas cifras del contenido.
    public class RouteWelcome : Control
    {
        public string Title = "Elige una ruta para empezar", Sub = "", Arrow = "", Footer = "", Copyright = "";
        public List<string> Chips = new List<string>();
        readonly Font _fLogo = Theme.Font(30f, FontStyle.Bold), _fT = Theme.Font(17f, FontStyle.Bold), _fS = Theme.Font(10f), _fA = Theme.Font(9.5f, FontStyle.Bold), _fC = Theme.Font(9f, FontStyle.Bold);
        public RouteWelcome() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); BackColor = Theme.Bg; }
        protected override void Dispose(bool disposing) { if (disposing) foreach (var f in new[] { _fLogo, _fT, _fS, _fA, _fC }) f.Dispose(); base.Dispose(disposing); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            const TextFormatFlags C = TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix;
            int cw = Math.Min(Width - Theme.Px(40), Theme.Px(640)), ch = Math.Min(Height - Theme.Px(110), Theme.Px(420));
            if (cw < 50 || ch < 50) return;
            var card = new Rectangle((Width - cw) / 2, (Height - Theme.Px(50) - ch) / 2, cw, ch);   // (deja sitio abajo para la versión)
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Theme.Round(card, Theme.Px(20)))
            {
                using (var b = new LinearGradientBrush(card, Color.FromArgb(44, 56, 48), Color.FromArgb(34, 37, 39), LinearGradientMode.Vertical)) g.FillPath(b, path);
                var st = g.Save(); g.SetClip(path, CombineMode.Intersect);
                // brillo verde suave detrás del logotipo
                var glow = new Rectangle(card.X + card.Width / 2 - Theme.Px(220), card.Y - Theme.Px(160), Theme.Px(440), Theme.Px(360));
                using (var gp = new GraphicsPath())
                {
                    gp.AddEllipse(glow);
                    using var pb = new PathGradientBrush(gp) { CenterColor = Color.FromArgb(60, 102, 197, 106), SurroundColors = new[] { Color.FromArgb(0, 102, 197, 106) } };
                    g.FillEllipse(pb, glow);
                }
                // vías en perspectiva al pie de la tarjeta
                using (var p = new Pen(Color.FromArgb(36, 255, 255, 255), Theme.Px(2)))
                {
                    int cx = card.X + card.Width / 2, by = card.Bottom;
                    g.DrawLine(p, cx - Theme.Px(16), by - Theme.Px(70), cx - Theme.Px(150), by);
                    g.DrawLine(p, cx + Theme.Px(16), by - Theme.Px(70), cx + Theme.Px(150), by);
                    for (int k = 0; k < 6; k++)
                    {
                        float t = (k + 1) / 7f, ry = by - Theme.Px(70) + t * t * Theme.Px(70), half = Theme.Px(18) + t * t * Theme.Px(140);
                        g.DrawLine(p, cx - half, ry, cx + half, ry);
                    }
                }
                g.Restore(st);
                using (var p = new Pen(Color.FromArgb(70, 102, 197, 106))) g.DrawPath(p, path);
            }
            // abajo del todo: versión y copyright
            using (var ff = Theme.Font(8.25f))
            {
                int fy = Height - Theme.Px(44);
                TextRenderer.DrawText(g, Footer, ff, new Rectangle(0, fy, Width, Theme.Px(18)), Color.FromArgb(150, 156, 162), TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(g, Copyright, ff, new Rectangle(0, fy + Theme.Px(18), Width, Theme.Px(18)), Color.FromArgb(120, 126, 132), TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            }
            // logotipo grande
            int ring = Theme.Px(64);
            int tw1 = TextRenderer.MeasureText(g, "Select", _fLogo, Size.Empty, TextFormatFlags.NoPadding).Width, tw2 = TextRenderer.MeasureText(g, "OR", _fLogo, Size.Empty, TextFormatFlags.NoPadding).Width;
            int total = ring + Theme.Px(14) + tw1 + tw2, lx = card.X + (card.Width - total) / 2, ly = card.Y + Theme.Px(40);
            SelectorLogo.DrawRing(g, new Rectangle(lx, ly, ring, ring));
            g.SmoothingMode = SmoothingMode.None;
            int th = _fLogo.Height, tx = lx + ring + Theme.Px(14), ty = ly + (ring - th) / 2;
            TextRenderer.DrawText(g, "Select", _fLogo, new Point(tx, ty), Theme.Text, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, "OR", _fLogo, new Point(tx + tw1, ty), Theme.Accent, TextFormatFlags.NoPadding);
            int y = ly + ring + Theme.Px(28), iw = card.Width - Theme.Px(60), ix = card.X + Theme.Px(30);
            TextRenderer.DrawText(g, Title, _fT, new Rectangle(ix, y, iw, _fT.Height + 4), Color.White, C);
            y += _fT.Height + Theme.Px(10);
            int sh = TextRenderer.MeasureText(g, Sub, _fS, new Size(iw, 1000), C).Height;
            TextRenderer.DrawText(g, Sub, _fS, new Rectangle(ix, y, iw, sh), Theme.Subtle, C);
            y += sh + Theme.Px(18);
            // pastilla con la flecha hacia la lista
            if (!string.IsNullOrEmpty(Arrow))
            {
                int aw = TextRenderer.MeasureText(g, Arrow, _fA, Size.Empty, TextFormatFlags.NoPadding).Width + Theme.Px(28), ah = Theme.Px(32);
                var ar = new Rectangle(card.X + (card.Width - aw) / 2, y, aw, ah);
                g.SmoothingMode = SmoothingMode.AntiAlias; Theme.FillRound(g, ar, ah / 2, Theme.Accent); g.SmoothingMode = SmoothingMode.None;
                TextRenderer.DrawText(g, Arrow, _fA, ar, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                y += ah + Theme.Px(14);
            }
            // cifras del contenido
            if (Chips.Count > 0)
            {
                var ws = new int[Chips.Count]; int sum = 0;
                for (int k = 0; k < Chips.Count; k++) { ws[k] = ColorText.Measure(g, Chips[k], _fC, 1000).Width + Theme.Px(22); sum += ws[k]; }
                int cx = card.X + (card.Width - (sum + Theme.Px(8) * (ws.Length - 1))) / 2, chH = Theme.Px(28);
                for (int k = 0; k < Chips.Count; k++)
                {
                    var r = new Rectangle(cx, y, ws[k], chH);
                    g.SmoothingMode = SmoothingMode.AntiAlias; Theme.FillRound(g, r, chH / 2, Color.FromArgb(120, 0, 0, 0)); g.SmoothingMode = SmoothingMode.None;
                    int h = ColorText.Measure(g, Chips[k], _fC, 1000).Height;
                    ColorText.Draw(g, Chips[k], _fC, new Rectangle(r.X + Theme.Px(11), r.Y + (chH - h) / 2, r.Width, h + 2), Theme.Text);
                    cx += ws[k] + Theme.Px(8);
                }
            }
        }
    }

    // ------------------------------------------------------------------ «Empezar a conducir» + cifras
    public class RouteGoPanel : Control
    {
        public sealed class Go { public string Icon, Title, Sub, Count; public Color Tint; public int Page; }
        public List<Go> Items = new List<Go>();
        public List<(string cap, string val, string sub)> Stats = new List<(string, string, string)>();
        public string Caption = "EMPEZAR A CONDUCIR";
        public event Action<int> GoTo;
        readonly Font _fCap = Theme.Font(8f, FontStyle.Bold), _fT = Theme.Font(10.5f, FontStyle.Bold), _fS = Theme.Font(8.5f), _fN = Theme.Font(16f, FontStyle.Bold),
                      _fSc = Theme.Font(7.5f, FontStyle.Bold), _fSv = Theme.Font(12.5f, FontStyle.Bold), _fSs = Theme.Font(7.75f), _fIc = EmojiPicker.EmojiFont(16f);
        readonly List<Rectangle> _rects = new List<Rectangle>();
        int _hover = -1;
        public RouteGoPanel() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); }
        protected override void Dispose(bool disposing) { if (disposing) foreach (var f in new[] { _fCap, _fT, _fS, _fN, _fSc, _fSv, _fSs, _fIc }) f.Dispose(); base.Dispose(disposing); }
        const TextFormatFlags L = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent?.BackColor ?? Theme.Bg);
            _rects.Clear();
            int w = Width - 1, y = 0;
            TextRenderer.DrawText(g, Caption, _fCap, new Rectangle(0, y, w, Theme.Px(18)), Theme.Subtle, L);
            y += Theme.Px(24);
            var icons = new List<(string, Rectangle)>();
            for (int i = 0; i < Items.Count; i++)
            {
                var it = Items[i];
                var r = new Rectangle(0, y, w, Theme.Px(66));
                _rects.Add(r);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Theme.FillRound(g, r, Theme.Px(12), i == _hover ? Color.FromArgb(60, 64, 68) : Color.FromArgb(52, 56, 60));
                if (i == _hover) Theme.DrawRoundBorder(g, r, Theme.Px(12), Color.FromArgb(120, it.Tint), 1.2f);
                var ic = new Rectangle(r.X + Theme.Px(12), r.Y + (r.Height - Theme.Px(42)) / 2, Theme.Px(42), Theme.Px(42));
                Theme.FillRound(g, ic, Theme.Px(11), Color.FromArgb(46, it.Tint));
                g.SmoothingMode = SmoothingMode.None;
                icons.Add((it.Icon, ic));
                int nw = TextRenderer.MeasureText(it.Count ?? "", _fN, Size.Empty, TextFormatFlags.NoPadding).Width + Theme.Px(6);
                TextRenderer.DrawText(g, it.Count ?? "", _fN, new Rectangle(r.Right - nw - Theme.Px(12), r.Y, nw, r.Height), it.Tint, L | TextFormatFlags.Right);
                int tx = ic.Right + Theme.Px(12), tw = r.Right - nw - Theme.Px(18) - tx;
                TextRenderer.DrawText(g, it.Title, _fT, new Rectangle(tx, r.Y + Theme.Px(13), tw, Theme.Px(20)), Theme.Text, L);
                TextRenderer.DrawText(g, it.Sub, _fS, new Rectangle(tx, r.Y + Theme.Px(34), tw, Theme.Px(18)), Theme.Subtle, L);
                y += r.Height + Theme.Px(8);
            }
            ColorText.DrawCells(g, icons, _fIc, Theme.Text, ClientRectangle);
            y += Theme.Px(4);
            int sw = (w - Theme.Px(8)) / 2, sh = Theme.Px(62);
            for (int i = 0; i < Stats.Count; i++)
            {
                var (cap, val, sub) = Stats[i];
                var r = new Rectangle((i % 2) * (sw + Theme.Px(8)), y + (i / 2) * (sh + Theme.Px(8)), sw, sh);
                g.SmoothingMode = SmoothingMode.AntiAlias; Theme.FillRound(g, r, Theme.Px(10), Color.FromArgb(52, 56, 60)); g.SmoothingMode = SmoothingMode.None;
                int x = r.X + Theme.Px(12), tw = r.Width - Theme.Px(20);
                TextRenderer.DrawText(g, cap, _fSc, new Rectangle(x, r.Y + Theme.Px(8), tw, Theme.Px(14)), Theme.Subtle, L);
                TextRenderer.DrawText(g, val, _fSv, new Rectangle(x, r.Y + Theme.Px(22), tw, Theme.Px(22)), Theme.Text, L);
                TextRenderer.DrawText(g, sub, _fSs, new Rectangle(x, r.Y + Theme.Px(43), tw, Theme.Px(14)), Theme.Subtle, L);
            }
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int h = _rects.FindIndex(r => r.Contains(e.Location));
            if (h != _hover) { _hover = h; Cursor = h >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
        }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (_hover >= 0) { _hover = -1; Invalidate(); } }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); int i = _rects.FindIndex(r => r.Contains(e.Location)); if (i >= 0 && i < Items.Count) GoTo?.Invoke(Items[i].Page); }
    }

    // ------------------------------------------------------------------ ACTIVIDADES
    public sealed class ActMeta
    {
        public string Start = "", Duration = "", Consist = "", From = "", To = "", LocoPath = "", PathFile = "";
        public int DurMin = -1, Diff = -1;                 // 0 fácil · 1 media · 2 difícil
        public string SeasonKind = "", WeatherKind = "";   // claves de Glyphs (spring…, clear/snow/rain)
    }

    public class ActivityCardGrid : CardListBase
    {
        public Func<object, ActMeta> MetaOf;
        public string[] DiffNames = { "FÁCIL", "MEDIA", "DIFÍCIL" };
        public Func<string, string> SeasonName = k => k, WeatherName = k => k;
        public static readonly Color[] DiffColors = { Color.FromArgb(102, 197, 106), Color.FromArgb(240, 196, 90), Color.FromArgb(229, 115, 115) };
        readonly Font _fT = Theme.Font(10.5f, FontStyle.Bold), _fM = Theme.Font(8.75f), _fMB = Theme.Font(8.75f, FontStyle.Bold), _fP = Theme.Font(7.5f, FontStyle.Bold), _fC = Theme.Font(8f);
        public ActivityCardGrid() { BackColor = Theme.Bg; }
        protected override void Dispose(bool disposing) { if (disposing) foreach (var f in new[] { _fT, _fM, _fMB, _fP, _fC }) f.Dispose(); base.Dispose(disposing); }
        protected override int MinCardW => Theme.Px(290);
        protected override int MaxCols => 3;
        protected override int CardH => Theme.Px(112);

        protected override void PaintCard(Graphics g, int i, Rectangle rc, bool sel, bool hov)
        {
            var it = Items[i]; var m = MetaOf?.Invoke(it) ?? new ActMeta();
            int rad = Theme.Px(12);
            Fill(g, rc, rad, sel ? Color.FromArgb(48, 60, 52) : hov ? Color.FromArgb(58, 62, 66) : Color.FromArgb(52, 56, 60));
            if (sel) Stroke(g, rc, rad, Theme.Accent);
            else if (ShowFocus && i == SelectedIndex) Stroke(g, rc, rad, Theme.AccentHi, 1f);
            int x = rc.X + Theme.Px(14), right = rc.Right - Theme.Px(12), y = rc.Y + Theme.Px(12);
            // dificultad
            int pw = 0;
            if (m.Diff >= 0 && m.Diff <= 2)
            {
                string d = DiffNames[m.Diff];
                pw = TW(d, _fP) + Theme.Px(14);
                var pr = new Rectangle(right - pw, y, pw, Theme.Px(19));
                Fill(g, pr, Theme.Px(6), Color.FromArgb(44, DiffColors[m.Diff]));
                TextRenderer.DrawText(g, d, _fP, pr, DiffColors[m.Diff], C1);
            }
            TextRenderer.DrawText(g, it.ToString(), _fT, new Rectangle(x, y - 1, right - x - pw - Theme.Px(8), Theme.Px(22)), Theme.Text, L1);
            y += Theme.Px(28);
            // hora · duración · estación y clima
            int mx = x;
            void Seg(string glyph, string text, bool bold)
            {
                if (string.IsNullOrEmpty(text) || mx >= right) return;
                int gs = Theme.Px(15);
                if (glyph != null) { Glyphs.Draw(g, glyph, new Rectangle(mx, y + Theme.Px(1), gs, gs), Theme.Subtle); g.SmoothingMode = SmoothingMode.None; mx += gs + Theme.Px(5); }
                var f = bold ? _fMB : _fM;
                int w = Math.Min(TW(text, f), right - mx);
                TextRenderer.DrawText(g, text, f, new Rectangle(mx, y, w + 2, Theme.Px(18)), bold ? Theme.Text : Theme.Subtle, L1);
                mx += w + Theme.Px(14);
            }
            Seg("clock", m.Start, true);
            Seg(null, m.Duration.Length > 0 ? "⏱ " + m.Duration : "", true);
            if (m.SeasonKind.Length > 0) Seg(m.SeasonKind, SeasonName(m.SeasonKind) + (m.WeatherKind.Length > 0 ? " · " + WeatherName(m.WeatherKind) : ""), false);
            y += Theme.Px(28);
            // tren y recorrido
            int cx = x;
            if (m.Consist.Length > 0) Pill(g, ref cx, y, right, m.Consist, _fC, Theme.Subtle, CardPaint.Rail, Theme.Px(20));
            if (m.From.Length > 0) Pill(g, ref cx, y, right, m.From + (m.To.Length > 0 && m.To != m.From ? " → " + m.To : ""), _fC, Theme.Subtle, CardPaint.Rail, Theme.Px(20));
        }
    }

    // Ficha de la actividad elegida: vista 2D del tren, seis datos y el resumen en párrafos.
    public class ActivityDetail : CanvasPanel
    {
        public string Caption = "ACTIVIDAD ELEGIDA", Title = "", Sub = "", SummaryCap = "RESUMEN", Empty = "Elige una actividad";
        public List<(string cap, string val, Color col)> Facts = new List<(string, string, Color)>();
        public List<string> Paragraphs = new List<string>();
        public string LocoPath;
        public VehicleThumbs Thumbs;
        readonly ThumbMemory _mem = new ThumbMemory(20);
        Font _fCap, _fT, _fS, _fFc, _fFv, _fP;
        public ActivityDetail()
        {
            _fCap = F(8f, FontStyle.Bold); _fT = F(14f, FontStyle.Bold); _fS = F(8.75f); _fFc = F(7.5f, FontStyle.Bold); _fFv = F(10f, FontStyle.Bold); _fP = F(9.5f);
            BackColor = Theme.Surface;
        }
        protected override void Dispose(bool disposing) { if (disposing) _mem.Dispose(); base.Dispose(disposing); }

        int ParaH(string p, int w) => TextRenderer.MeasureText(p, _fP, new Size(w, 100000), Wrap).Height;

        protected override int DoLayout(int w)
        {
            int pad = Theme.Px(14), iw = w - pad * 2;
            int y = pad + Theme.Px(18) + Theme.Px(30) + Theme.Px(20) + Theme.Px(10) + Theme.Px(62) + Theme.Px(12);
            y += ((Facts.Count + 2) / 3) * Theme.Px(50) + Theme.Px(8);
            y += Theme.Px(22);
            foreach (var p in Paragraphs) y += ParaH(p, iw - Theme.Px(24)) + Theme.Px(8);
            return y + Theme.Px(24) + pad;
        }

        protected override void DoPaint(Graphics g, int top)
        {
            int pad = Theme.Px(14), w = W - pad * 2, x = pad, y = pad - top;
            const TextFormatFlags L = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
            Text(g, Caption, _fCap, new Rectangle(x, y, w, Theme.Px(16)), Theme.Subtle, L);
            y += Theme.Px(18);
            if (string.IsNullOrEmpty(Title)) { Text(g, Empty, _fS, new Rectangle(x, y, w, Theme.Px(24)), Theme.Subtle, L); return; }
            Text(g, Title, _fT, new Rectangle(x, y, w, Theme.Px(28)), Theme.Text, L);
            y += Theme.Px(30);
            Text(g, Sub, _fS, new Rectangle(x, y, w, Theme.Px(18)), Theme.Subtle, L);
            y += Theme.Px(30);
            // vista 2D del tren
            var rail = new Rectangle(x, y, w, Theme.Px(62));
            CardPaint.Rail_(g, rail, _mem.Get(LocoPath), _mem.Failed(LocoPath) ? "🚆" : null, _fS);
            if (!string.IsNullOrEmpty(LocoPath)) _mem.Request(Thumbs, LocoPath, Theme.Px(50), w - Theme.Px(20), Invalidate, k => k == LocoPath);
            y += rail.Height + Theme.Px(12);
            // datos
            int fw = (w - Theme.Px(12)) / 3, fh = Theme.Px(44);
            for (int i = 0; i < Facts.Count; i++)
            {
                var (cap, val, col) = Facts[i];
                var r = new Rectangle(x + (i % 3) * (fw + Theme.Px(6)), y + (i / 3) * (fh + Theme.Px(6)), fw, fh);
                Round(g, r, Theme.Px(9), CardC);
                Text(g, cap, _fFc, new Rectangle(r.X + Theme.Px(10), r.Y + Theme.Px(6), r.Width - Theme.Px(14), Theme.Px(14)), Theme.Subtle, L);
                Text(g, val, _fFv, new Rectangle(r.X + Theme.Px(10), r.Y + Theme.Px(20), r.Width - Theme.Px(14), Theme.Px(20)), col, L);
            }
            y += ((Facts.Count + 2) / 3) * (fh + Theme.Px(6)) + Theme.Px(8);
            if (Paragraphs.Count == 0) return;
            Text(g, SummaryCap, _fCap, new Rectangle(x, y, w, Theme.Px(16)), Theme.Subtle, L);
            y += Theme.Px(22);
            int bh = Theme.Px(12); foreach (var p in Paragraphs) bh += ParaH(p, w - Theme.Px(24)) + Theme.Px(8);
            Round(g, new Rectangle(x, y, w, bh + Theme.Px(4)), Theme.Px(10), CardC);
            int py = y + Theme.Px(12);
            foreach (var p in Paragraphs)
            {
                int h = ParaH(p, w - Theme.Px(24));
                TextRenderer.DrawText(g, p, _fP, new Rectangle(x + Theme.Px(12), py, w - Theme.Px(24), h), Color.FromArgb(214, 218, 220), Wrap);
                py += h + Theme.Px(8);
            }
        }

        public void SetContent(string title, string sub, string loco, List<(string, string, Color)> facts, List<string> paras)
        {
            Title = title ?? ""; Sub = sub ?? ""; LocoPath = loco; Facts = facts ?? new(); Paragraphs = paras ?? new();
            AutoScrollPosition = new Point(0, 0);
            Relayout(true);
        }
    }
}
