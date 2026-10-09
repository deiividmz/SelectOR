// Trenes en LISTA con su composición 2D entera (Compra → Comprar, Compra → Solicitudes y Flota).
//  · StripPaint.Draw: dibuja la composición en un hueco dado, sin el aire de alrededor. En una línea, ajustada a ella;
//    si se dejan más renglones y el tren es largo, se reparte (como un texto) cortando entre vehículo y vehículo.
//  · TrainRowList: una línea por tren: nombre y datos, la composición ajustada a la línea, el precio y el botón «i»
//    (InfoClicked: el desglose). La imagen es la del .con (MainMenuForm la dibuja) o la guardada (.jpg/.png).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.CompilerServices;
using System.Windows.Forms;

namespace SelectOR
{
    static class StripPaint
    {
        public const int ImageH = 112;   // alto con el que se guardan en memoria (el de la caché de disco)
        sealed class Profile { public int[] Cols; public int Top, Bottom, Left, Right; }
        static readonly ConditionalWeakTable<Bitmap, Profile> _ink = new();

        // Cuánto dibujo hay en cada columna (para cortar entre vehículos, no por la mitad de uno) y dónde empieza y acaba
        // el dibujo en vertical (la imagen guarda aire encima de los vehículos: no se dibuja). Es «dibujo» lo que se
        // distingue del fondo (el color de la esquina, o transparente); las filas que lo son de lado a lado (el raíl de
        // la propia imagen) cuentan para el alto, pero no para elegir dónde cortar.
        static Profile Ink(Bitmap b)
        {
            if (_ink.TryGetValue(b, out var p)) return p;
            int W = b.Width, H = b.Height;
            p = new Profile { Cols = new int[W], Top = 0, Bottom = H - 1, Left = 0, Right = W - 1 };
            try
            {
                var d = b.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                var px = new byte[d.Stride * H];
                try { System.Runtime.InteropServices.Marshal.Copy(d.Scan0, px, 0, px.Length); }
                finally { b.UnlockBits(d); }
                int stride = d.Stride;
                byte bb = px[0], bg = px[1], br = px[2], ba = px[3];
                bool Ink1(int i) => ba < 60 ? px[i + 3] > 60
                                            : px[i + 3] > 60 && Math.Abs(px[i] - bb) + Math.Abs(px[i + 1] - bg) + Math.Abs(px[i + 2] - br) > 40;
                var rowInk = new int[H];
                for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) if (Ink1(y * stride + x * 4)) rowInk[y]++;
                int top = -1, bottom = -1;
                for (int y = 0; y < H; y++)
                {
                    if (rowInk[y] == 0) continue;
                    if (top < 0) top = y; bottom = y;
                    if (rowInk[y] > W * 0.85) continue;   // el raíl
                    for (int x = 0; x < W; x++) if (Ink1(y * stride + x * 4)) p.Cols[x]++;
                }
                if (top >= 0) { p.Top = Math.Max(0, top - 1); p.Bottom = Math.Min(H - 1, bottom + 1); }
                int l = Array.FindIndex(p.Cols, c => c > 0), r = Array.FindLastIndex(p.Cols, c => c > 0);
                if (l >= 0 && r > l) { p.Left = Math.Max(0, l - 1); p.Right = Math.Min(W - 1, r + 1); }
            }
            catch { }
            _ink.AddOrUpdate(b, p);
            return p;
        }

        // Los tramos [desde, hasta) de cada renglón, dentro de [x0, x1).
        static List<(int a, int b)> Cuts(Bitmap img, int rows, int x0, int x1)
        {
            var l = new List<(int, int)>();
            if (rows <= 1) { l.Add((x0, x1)); return l; }
            var ink = Ink(img).Cols;
            int W = x1 - x0, prev = x0, win = Math.Max(4, W / (rows * 6));
            for (int k = 1; k < rows; k++)
            {
                int ideal = x0 + W * k / rows, best = ideal, bestInk = int.MaxValue;
                for (int x = Math.Max(prev + 1, ideal - win); x < Math.Min(x1 - 1, ideal + win); x++)
                {
                    int v = ink[x] * 1000 + Math.Abs(x - ideal);   // primero lo que menos dibujo tenga; a igualdad, lo más cerca
                    if (v < bestInk) { bestInk = v; best = x; }
                }
                l.Add((prev, best)); prev = best;
            }
            l.Add((prev, x1));
            return l;
        }

        // Escala con la que se verá en «rows» renglones dentro de w × h.
        static double Scale(int imgW, int imgH, int w, int h, int rows, int gap)
        {
            double byH = (h - gap * (rows - 1)) / (double)rows / imgH;
            double byW = w * rows / (double)imgW;
            return Math.Min(byH, byW * 0.97);
        }

        // maxRows: 1 = ajustada a la línea. left: pegada a la izquierda (listas) en vez de centrada.
        public static void Draw(Graphics g, Rectangle r, Bitmap img, string empty, Font f, int maxRows = 3, bool left = false)
        {
            CardPaint.FillRound(g, r, Theme.Px(6), CardPaint.Rail);
            if (img == null)
            {
                if (!string.IsNullOrEmpty(empty)) TextRenderer.DrawText(g, empty, f, r, Theme.Subtle, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                return;
            }
            var area = Rectangle.Inflate(r, -Theme.Px(maxRows == 1 ? 8 : 10), -Theme.Px(maxRows == 1 ? 3 : 6));
            var prof = Ink(img);
            int top = prof.Top, ih = prof.Bottom - prof.Top + 1, x0 = prof.Left, x1 = prof.Right + 1, iw = x1 - x0;
            int gap = Theme.Px(8), rows = 1;
            double s = Scale(iw, ih, area.Width, area.Height, 1, gap);
            for (int k = 2; k <= maxRows; k++) { double sk = Scale(iw, ih, area.Width, area.Height, k, gap); if (sk > s * 1.12) { s = sk; rows = k; } }
            s = Math.Min(s, 2.0);   // un tren corto no se agranda de más
            int rowH = (int)Math.Round(ih * s);
            int total = rows * rowH + (rows - 1) * gap, y = area.Y + (area.Height - total) / 2;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            using var rail = new Pen(CardPaint.RailLine, Math.Max(1, Theme.Px(2)));
            foreach (var (a, b) in Cuts(img, rows, x0, x1))
            {
                int w = (int)Math.Round((b - a) * s);
                int x = rows == 1 && !left ? area.X + (area.Width - w) / 2 : area.X;
                g.DrawLine(rail, area.X, y + rowH - Theme.Px(2), area.Right, y + rowH - Theme.Px(2));
                g.DrawImage(img, new Rectangle(x, y, w, rowH), new Rectangle(a, top, b - a, ih), GraphicsUnit.Pixel);
                y += rowH + gap;
            }
            g.PixelOffsetMode = PixelOffsetMode.Default;
        }

        // El botón «i» (información: el desglose del precio).
        public static void InfoButton(Graphics g, Rectangle r, bool hover)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var b = new SolidBrush(hover ? Theme.Accent : Color.FromArgb(70, 76, 82))) g.FillEllipse(b, r);
            g.SmoothingMode = SmoothingMode.None;
            using var f = Theme.Font(10f, FontStyle.Bold);
            TextRenderer.DrawText(g, "i", f, r, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    // La composición de un tren en un recuadro (ventana de compra): entera, sin aire y en una sola línea.
    public class StripBox : Control
    {
        public Bitmap Image;
        public StripBox() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true); BackColor = Theme.Bg; }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            using var f = Theme.Font(9f);
            StripPaint.Draw(e.Graphics, ClientRectangle, Image, "🚆", f, maxRows: 1);
        }
    }

    // Etiqueta de una sola línea (con «…» si no cabe): el nombre del tren elegido.
    public class OneLineLabel : Label
    {
        public OneLineLabel() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ForeColor,
                TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        }
    }

    public class TrainRowList : CardListBase
    {
        public Func<object, string> TitleOf, SubOf, PathOf, RightOf, BadgeOf;
        public Func<object, List<(string text, Color fg, Color bg)>> PillsOf;
        public event Action<object> InfoClicked;          // el botón «i» de una línea (sin él, no se dibuja)
        public VehicleThumbs Thumbs;
        public string Empty2D = "🚆";
        readonly ThumbMemory _mem = new ThumbMemory(60);
        readonly Font _fT = Theme.Font(9.75f, FontStyle.Bold), _fS = Theme.Font(8.25f), _fP = Theme.Font(7.5f, FontStyle.Bold), _fR = Theme.Font(10f, FontStyle.Bold);
        Point _mouse = new Point(-1, -1);

        public TrainRowList() { BackColor = Theme.Bg; }
        protected override void Dispose(bool disposing) { if (disposing) { _mem.Dispose(); foreach (var f in new[] { _fT, _fS, _fP, _fR }) f.Dispose(); } base.Dispose(disposing); }
        protected override int MinCardW => Theme.Px(300);
        protected override int MaxCols => 1;
        protected override int CardH => Theme.Px(62);
        protected override int Gap => Theme.Px(6);

        int TextW(Rectangle rc) => Math.Max(Theme.Px(220), Math.Min(Theme.Px(400), (int)(rc.Width * 0.30)));
        Rectangle InfoRect(Rectangle rc) => new Rectangle(rc.Right - Theme.Px(12) - Theme.Px(26), rc.Y + (rc.Height - Theme.Px(26)) / 2, Theme.Px(26), Theme.Px(26));

        protected override void PaintCard(Graphics g, int i, Rectangle rc, bool sel, bool hov)
        {
            var it = Items[i];
            int rad = Theme.Px(10);
            Fill(g, rc, rad, sel ? CardPaint.Sel : hov ? Color.FromArgb(58, 62, 66) : Color.FromArgb(48, 52, 56));
            if (sel) Stroke(g, rc, rad, Theme.Accent);
            else if (ShowFocus && i == SelectedIndex) Stroke(g, rc, rad, Theme.AccentHi, 1f);
            int pad = Theme.Px(12), x = rc.X + pad, right = rc.Right - pad;
            // el botón «i»
            if (InfoClicked != null)
            {
                var ir = InfoRect(rc);
                StripPaint.InfoButton(g, ir, ir.Contains(_mouse));
                right = ir.X - Theme.Px(10);
            }
            // el precio y el distintivo (a la derecha, uno encima del otro)
            string price = RightOf?.Invoke(it), badge = BadgeOf?.Invoke(it);
            int rw = Math.Max(string.IsNullOrEmpty(price) ? 0 : TW(price, _fR) + Theme.Px(4), string.IsNullOrEmpty(badge) ? 0 : TW(badge, _fP) + Theme.Px(16));
            if (rw > 0)
            {
                int ry = rc.Y + Theme.Px(string.IsNullOrEmpty(badge) ? 19 : 8);
                if (!string.IsNullOrEmpty(price)) TextRenderer.DrawText(g, price, _fR, new Rectangle(right - rw, ry, rw, Theme.Px(22)), Theme.Accent, L1 | TextFormatFlags.Right);
                if (!string.IsNullOrEmpty(badge))
                {
                    int bw = TW(badge, _fP) + Theme.Px(16);
                    var br = new Rectangle(right - bw, rc.Y + Theme.Px(34), bw, Theme.Px(18));
                    Fill(g, br, Theme.Px(9), Color.FromArgb(40, 76, 175, 80));
                    TextRenderer.DrawText(g, badge, _fP, br, Theme.AccentHi, C1);
                }
                right -= rw + Theme.Px(12);
            }
            // nombre y, debajo, distintivos y datos
            int tw = TextW(rc);
            string title = TitleOf?.Invoke(it) ?? it?.ToString() ?? "";
            TextRenderer.DrawText(g, title, _fT, new Rectangle(x, rc.Y + Theme.Px(8), tw, Theme.Px(22)), Theme.Text, L1);
            int cx = x, cy = rc.Y + Theme.Px(33);
            var pills = PillsOf?.Invoke(it);
            if (pills != null) foreach (var p in pills) Pill(g, ref cx, cy, x + tw, p.text, _fP, p.fg, p.bg, Theme.Px(18));
            string sub = SubOf?.Invoke(it);
            if (!string.IsNullOrEmpty(sub) && cx < x + tw - Theme.Px(30))
                TextRenderer.DrawText(g, sub, _fS, new Rectangle(cx + (cx > x ? Theme.Px(2) : 0), cy, x + tw - cx, Theme.Px(18)), Theme.Subtle, L1);
            // la composición, ajustada a la línea
            string path = PathOf?.Invoke(it) ?? "";
            var strip = new Rectangle(x + tw + Theme.Px(12), rc.Y + Theme.Px(6), Math.Max(Theme.Px(40), right - (x + tw + Theme.Px(12))), rc.Height - Theme.Px(12));
            StripPaint.Draw(g, strip, _mem.Get(path), _mem.Failed(path) || path.Length == 0 ? Empty2D : null, _fS, maxRows: 1, left: true);
        }

        protected override void AfterPaint(Graphics g, int first, int last)
        {
            var visible = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int pre = Math.Min(Items.Count - 1, last + 4);
            for (int i = first; i <= pre; i++) visible.Add(PathOf?.Invoke(Items[i]) ?? "");
            for (int i = first; i <= pre; i++)
            {
                string p = PathOf?.Invoke(Items[i]) ?? "";
                if (p.Length > 0) _mem.Request(Thumbs, p, StripPaint.ImageH, 0, Invalidate, k => visible.Contains(k));
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool was = _mouse.X >= 0; _mouse = e.Location;
            if (InfoClicked != null) { int i = IndexAt(e.Location); if (i >= 0 && InfoRect(CardRect(i)).Contains(e.Location)) Cursor = Cursors.Hand; Invalidate(); }
        }
        protected override void OnMouseLeave(EventArgs e) { _mouse = new Point(-1, -1); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            int i = IndexAt(e.Location);
            if (InfoClicked != null && i >= 0 && e.Button == MouseButtons.Left && InfoRect(CardRect(i)).Contains(e.Location))
            {
                Focus(); SelectedIndex = i;
                InfoClicked(Items[i]);
                return;
            }
            base.OnMouseDown(e);
        }

        // La imagen de una línea ha cambiado (p. ej. ya se tiene el .con): se vuelve a pedir.
        public void Forget(string path) { _mem.Forget(path); Invalidate(); }
    }
}
